using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests.Bugs;

// An [Entity] load goes through a DbContext, but that dependency was invisible to the EF Core transactional
// middleware: the load frame is not a MethodCall, and EntityAttribute.Modify() only builds it at codegen, after
// AutoApplyTransactions (and, on handler chains, [Transactional]) had already decided the chain uses no
// DbContext. So a handler that only changed an [Entity] was never saved. And when two DbContexts map the same
// entity, [Entity] always loaded through the first one registered, even on a chain that designates the other
// with [Transactional(typeof(X))] or [Storage(typeof(X))], so the change was tracked by a DbContext that
// nothing saved. Returned storage actions (Storage.Update/Delete/...) had the same problem on the write side: they
// were applied to the first registered DbContext while the transactional middleware saved the designated one.
public class Bug_entity_load_dbcontext : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery()
                    .IncludeType(typeof(RenameEntityLoadNoteHandler))
                    .IncludeType(typeof(RenameEntityLoadItemHandler))
                    .IncludeType(typeof(AdminRenameEntityLoadItemHandler))
                    .IncludeType(typeof(StorageRenameEntityLoadItemHandler))
                    .IncludeType(typeof(AdminUpdateEntityLoadItemHandler))
                    .IncludeType(typeof(AdminDeleteEntityLoadItemHandler))
                    .IncludeType(typeof(InjectedAdminRenameEntityLoadItemHandler))
                    .IncludeType(typeof(AuditEntityLoadNoteHandler))
                    .IncludeType(typeof(CountAllEntityLoadItemsHandler))
                    .IncludeType(typeof(CountQueryableEntityLoadItemsHandler))
                    .IncludeType(typeof(FindFirstEntityLoadItemHandler));

                // Registered first, so it is the default DbContext for EntityLoadItem
                opts.Services.AddDbContextWithWolverineIntegration<EntityLoadMainDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.Services.AddDbContextWithWolverineIntegration<EntityLoadAdminDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.Services.AddDbContextWithWolverineIntegration<EntityLoadAuditDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await _host.ResetResourceState();

        await execute("""
            IF SCHEMA_ID('entity_load') IS NULL EXEC('CREATE SCHEMA entity_load');
            DROP TABLE IF EXISTS entity_load.items;
            DROP TABLE IF EXISTS entity_load.notes;
            DROP TABLE IF EXISTS entity_load.audit;
            CREATE TABLE entity_load.items (Id uniqueidentifier PRIMARY KEY, Owner nvarchar(50) NOT NULL, Name nvarchar(50) NOT NULL);
            CREATE TABLE entity_load.notes (Id uniqueidentifier PRIMARY KEY, Name nvarchar(50) NOT NULL);
            CREATE TABLE entity_load.audit (Id uniqueidentifier PRIMARY KEY, Text nvarchar(100) NOT NULL);
            """);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static async Task<object?> execute(string sql)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        return await new SqlCommand(sql, conn).ExecuteScalarAsync();
    }

    private static async Task<Guid> insertItem(string owner)
    {
        var id = Guid.NewGuid();
        await execute($"INSERT INTO entity_load.items (Id, Owner, Name) VALUES ('{id}', '{owner}', 'original')");
        return id;
    }

    private static async Task<string?> storedName(string table, Guid id)
    {
        return (string?)await execute($"SELECT Name FROM entity_load.{table} WHERE Id = '{id}'");
    }

    [Fact]
    public async Task a_handler_that_only_changes_an_entity_is_saved()
    {
        var id = Guid.NewGuid();
        await execute($"INSERT INTO entity_load.notes (Id, Name) VALUES ('{id}', 'original')");

        await _host.TrackActivity().Timeout(30.Seconds()).InvokeMessageAndWaitAsync(new RenameEntityLoadNote(id, "renamed"));

        (await storedName("notes", id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task an_entity_mapped_by_two_dbcontexts_is_saved_through_the_default_one()
    {
        var id = await insertItem("alice");

        await _host.TrackActivity().Timeout(30.Seconds()).InvokeMessageAndWaitAsync(new RenameEntityLoadItem(id, "renamed"));

        (await storedName("items", id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task an_entity_is_loaded_through_the_dbcontext_designated_by_transactional()
    {
        // Only the admin DbContext can see bob's item, so loading through the main DbContext would find nothing
        var id = await insertItem("bob");

        await _host.TrackActivity().Timeout(30.Seconds()).InvokeMessageAndWaitAsync(new AdminRenameEntityLoadItem(id, "renamed"));

        (await storedName("items", id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task an_entity_is_loaded_through_the_one_injected_dbcontext_that_maps_it()
    {
        // No designation: the handler injecting the admin DbContext is enough
        var id = await insertItem("bob");

        await _host.TrackActivity().Timeout(30.Seconds()).InvokeMessageAndWaitAsync(new InjectedAdminRenameEntityLoadItem(id, "renamed"));

        (await storedName("items", id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task an_entity_read_through_one_dbcontext_does_not_take_the_transaction_from_the_injected_one()
    {
        // EntityLoadNote is mapped only by the main DbContext, and the handler writes through the audit DbContext.
        // The injected DbContext still owns the transaction, as it did before [Entity] loads were considered.
        var id = Guid.NewGuid();
        await execute($"INSERT INTO entity_load.notes (Id, Name) VALUES ('{id}', 'hello')");

        await _host.TrackActivity().Timeout(30.Seconds()).InvokeMessageAndWaitAsync(new AuditEntityLoadNote(id));

        (await execute($"SELECT COUNT(*) FROM entity_load.audit WHERE Text = 'read {id}'")).ShouldBe(1);
    }

    [Fact]
    public async Task two_injected_dbcontexts_that_map_the_entity_are_ambiguous_without_a_designation()
    {
        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.DisableConventionalDiscovery().IncludeType(typeof(AmbiguousRenameEntityLoadItemHandler));

                    opts.Services.AddDbContextWithWolverineIntegration<EntityLoadMainDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString));
                    opts.Services.AddDbContextWithWolverineIntegration<EntityLoadAdminDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString));

                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                    opts.UseEntityFrameworkCoreTransactions();
                    opts.Policies.AutoApplyTransactions();
                }).StartAsync();

            await host.InvokeMessageAndWaitAsync(new AmbiguousRenameEntityLoadItem(Guid.NewGuid(), "renamed"));
        });

        ex.ToString().ShouldContain("it is mapped by more than one injected DbContext: EntityLoadMainDbContext, EntityLoadAdminDbContext");
    }

    // [All], [Queryable] and [FirstOrDefault] choose their DbContext the same way [Entity] does. The main DbContext
    // only sees alice's items, so seeing bob's proves the read went through the admin DbContext.

    [Fact]
    public async Task all_reads_through_the_injected_dbcontext()
    {
        await insertItem("alice");
        await insertItem("bob");

        var count = await _host.Services.GetRequiredService<IMessageBus>().InvokeAsync<EntityLoadItemCount>(new CountAllEntityLoadItems(), TestContext.Current.CancellationToken);

        count.Count.ShouldBe(2);
    }

    [Fact]
    public async Task queryable_reads_through_the_injected_dbcontext()
    {
        await insertItem("alice");
        await insertItem("bob");

        var count = await _host.Services.GetRequiredService<IMessageBus>().InvokeAsync<EntityLoadItemCount>(new CountQueryableEntityLoadItems(), TestContext.Current.CancellationToken);

        count.Count.ShouldBe(2);
    }

    [Fact]
    public async Task first_or_default_reads_through_the_injected_dbcontext()
    {
        await insertItem("bob");

        var count = await _host.Services.GetRequiredService<IMessageBus>().InvokeAsync<EntityLoadItemCount>(new FindFirstEntityLoadItem(), TestContext.Current.CancellationToken);

        count.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_storage_update_is_applied_to_the_designated_dbcontext()
    {
        var id = await insertItem("bob");

        await _host.TrackActivity().Timeout(30.Seconds()).InvokeMessageAndWaitAsync(new AdminUpdateEntityLoadItem(id, "renamed"));

        (await storedName("items", id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task a_storage_delete_is_applied_to_the_designated_dbcontext()
    {
        var id = await insertItem("bob");

        await _host.TrackActivity().Timeout(30.Seconds()).InvokeMessageAndWaitAsync(new AdminDeleteEntityLoadItem(id));

        (await execute($"SELECT COUNT(*) FROM entity_load.items WHERE Id = '{id}'")).ShouldBe(0);
    }

    [Fact]
    public async Task a_storage_action_for_an_entity_the_designated_dbcontext_does_not_map_fails_at_codegen()
    {
        // The admin DbContext does not map EntityLoadNote, so the insert would go to the main DbContext,
        // which the transactional middleware never saves
        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.DisableConventionalDiscovery().IncludeType(typeof(MisdesignatedInsertEntityLoadNoteHandler));

                    opts.Services.AddDbContextWithWolverineIntegration<EntityLoadMainDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString));
                    opts.Services.AddDbContextWithWolverineIntegration<EntityLoadAdminDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString));

                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                    opts.UseEntityFrameworkCoreTransactions();
                    opts.Policies.AutoApplyTransactions();
                }).StartAsync();

            await host.InvokeMessageAndWaitAsync(new MisdesignatedInsertEntityLoadNote(Guid.NewGuid()));
        });

        ex.ToString().ShouldContain("which EntityLoadAdminDbContext does not map");
    }

    [Fact]
    public async Task an_entity_is_loaded_through_the_dbcontext_designated_by_storage()
    {
        var id = await insertItem("bob");

        await _host.TrackActivity().Timeout(30.Seconds()).InvokeMessageAndWaitAsync(new StorageRenameEntityLoadItem(id, "renamed"));

        (await storedName("items", id)).ShouldBe("renamed");
    }
}

