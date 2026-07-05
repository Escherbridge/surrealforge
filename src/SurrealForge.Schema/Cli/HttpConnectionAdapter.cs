// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Schema -- HTTP `POST /sql` adapter for the CLI.
//
// Why this lives in the Schema package rather than Client:
//   The CLI is shipped from `SurrealForge.Schema.csproj` (PackAsTool). The
//   adapter is a *tiny* HTTP transport just sufficient to run schema
//   migrations against a stock SurrealDB endpoint. It is intentionally NOT a
//   replacement for `SurrealForge.Client.HttpSurrealConnection` (A1's
//   production transport with pooling, retries, transactions). When A1 lands,
//   the CLI can switch to that transport via a constructor swap — both
//   implement `ISurrealConnection`.
//
// Auth + scoping:
//   - HTTP Basic <user:pass>
//   - `Surreal-NS` + `Surreal-DB` headers
//   - Accept: application/json

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Schema.Migration;

namespace SurrealForge.Schema.Cli
{
    /// <summary>
    /// Thin HTTP <c>POST /sql</c> adapter implementing <see cref="ISurrealConnection"/>.
    /// CLI-scoped; production code should use the (forthcoming)
    /// <c>SurrealForge.Client.HttpSurrealConnection</c>.
    /// </summary>
    public sealed class HttpConnectionAdapter : ISurrealConnection, IDisposable
    {
        private readonly HttpClient _http;
        private readonly Uri _endpoint;
        private readonly bool _ownsHttp;
        private readonly string? _ns;
        private readonly string? _db;

        /// <summary>
        /// Build an adapter from a URL + credentials + namespace/database.
        /// </summary>
        public HttpConnectionAdapter(string url, string user, string pass, string ns, string db, HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("url is required", nameof(url));
            _ownsHttp = httpClient == null;
            _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            // Endpoint: <url>/sql
            var b = url.TrimEnd('/') + "/sql";
            _endpoint = new Uri(b, UriKind.Absolute);

            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes((user ?? string.Empty) + ":" + (pass ?? string.Empty)));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);

            // NS/DB attached per-call (not as defaults) so the namespace
            // bootstrap can skip them.
            _ns = string.IsNullOrWhiteSpace(ns) ? null : ns;
            _db = string.IsNullOrWhiteSpace(db) ? null : db;

            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public Task<SurrealExecutionResult> ExecuteAsync(string surql, CancellationToken ct = default)
            => ExecuteCoreAsync(surql, scoped: true, ct);

        public Task<SurrealExecutionResult> ExecuteUnscopedAsync(string surql, CancellationToken ct = default)
            => ExecuteCoreAsync(surql, scoped: false, ct);

        private async Task<SurrealExecutionResult> ExecuteCoreAsync(string surql, bool scoped, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(surql ?? string.Empty, Encoding.UTF8, "application/json"),
            };

            if (scoped)
            {
                if (_ns != null) req.Headers.Add("Surreal-NS", _ns);
                if (_db != null) req.Headers.Add("Surreal-DB", _db);
            }

            // SurrealDB's /sql expects raw SurQL with `application/json` accept;
            // text/plain on the request side is preferred but the server is
            // tolerant.
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return SurrealExecutionResult.Error("transport: " + ex.Message);
            }

            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return SurrealExecutionResult.Error($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}", body);
                }
                if (body != null && body.IndexOf("\"status\":\"ERR\"", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var snippet = body.Length > 2048 ? body.Substring(0, 2048) + "…(truncated)" : body;
                    return SurrealExecutionResult.Error("server reported statement error: " + snippet, body);
                }
                return SurrealExecutionResult.Ok(body);
            }
        }

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }
    }
}
