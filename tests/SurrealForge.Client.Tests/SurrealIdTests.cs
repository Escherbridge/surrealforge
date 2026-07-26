// SPDX-License-Identifier: MIT

using FluentAssertions;
using Xunit;

namespace SurrealForge.Client.Tests;

public sealed class SurrealIdTests
{
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("avatar:0123456789abcdef0123456789abcdef")]
    [InlineData("avatar:`0123456789abcdef0123456789abcdef`")]
    [InlineData("avatar:\"0123456789abcdef0123456789abcdef\"")]
    public void ParseRecordGuid_accepts_bare_linked_and_quoted_record_ids(string value)
    {
        SurrealId.ParseRecordGuid(value).Should().Be(
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));
    }
}