// [WolverineIgnore] on the handlers below keeps conventional discovery in OTHER tests in this assembly from picking
// them up; the hosts here include them explicitly

public class EntityLoadItem
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
}

public class EntityLoadNote
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class EntityLoadAuditRow
{
    public Guid Id { get; set; }
    public string Text { get; set; } = "";
}

public class EntityLoadMainDbContext(DbContextOptions<EntityLoadMainDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EntityLoadItem>().ToTable("items", "entity_load").HasQueryFilter(x => x.Owner == "alice");
        modelBuilder.Entity<EntityLoadNote>().ToTable("notes", "entity_load");
    }
}

public class EntityLoadAdminDbContext(DbContextOptions<EntityLoadAdminDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EntityLoadItem>().ToTable("items", "entity_load");
    }
}

public class EntityLoadAuditDbContext(DbContextOptions<EntityLoadAuditDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EntityLoadAuditRow>().ToTable("audit", "entity_load");
    }
}

public record RenameEntityLoadNote(Guid Id, string Name);

public record InjectedAdminRenameEntityLoadItem(Guid Id, string Name);

public record AuditEntityLoadNote(Guid Id);

public record AmbiguousRenameEntityLoadItem(Guid Id, string Name);

public record CountAllEntityLoadItems;

public record CountQueryableEntityLoadItems;

