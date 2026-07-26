// SPDX-License-Identifier: MIT
// AddSurrealVectorSearch registration smoke tests.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SurrealForge.Client.Schema;
using SurrealForge.Vector;

namespace SurrealForge.Vector.Tests;

public sealed class ServiceCollectionTests
{
    [Fact]
    public void AddSurrealVectorSearch_registers_options_cache_registry_and_hosted_service()
    {
        var encoder = new FakeVectorEncoder();
        var services = new ServiceCollection();
        services.AddSurrealVectorSearch(v => v.AddEncoder(encoder));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<SurrealVectorOptions>().Should().NotBeNull();
        provider.GetRequiredService<IEmbeddingCache>().Should().BeOfType<InMemoryEmbeddingCache>();

        var registry = provider.GetRequiredService<VectorEncoderRegistry>();
        registry.Resolve().Should().BeSameAs(encoder);
        registry.Resolve(SurrealVectorOptions.DefaultProfile).Should().BeSameAs(encoder);

        provider.GetServices<IHostedService>().Should().ContainSingle(s => s is EmbeddingBackfillService);
    }

    [Fact]
    public void A_consumer_supplied_cache_wins_over_the_default()
    {
        var services = new ServiceCollection();
        var custom = new InMemoryEmbeddingCache();
        services.AddSingleton<IEmbeddingCache>(custom);
        services.AddSurrealVectorSearch(v => v.AddEncoder(new FakeVectorEncoder()));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEmbeddingCache>().Should().BeSameAs(custom);
    }

    [Fact]
    public void Resolving_an_unknown_profile_names_the_known_ones()
    {
        var services = new ServiceCollection();
        services.AddSurrealVectorSearch(v => v.AddEncoder("mini", new FakeVectorEncoder()));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<VectorEncoderRegistry>();

        var act = () => registry.Resolve("missing");
        act.Should().Throw<InvalidOperationException>().WithMessage("*missing*mini*");
    }

    [Fact]
    public void Duplicate_encoder_profiles_are_rejected()
    {
        var options = new SurrealVectorOptions();
        options.AddEncoder("mini", new FakeVectorEncoder());

        var act = () => options.AddEncoder("mini", new FakeVectorEncoder());
        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    [SurrealTable("document")]
    public sealed class JobDoc
    {
        [Embedded("embedding", Mode = EmbeddingMode.Batched)]
        public string Body { get; set; } = string.Empty;

        [Embedded("title_vec", Mode = EmbeddingMode.WriteTime)]
        public string Title { get; set; } = string.Empty;
    }

    [Fact]
    public void AddJobsFrom_registers_a_backfill_job_per_batched_field_only()
    {
        var options = new SurrealVectorOptions();
        options.AddJobsFrom<JobDoc>();

        options.Jobs.Should().HaveCount(1);
        options.Jobs[0].Definition.JobName.Should().Be("document.embedding");
        options.Jobs[0].Incremental.Should().NotBeNull("incremental is the default job kind");
    }
}
