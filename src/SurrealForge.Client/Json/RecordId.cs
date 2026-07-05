using System;

namespace SurrealForge.Client.Json;

/// <summary>
/// A SurrealDB record identifier of the shape <c>&lt;table&gt;:&lt;id&gt;</c>
/// (e.g. <c>wallet:abc123</c>). The id part is opaque — UUID, ULID, numeric,
/// or any allowed SurrealQL identifier — and is preserved verbatim.
/// See <see href="https://surrealdb.com/docs/surrealql/datamodel/ids">SurrealDB
/// IDs</see> for the full grammar; this type intentionally does not validate
/// against the grammar (validation is the analyzer / identifier-allowlist
/// territory). It only enforces the single <c>:</c> separator.
/// </summary>
public readonly struct RecordId : IEquatable<RecordId>
{
    public string Table { get; }
    public string Id { get; }

    public RecordId(string table, string id)
    {
        if (string.IsNullOrEmpty(table))
            throw new ArgumentException("Table must not be empty.", nameof(table));
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("Id must not be empty.", nameof(id));
        if (table.IndexOf(':') >= 0)
            throw new ArgumentException("Table name must not contain ':'.", nameof(table));

        Table = table;
        Id    = id;
    }

    /// <summary>
    /// Parse the <c>table:id</c> form. Splits on the FIRST <c>:</c> so id
    /// values that themselves contain colons (e.g. URL fragments) survive.
    /// </summary>
    public static RecordId Parse(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var colon = value.IndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
        {
            throw new FormatException(
                $"RecordId must be of the form 'table:id'; got '{value}'.");
        }
        return new RecordId(value.Substring(0, colon), value.Substring(colon + 1));
    }

    public override string ToString() => Table + ":" + Id;

    public bool Equals(RecordId other) =>
        string.Equals(Table, other.Table, StringComparison.Ordinal) &&
        string.Equals(Id,    other.Id,    StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is RecordId other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return ((Table?.GetHashCode() ?? 0) * 397) ^ (Id?.GetHashCode() ?? 0);
        }
    }

    public static bool operator ==(RecordId left, RecordId right) => left.Equals(right);
    public static bool operator !=(RecordId left, RecordId right) => !left.Equals(right);
}
