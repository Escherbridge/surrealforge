// SPDX-License-Identifier: UNLICENSED
// SurqlEmitter OVERWRITE-mode tests -- the emit half of the fix. A changed
// field must render `DEFINE FIELD OVERWRITE ... TYPE <new>` (evolves in place)
// rather than `IF NOT EXISTS` (which never alters an existing field).

using FluentAssertions;
using SurrealForge.Schema.Generator;
using static SurrealForge.Schema.Tests.Migration.SchemaModelTestFactory;

namespace SurrealForge.Schema.Tests.Migration
{
    public class SurqlEmitterOverwriteTests
    {
        [Fact]
        public void Evolve_mode_emits_DEFINE_FIELD_OVERWRITE_with_new_type()
        {
            var attr = Field("scopes", "array<string>");
            var ddl = SurqlEmitter.EmitFieldStatement("api_key", attr, SurqlEmitter.EmitOptions.Evolve);

            ddl.Should().StartWith("DEFINE FIELD OVERWRITE scopes ON TABLE api_key TYPE array<string>");
            ddl.Should().NotContain("IF NOT EXISTS");
        }

        [Fact]
        public void Idempotent_mode_still_emits_IF_NOT_EXISTS()
        {
            var attr = Field("scopes", "array<string>");
            var ddl = SurqlEmitter.EmitFieldStatement("api_key", attr, SurqlEmitter.EmitOptions.Default);
            ddl.Should().Contain("DEFINE FIELD IF NOT EXISTS scopes");
            ddl.Should().NotContain("OVERWRITE");
        }

        [Fact]
        public void Strict_mode_emits_bare_DEFINE_FIELD()
        {
            var attr = Field("scopes", "array<string>");
            var ddl = SurqlEmitter.EmitFieldStatement("api_key", attr, SurqlEmitter.EmitOptions.Strict);
            ddl.Should().StartWith("DEFINE FIELD scopes ON TABLE api_key");
            ddl.Should().NotContain("IF NOT EXISTS");
            ddl.Should().NotContain("OVERWRITE");
        }

        [Fact]
        public void Evolve_mode_carries_default_and_assert_clauses()
        {
            var attr = Field("flag", "bool", Default("false"), Assert("$value != NONE"));
            var ddl = SurqlEmitter.EmitFieldStatement("t", attr, SurqlEmitter.EmitOptions.Evolve);
            ddl.Should().Contain("DEFINE FIELD OVERWRITE flag ON TABLE t TYPE bool");
            ddl.Should().Contain("DEFAULT false");
            ddl.Should().Contain("ASSERT $value != NONE");
        }

        [Fact]
        public void Evolve_mode_index_emits_OVERWRITE()
        {
            var idx = Index("t_a", true, "a");
            var ddl = SurqlEmitter.EmitIndexStatement("t", idx, SurqlEmitter.EmitOptions.Evolve);
            ddl.Should().Contain("DEFINE INDEX OVERWRITE t_a");
            ddl.Should().Contain("UNIQUE");
        }

        [Fact]
        public void Overwrite_wins_over_idempotent_when_both_set()
        {
            var opts = new SurqlEmitter.EmitOptions(idempotent: true, overwrite: true);
            var ddl = SurqlEmitter.EmitFieldStatement("t", Field("x", "string"), opts);
            ddl.Should().Contain("OVERWRITE");
            ddl.Should().NotContain("IF NOT EXISTS");
        }
    }
}
