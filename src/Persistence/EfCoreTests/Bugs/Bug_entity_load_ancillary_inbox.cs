using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace EfCoreTests.Bugs;

// The [Entity] and storage action counterparts of GH-3870. A handler that only loads an entity through a DbContext
// enrolled in an ancillary message store, or only returns a storage action for one, gets that DbContext's
// transactional middleware, so its inbox envelope has to land in the same store, exactly as it does for a handler that
// takes the DbContext as a parameter.

public record RenameEntityLoadModule(Guid Id, string Name);

public record InsertEntityLoadModule(Guid Id);

public sealed class EntityLoadModuleItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class EntityLoadModuleDbContext(DbContextOptions<EntityLoadModuleDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("entity_load_module");
        modelBuilder.Entity<EntityLoadModuleItem>().ToTable("items");
    }
}

// No [Storage] and no DbContext parameter: the [Entity] load is the only thing tying this handler to the module
[WolverineIgnore]
public sealed class RenameEntityLoadModuleHandler
{
    public void Handle(RenameEntityLoadModule message, [Entity] EntityLoadModuleItem item) => item.Name = message.Name;
}

// No [Storage] and no DbContext parameter either: the storage action is the only thing tying this one to the module
[WolverineIgnore]
public sealed class InsertEntityLoadModuleHandler
{
    public Insert<EntityLoadModuleItem> Handle(InsertEntityLoadModule message)
        => Storage.Insert(new EntityLoadModuleItem { Id = message.Id, Name = "inserted" });
}

public class Bug_entity_load_ancillary_inbox : IAsyncLifetime
{
    private IHost _host = null!;
    private string _queueName = null!;

    public async ValueTask InitializeAsync()
    {
        _queueName = "entity_load_" + Guid.NewGuid().ToString("N")[..8];

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<RenameEntityLoadModuleHandler>()
                    .IncludeType<InsertEntityLoadModuleHandler>();

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<RenameEntityLoadModule>()
                    .ToRabbitQueue(_queueName)
                    .UseDurableOutbox();

                opts.PublishMessage<InsertEntityLoadModule>()
                    .ToRabbitQueue(_queueName)
                    .UseDurableOutbox();

                opts.ListenToRabbitQueue(_queueName).UseDurableInbox();

                opts.Policies.AutoApplyTransactions();
                opts.UseEntityFrameworkCoreTransactions();

                opts.Services.AddDbContextWithWolverineIntegration<EntityLoadModuleDbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString),
                    "entity_load_module_wolverine");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "entity_load_main");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString,
                        "entity_load_module_wolverine", MessageStoreRole.Ancillary)
                    .Enroll<EntityLoadModuleDbContext>();

                opts.Services.AddResourceSetupOnStartup();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task envelope_is_stored_and_handled_in_the_store_enrolled_to_the_loaded_dbcontext()
    {
        var id = Guid.NewGuid();
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EntityLoadModuleDbContext>();
            db.Add(new EntityLoadModuleItem { Id = id, Name = "original" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(new RenameEntityLoadModule(id, "renamed"));

        // The mark-as-handled write is asynchronous relative to the tracked session completing
        await Task.Delay(500, TestContext.Current.CancellationToken);

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EntityLoadModuleDbContext>();
            var item = await db.Set<EntityLoadModuleItem>().SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
            item.Name.ShouldBe("renamed");
        }

        await assertInboxIsTheEnrolledStore(typeof(RenameEntityLoadModule));
    }

    [Fact]
    public async Task envelope_is_stored_and_handled_in_the_store_enrolled_to_the_dbcontext_a_storage_action_writes_to()
    {
        var id = Guid.NewGuid();

        await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(new InsertEntityLoadModule(id));

        // The mark-as-handled write is asynchronous relative to the tracked session completing
        await Task.Delay(500, TestContext.Current.CancellationToken);

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EntityLoadModuleDbContext>();
            var item = await db.Set<EntityLoadModuleItem>().SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
            item.Name.ShouldBe("inserted");
        }

        await assertInboxIsTheEnrolledStore(typeof(InsertEntityLoadModule));
    }

    private async Task assertInboxIsTheEnrolledStore(Type messageType)
    {
        var runtime = _host.GetRuntime();
        var messageTypeName = messageType.ToMessageTypeName();

        var ancillaryStore = runtime.Stores.FindAncillaryStore(typeof(EntityLoadModuleDbContext));
        var inAncillary = await ancillaryStore.Admin.AllIncomingAsync();

        inAncillary.Where(x => x.MessageType == messageTypeName && x.Status == EnvelopeStatus.Handled)
            .ShouldNotBeEmpty(
                "The envelope should be marked Handled in the store enrolled to the DbContext the handler uses, " +
                "so that the inbox update and the EF Core writes share one transaction.");

        var inMain = await runtime.Storage.Admin.AllIncomingAsync();

        inMain.Where(x => x.MessageType == messageTypeName && x.Status == EnvelopeStatus.Incoming)
            .ShouldBeEmpty("The envelope should not be left Incoming in the main store.");
    }
}
