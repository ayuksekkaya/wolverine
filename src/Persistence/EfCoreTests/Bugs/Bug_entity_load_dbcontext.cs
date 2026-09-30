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
                    .IncludeType(typeof(AdminDeleteEntityLoadItemHandler));

                // Registered first, so it is the default DbContext for EntityLoadItem
                opts.Services.AddDbContextWithWolverineIntegration<EntityLoadMainDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.Services.AddDbContextWithWolverineIntegration<EntityLoadAdminDbContext>(x =>
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
            CREATE TABLE entity_load.items (Id uniqueidentifier PRIMARY KEY, Owner nvarchar(50) NOT NULL, Name nvarchar(50) NOT NULL);
            CREATE TABLE entity_load.notes (Id uniqueidentifier PRIMARY KEY, Name nvarchar(50) NOT NULL);
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

public record RenameEntityLoadNote(Guid Id, string Name);

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
