// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client.Connection -- WebSocket (RPC) transport for LIVE
// queries (surreal-linq-graph-query Phase 5, decision D8). Lives ALONGSIDE
// HttpSurrealConnection; the HTTP /sql path is request/response and physically
// cannot carry `LIVE SELECT` push frames, so live subscriptions need the
// SurrealDB WebSocket RPC protocol (/rpc, JSON-RPC {id, method, params}).
//
// Protocol (SurrealDB 3.x):
//   signin [{user,pass}] -> auth          use [ns, db] -> scope
//   query ["LIVE SELECT ..."] -> result[0].result is the live-query UUID
//   notifications arrive UNSOLICITED as { id: <liveUuid>, action, result }
//   kill [uuid] -> stop the subscription
//
// A single background receive loop demuxes: frames with a pending request id
// complete that request's TaskCompletionSource; frames carrying a known live
// uuid are routed to that subscription's channel.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SurrealForge.Client.Json;

namespace SurrealForge.Client.Connection
{
    /// <summary>
    /// WebSocket RPC connection used only for live-query subscriptions. Open
    /// via <see cref="ConnectAsync"/>; subscribe via <see cref="LiveAsync{T}"/>;
    /// dispose to KILL all live queries and close the socket.
    /// </summary>
    public sealed class WebSocketSurrealConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private readonly SurrealConnectionOptions _options;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly ConcurrentDictionary<string, Channel<JsonElement>> _liveChannels = new();
        private readonly CancellationTokenSource _receiveCts = new();
        private Task? _receiveLoop;
        private int _rpcId;