public record FindFirstEntityLoadItem;

public record EntityLoadItemCount(int Count);

public record RenameEntityLoadItem(Guid Id, string Name);

public record AdminRenameEntityLoadItem(Guid Id, string Name);

public record StorageRenameEntityLoadItem(Guid Id, string Name);

public record AdminUpdateEntityLoadItem(Guid Id, string Name);

public record AdminDeleteEntityLoadItem(Guid Id);

public record MisdesignatedInsertEntityLoadNote(Guid Id);

[WolverineIgnore]
public static class RenameEntityLoadNoteHandler
{
    public static void Handle(RenameEntityLoadNote command, [Entity] EntityLoadNote note) => note.Name = command.Name;
}

[WolverineIgnore]
public static class RenameEntityLoadItemHandler
{
    public static void Handle(RenameEntityLoadItem command, [Entity] EntityLoadItem item) => item.Name = command.Name;
}

[WolverineIgnore]
public static class AdminRenameEntityLoadItemHandler
{
    [Transactional(typeof(EntityLoadAdminDbContext))]
    public static void Handle(AdminRenameEntityLoadItem command, [Entity] EntityLoadItem item) => item.Name = command.Name;
}

[WolverineIgnore]
public static class StorageRenameEntityLoadItemHandler
{
    [Storage(typeof(EntityLoadAdminDbContext))]
    public static void Handle(StorageRenameEntityLoadItem command, [Entity] EntityLoadItem item) => item.Name = command.Name;
}

