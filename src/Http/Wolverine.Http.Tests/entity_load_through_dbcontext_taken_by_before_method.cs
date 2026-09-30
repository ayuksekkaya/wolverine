using Alba;
using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http.Tests.EfCoreOnly;
using Wolverine.Persistence;
using Xunit;

namespace Wolverine.Http.Tests;

// An HTTP endpoint's [Entity] parameters are built while the chain is constructed, before the endpoint's own
// Before/Load methods are added to it, and before any policy adds registered middleware. The EF Core transactional
// middleware sees the DbContext such middleware takes, so the [Entity] load has to as well, or the entity is tracked
// by a DbContext that nothing saves.
public class entity_load_through_dbcontext_taken_by_before_method
{
    private static async Task<IAlbaHost> buildHost()
    {
        var builder = WebApplication.CreateBuilder();

        // Registered first, so it is the default DbContext for BeforeLoadItem
        builder.Services.AddDbContextWithWolverineIntegration<BeforeLoadMainDbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));
        builder.Services.AddDbContextWithWolverineIntegration<BeforeLoadAdminDbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(Bug3353StorageActionEndpoint).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
            opts.UseEntityFrameworkCoreTransactions();
        });

        builder.Services.AddWolverineHttp();

        // The endpoint below is nested, so discovery never finds it; build its chain directly instead. Nothing else
        // is discovered either, since those endpoints need DbContexts this host does not register.
        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q => q.Excludes.WithCondition("everything", _ => true))));
    }

    private static string generate(IAlbaHost host, HttpChain chain)
    {
        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, host.Services);
        chain.SourceCode.ShouldNotBeNull();
        return chain.SourceCode;
    }

    [Fact]
    public async Task the_entity_is_loaded_through_the_dbcontext_the_before_method_takes()
    {
        await using var host = await buildHost();
        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;

        var chain = new HttpChain(new MethodCall(typeof(RenameBeforeLoadItemEndpoint), nameof(RenameBeforeLoadItemEndpoint.Post)), graph);
        var source = generate(host, chain);

        // The transaction saves the admin DbContext, so the item has to be loaded through it
        source.ShouldContain("beforeLoadAdminDbContext.FindAsync<");
        source.ShouldContain("beforeLoadAdminDbContext.SaveChangesAsync(");
        source.ShouldNotContain("beforeLoadMainDbContext");
    }

    [Fact]
    public async Task the_entity_is_loaded_through_the_dbcontext_registered_middleware_takes()
    {
        await using var host = await buildHost();
        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;

        var chain = new HttpChain(new MethodCall(typeof(RenameMiddlewareLoadItemEndpoint), nameof(RenameMiddlewareLoadItemEndpoint.Post)), graph);

        // What a middleware policy does, after the endpoint and its [Entity] load have been built
        chain.Middleware.Add(new MethodCall(typeof(BeforeLoadAdminMiddleware), nameof(BeforeLoadAdminMiddleware.Before)));

        var source = generate(host, chain);

        source.ShouldContain("beforeLoadAdminDbContext.FindAsync<");
        source.ShouldContain("beforeLoadAdminDbContext.SaveChangesAsync(");
        source.ShouldNotContain("beforeLoadMainDbContext");
    }

    public class BeforeLoadItem
    {
        public Guid Id { get; set; }
        public string Owner { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public class BeforeLoadMainDbContext(DbContextOptions<BeforeLoadMainDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BeforeLoadItem>().ToTable("before_load_items").HasQueryFilter(x => x.Owner == "alice");
        }
    }

    public class BeforeLoadAdminDbContext(DbContextOptions<BeforeLoadAdminDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BeforeLoadItem>().ToTable("before_load_items");
        }
    }

    public record RenameBeforeLoadItem(string Name);

    public static class BeforeLoadAdminMiddleware
    {
        public static void Before(BeforeLoadAdminDbContext db)
        {
        }
    }

    public static class RenameMiddlewareLoadItemEndpoint
    {
        [Transactional]
        [WolverinePost("/middleware-load-items/{id}")]
        public static void Post(RenameBeforeLoadItem command, [Entity] BeforeLoadItem item) => item.Name = command.Name;
    }

    public static class RenameBeforeLoadItemEndpoint
    {
        public static void Before(BeforeLoadAdminDbContext db)
        {
        }

        [Transactional]
        [WolverinePost("/before-load-items/{id}")]
        public static void Post(RenameBeforeLoadItem command, [Entity] BeforeLoadItem item) => item.Name = command.Name;
    }
}