        public WebSocketSurrealConnection(SurrealConnectionOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Connect the socket, sign in, and select the namespace/database from
        /// <see cref="SurrealConnectionOptions"/>. Derives the <c>ws(s)://…/rpc</c>
        /// URL from the configured HTTP <c>Endpoint</c>.
        /// </summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            var rpcUri = ToRpcUri(_options.Endpoint);
            await _ws.ConnectAsync(rpcUri, ct).ConfigureAwait(false);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));

            await RpcAsync("signin",
                new object[] { new { user = _options.User, pass = _options.Password } }, ct)
                .ConfigureAwait(false);
            await RpcAsync("use",
                new object[] { _options.Namespace, _options.Database }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Issue a <c>LIVE SELECT</c> for the supplied query and stream typed
        /// notifications until <paramref name="ct"/> cancels or the connection
        /// disposes. On teardown the live query is <c>KILL</c>ed.
        /// </summary>
        public async IAsyncEnumerable<LiveNotification<T>> LiveAsync<T>(
            Query.SurrealQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            query.Validate(strict: false);

            // The query carries `LIVE SELECT …`; params bind via the RPC `query`
            // method's second argument (the vars object).
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in query.Params) vars[kv.Key] = kv.Value;
            var result = await RpcAsync("query", new object[] { query.Sql, vars }, ct).ConfigureAwait(false);

            var liveId = ExtractLiveId(result);
            if (string.IsNullOrEmpty(liveId))
                throw new InvalidOperationException(
                    "LIVE SELECT did not return a live-query id. SQL: " + query.Sql);

            var channel = Channel.CreateUnbounded<JsonElement>();
            _liveChannels[liveId!] = channel;

            try
            {
                while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var frame))
                    {
                        if (TryParseNotification<T>(frame, out var note))
                            yield return note!;
                    }
                }
            }
            finally
            {
                _liveChannels.TryRemove(liveId!, out _);
                // Best-effort KILL; ignore failures during teardown.
                try { await RpcAsync("kill", new object[] { liveId! }, CancellationToken.None).ConfigureAwait(false); }
                catch { /* connection may already be closing */ }
            }
        }

        // ─── RPC plumbing ────────────────────────────────────────────────────

        private async Task<JsonElement> RpcAsync(string method, object[] @params, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _rpcId).ToString();
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new { id, method, @params }, SurrealJsonOptions.Default);
            await _ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text,
                endOfMessage: true, ct).ConfigureAwait(false);

            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();
            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    sb.Clear();
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (res.MessageType == WebSocketMessageType.Close) return;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                    }
                    while (!res.EndOfMessage);

                    Dispatch(sb.ToString());
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (WebSocketException) { /* socket dropped; pending awaiters cancel on dispose */ }
        }

        private void Dispatch(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();

            // RPC response: { id, result | error }. Live notification:
            // { id: <liveUuid>, action, result }. Disambiguate by whether the id
            // matches a pending request OR a known live channel.
            if (root.TryGetProperty("id", out var idEl))
            {
                var id = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText();
                if (id != null && _pending.TryRemove(id, out var tcs))
                {
                    if (root.TryGetProperty("error", out var err))
                        tcs.TrySetException(new InvalidOperationException(
                            "SurrealDB RPC error: " + err.GetRawText()));
                    else
                        tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
                    return;
                }
                if (id != null && _liveChannels.TryGetValue(id, out var liveChan))
                {
                    liveChan.Writer.TryWrite(root);
                    return;
                }
            }

            // Some 3.x builds wrap live frames under result: { id, action, result }.
            if (root.TryGetProperty("result", out var inner) && inner.ValueKind == JsonValueKind.Object
                && inner.TryGetProperty("id", out var liveIdEl))
            {
                var liveId = liveIdEl.GetString();
                if (liveId != null && _liveChannels.TryGetValue(liveId, out var chan))
                    chan.Writer.TryWrite(inner.Clone());
            }
        }

        // ─── frame parsing ───────────────────────────────────────────────────

        private static string? ExtractLiveId(JsonElement queryResult)
        {
            // query result is an array of per-statement results:
            // [{ status:"OK", result:"<uuid>" }]; the live id is result[0].result.
            if (queryResult.ValueKind == JsonValueKind.Array && queryResult.GetArrayLength() > 0)
            {
                var first = queryResult[0];
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("result", out var r))
                    return r.ValueKind == JsonValueKind.String ? r.GetString() : r.GetRawText().Trim('"');
                if (first.ValueKind == JsonValueKind.String) return first.GetString();
            }
            if (queryResult.ValueKind == JsonValueKind.String) return queryResult.GetString();
            return null;
        }

        private static bool TryParseNotification<T>(JsonElement frame, out LiveNotification<T>? note)
        {
            note = null;
            // Frame: { id:<liveUuid>, action:"CREATE|UPDATE|DELETE", result:{record} }.
            if (!frame.TryGetProperty("action", out var actionEl)) return false;
            var action = actionEl.GetString()?.ToUpperInvariant() switch
            {
                "CREATE" => LiveAction.Create,
                "UPDATE" => LiveAction.Update,
                "DELETE" => LiveAction.Delete,
                _ => (LiveAction?)null,
            };
            if (action is null) return false;
            if (!frame.TryGetProperty("result", out var recordEl)) return false;

            var record = recordEl.Deserialize<T>(SurrealJsonOptions.Default);
            if (record is null) return false;
            note = new LiveNotification<T>(action.Value, record);
            return true;
        }

        private static Uri ToRpcUri(string httpEndpoint)
        {
            var b = new UriBuilder(httpEndpoint);
            b.Scheme = b.Scheme == "https" ? "wss" : "ws";
            b.Path = b.Path.TrimEnd('/') + "/rpc";
            return b.Uri;
        }

        public async ValueTask DisposeAsync()
        {
            _receiveCts.Cancel();
            foreach (var chan in _liveChannels.Values) chan.Writer.TryComplete();
            _liveChannels.Clear();
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch { /* ignore */ }
            _ws.Dispose();
            _receiveCts.Dispose();
            if (_receiveLoop != null)
            {
                try { await _receiveLoop.ConfigureAwait(false); } catch { /* ignore */ }
            }
        }
    }
}