[WolverineIgnore]
public static class AdminUpdateEntityLoadItemHandler
{
    [Transactional(typeof(EntityLoadAdminDbContext))]
    public static Update<EntityLoadItem> Handle(AdminUpdateEntityLoadItem command, [Entity] EntityLoadItem item)
    {
        item.Name = command.Name;
        return Storage.Update(item);
    }
}

[WolverineIgnore]
public static class AdminDeleteEntityLoadItemHandler
{
    [Transactional(typeof(EntityLoadAdminDbContext))]
    public static Delete<EntityLoadItem> Handle(AdminDeleteEntityLoadItem command, [Entity] EntityLoadItem item)
        => Storage.Delete(item);
}

[WolverineIgnore]
public static class MisdesignatedInsertEntityLoadNoteHandler
{
    [Transactional(typeof(EntityLoadAdminDbContext))]
    public static Insert<EntityLoadNote> Handle(MisdesignatedInsertEntityLoadNote command)
        => Storage.Insert(new EntityLoadNote { Id = command.Id, Name = "new" });
}

[WolverineIgnore]
public static class InjectedAdminRenameEntityLoadItemHandler
{
    public static void Handle(InjectedAdminRenameEntityLoadItem command, [Entity] EntityLoadItem item,
        EntityLoadAdminDbContext db) => item.Name = command.Name;
}

[WolverineIgnore]
public static class AuditEntityLoadNoteHandler
{
    public static void Handle(AuditEntityLoadNote command, [Entity] EntityLoadNote note, EntityLoadAuditDbContext audit)
        => audit.Add(new EntityLoadAuditRow { Id = Guid.NewGuid(), Text = $"read {note.Id}" });
}

[WolverineIgnore]
public static class AmbiguousRenameEntityLoadItemHandler
{
    public static void Handle(AmbiguousRenameEntityLoadItem command, [Entity] EntityLoadItem item,
        EntityLoadMainDbContext main, EntityLoadAdminDbContext admin) => item.Name = command.Name;
}

[WolverineIgnore]
public static class CountAllEntityLoadItemsHandler
{
    public static EntityLoadItemCount Handle(CountAllEntityLoadItems command, [All] IReadOnlyList<EntityLoadItem> items,
        EntityLoadAdminDbContext db) => new(items.Count);
}

[WolverineIgnore]
public static class CountQueryableEntityLoadItemsHandler
{
    public static EntityLoadItemCount Handle(CountQueryableEntityLoadItems command,
        [Queryable] IQueryable<EntityLoadItem> items, EntityLoadAdminDbContext db) => new(items.Count());
}

[WolverineIgnore]
public static class FindFirstEntityLoadItemHandler
{
    public static EntityLoadItemCount Handle(FindFirstEntityLoadItem command, [FirstOrDefault] EntityLoadItem? item,
        EntityLoadAdminDbContext db) => new(item == null ? 0 : 1);
}
