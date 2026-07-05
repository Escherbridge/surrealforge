// SPDX-License-Identifier: UNLICENSED
// RecordId<T> -- typed record-id wrapper. Tests cover:
//   * Implicit conversion to untyped RecordId preserves table + id
//   * Explicit conversion from untyped RecordId with matching schema succeeds
//   * Explicit conversion with mismatched schema throws InvalidCastException
//   * JSON round-trip preserves table and id

using System.Text.Json;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Json;

namespace SurrealForge.Client.Tests;

public class RecordIdGenericTests
{
    [Fact]
    public void Constructor_with_id_picks_table_from_T_SchemaName()
    {
        var typed = new RecordId<TestWallet>("abc123");
        typed.Table.Should().Be("wallet");
        typed.Id.Should().Be("abc123");
        typed.ToString().Should().Be("wallet:abc123");
    }

    [Fact]
    public void Implicit_conversion_to_RecordId_preserves_table_and_id()
    {
        var typed = new RecordId<TestWallet>("abc123");
        RecordId untyped = typed;

        untyped.Table.Should().Be("wallet");
        untyped.Id.Should().Be("abc123");
    }

    [Fact]
    public void Explicit_conversion_with_matching_schema_succeeds()
    {
        var untyped = new RecordId("wallet", "abc123");
        var typed = (RecordId<TestWallet>)untyped;

        typed.Table.Should().Be("wallet");
        typed.Id.Should().Be("abc123");
    }

    [Fact]
    public void Explicit_conversion_with_mismatched_schema_throws_InvalidCast()
    {
        var untyped = new RecordId("idempotency_key_store", "k1");

        var act = () => { var _ = (RecordId<TestWallet>)untyped; };
        act.Should().Throw<InvalidCastException>()
            .WithMessage("*idempotency_key_store*")
            .WithMessage("*wallet*");
    }

    [Fact]
    public void Equality_treats_two_typed_record_ids_with_same_table_and_id_as_equal()
    {
        var a = new RecordId<TestWallet>("abc");
        var b = new RecordId<TestWallet>("abc");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void AsUntyped_method_returns_a_RecordId_carrying_the_same_data()
    {
        var typed = new RecordId<TestWallet>("abc");
        var untyped = typed.AsUntyped();
        untyped.Table.Should().Be("wallet");
        untyped.Id.Should().Be("abc");
    }

    [Fact]
    public void SchemaNameOf_caches_value_across_calls()
    {
        // Confirm two calls return the same string instance from the cache.
        var first = RecordId<TestWallet>.SchemaNameOf<TestWallet>();
        var second = RecordId<TestWallet>.SchemaNameOf<TestWallet>();
        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void Json_roundtrip_via_implicit_untyped_form_preserves_table_and_id()
    {
        var typed = new RecordId<TestWallet>("abc123");
        // Convert to untyped for serialization since RecordIdJsonConverter is
        // registered for RecordId (not RecordId<T>); the typed form is a
        // compile-time-only pin that widens implicitly at the JSON boundary.
        RecordId untyped = typed;

        var json = JsonSerializer.Serialize(untyped, new JsonSerializerOptions
        {
            Converters = { new RecordIdJsonConverter() }
        });

        json.Should().Be("\"wallet:abc123\"");

        var roundTripped = JsonSerializer.Deserialize<RecordId>(json, new JsonSerializerOptions
        {
            Converters = { new RecordIdJsonConverter() }
        });
        roundTripped.Should().Be(new RecordId("wallet", "abc123"));

        // And the typed wrapper restores cleanly.
        var typedAgain = (RecordId<TestWallet>)roundTripped;
        typedAgain.Should().Be(typed);
    }

    /// <summary>Test-only ISurrealRecord stand-in for the wallet table.</summary>
    private sealed class TestWallet : ISurrealRecord
    {
        public string SchemaName => "wallet";
    }
}
