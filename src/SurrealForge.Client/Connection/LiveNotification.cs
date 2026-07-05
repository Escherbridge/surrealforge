// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client.Connection -- live-query notification model
// (surreal-linq-graph-query Phase 5). A LIVE SELECT subscription streams one
// of these per change to a matching record over the WebSocket RPC transport.

#nullable enable

namespace SurrealForge.Client.Connection
{
    /// <summary>The kind of change a live notification represents.</summary>
    public enum LiveAction
    {
        /// <summary>A row matching the live query was created.</summary>
        Create,
        /// <summary>A matching row was updated.</summary>
        Update,
        /// <summary>A matching row was deleted (the record carries its last state).</summary>
        Delete,
    }

    /// <summary>
    /// One push notification from a <c>LIVE SELECT</c> subscription: the
    /// <see cref="Action"/> and the typed <see cref="Record"/> it applied to.
    /// </summary>
    public sealed class LiveNotification<T>
    {
        public LiveAction Action { get; }
        public T Record { get; }

        public LiveNotification(LiveAction action, T record)
        {
            Action = action;
            Record = record;
        }
    }
}
