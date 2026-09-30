using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ImTools;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;

namespace Wolverine.EntityFrameworkCore.Codegen;

// Which DbContext each chain loads, writes and commits through. See EfCorePersistencePlan.
internal partial class EFCorePersistenceFrameProvider
{
    private const string WrittenEntityTypesKey = "efcore_written_entity_types";
    private const string PendingChoicesKey = "efcore_pending_dbcontext_choices";

    // Set only on the chain-bound view that ForChain() returns
    private IChain? _chain;

    // Shared with every chain-bound view, which MemberwiseClone() copies the reference of
    private readonly ConcurrentDictionary<(Type DbContext, Type Entity), bool> _maps = new();

    /// <summary>
    ///     A view of this provider bound to <paramref name="chain" />, so that every load and storage action it builds
    ///     goes through the DbContext the chain's plan chooses for that entity type. Before the chain is finalized that
    ///     choice is left open in the frame, see <see cref="DbContextChoice" />.
    /// </summary>
    public IPersistenceFrameProvider ForChain(IChain chain, IServiceContainer container)
    {
        var bound = (EFCorePersistenceFrameProvider)MemberwiseClone();
        bound._chain = chain;
        return bound;
    }

    /// <summary>
    ///     Settle the chain's plan and everything that was waiting on it: the transactional middleware
    ///     <c>AutoApplyTransactions()</c>, <c>[Transactional]</c> or a storage action asked for before now, and the
    ///     DbContext of every load and storage action built before now. Failing here rather than at code generation
    ///     keeps a misconfigured chain a startup error.
    /// </summary>
    public void FinalizePersistence(IChain chain, IServiceContainer container)
    {
        var plan = PlanFor(chain, container);

        applyPendingTransaction(chain, container, plan);

        if (chain.Tags.TryGetValue(PendingChoicesKey, out var raw) && raw is List<DbContextChoice> choices)
        {
            chain.Tags.Remove(PendingChoicesKey);
            foreach (var choice in choices) choice.Resolve();
        }
    }

    /// <summary>
    ///     The chain's plan. Built once and kept from the moment the chain is finalized; built afresh on every call
    ///     before then, while policies may still be changing the chain.
    /// </summary>
    internal EfCorePersistencePlan PlanFor(IChain chain, IServiceContainer container)
    {
        if (chain.Tags.TryGetValue(EfCorePersistencePlan.ChainKey, out var cached) && cached is EfCorePersistencePlan plan)
        {
            return plan;
        }

        plan = buildPlan(chain, container);
        if (chain.IsPersistenceFinalized())
        {
            chain.Tags[EfCorePersistencePlan.ChainKey] = plan;
        }

        return plan;
    }

    private EfCorePersistencePlan buildPlan(IChain chain, IServiceContainer container)
    {
        var designation = findDesignation(chain);
        var designated = designation == null ? null : concreteDbContextType(designation.Value.Type);
        var injected = injectedDbContextTypes(chain, container);

        Type? chooseQuietly(Type entityType)
        {
            // An ambiguous or unmappable entity is reported by the load or storage action that uses it
            try
            {
                return normalize(chooseDbContext(designated, injected, entityType, chain, container));
            }
            catch (Exception)
            {
                return null;
            }
        }

        var loaded = loadedEntityTypes(chain, container).Select(chooseQuietly)
            .Concat(queryPlanDbContextTypes(chain))
            .OfType<Type>().Distinct().ToArray();

        var written = writtenEntityTypes(chain).Select(chooseQuietly).OfType<Type>().Distinct().ToArray();

        var plan = new EfCorePersistencePlan(designated, injected, loaded, written);
        chooseTransactionOwner(plan, designation, chain, container);
        return plan;
    }

    /// <summary>
    ///     In order: the DbContext that persists the saga, the designated one, the one the chain takes or its storage
    ///     actions write to, the one it loads through, and the one that persists a saga the handler starts.
    /// </summary>
    /// <remarks>
    ///     What the chain takes and what its storage actions write to count as one set, because Wolverine cannot tell
    ///     whether a DbContext taken as a parameter is only read from. When they are different DbContexts, whichever
    ///     one is picked, the other one's writes are silently dropped, and which one it was used to depend on whether
    ///     <c>AutoApplyTransactions()</c> or <c>[Transactional]</c> applied the transaction. So that is an error asking for
    ///     a designation, like any other chain with more than one candidate (GH-4631).
    /// </remarks>
    private void chooseTransactionOwner(EfCorePersistencePlan plan, (Type Type, string Source)? designation,
        IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain saga && TryDetermineDbContextType(saga.SagaType, container) is { } sagaDbContextType)
        {
            plan.OwnedBy(normalize(sagaDbContextType));
            return;
        }

        if (plan.Designated != null)
        {
            var candidates = plan.Injected.Union(plan.Loaded).Union(plan.Written).ToArray();
            if (candidates.Contains(plan.Designated))
            {
                plan.OwnedBy(plan.Designated);
            }
            else
            {
                // A typo should fail loudly rather than fall through to a default
                plan.Unowned(
                    $"The {designation!.Value.Source} DbContextType {designation.Value.Type.FullNameInCode()} on {chain.Description} is not one of this chain's dependencies (directly or via a registered DbContext abstraction). " +
                    $"Detected {nameof(DbContext)} types: {(candidates.Length == 0 ? "none" : candidates.Select(x => x.Name).Join(", "))}");
            }

            return;
        }

        var takenOrWritten = plan.Injected.Union(plan.Written).ToArray();
        if (plan.Injected.Length > 0 && plan.Written.Length > 0 && takenOrWritten.Length > 1)
        {
            plan.Unowned(
                $"Cannot determine the {nameof(DbContext)} type for {chain.Description}, which takes {plan.Injected.Select(x => x.Name).Join(", ")} and returns storage actions for {plan.Written.Select(x => x.Name).Join(", ")}. " +
                $"Wolverine cannot tell whether a {nameof(DbContext)} taken as a parameter is only read from, and only one of them can own the transaction, so the others' writes would be silently dropped. " +
                $"Designate the transactional {nameof(DbContext)} with [Transactional(typeof(YourDbContext))] or [Storage(typeof(YourDbContext))] on the handler.");
            return;
        }

        if (plan.TryOwnBy(takenOrWritten, chain.Description)) return;
        if (plan.TryOwnBy(plan.Loaded, chain.Description)) return;

        var startedSagaType = chain.HandlerCalls().SelectMany(x => x.Creates)
            .Where(x => x.VariableType.CanBeCastTo<Saga>())
            .Select(x => x.VariableType)
            .FirstOrDefault();

        if (startedSagaType != null && TryDetermineDbContextType(startedSagaType, container) is { } startedSagaDbContextType)
        {
            plan.OwnedBy(normalize(startedSagaDbContextType));
            return;
        }

        plan.Unowned($"Cannot determine the {nameof(DbContext)} type for {chain.Description}");
    }

    /// <summary>
    ///     The DbContext an entity on this chain is loaded and written through: the designated one if it maps the
    ///     entity, else the one DbContext the chain takes that maps it, else the first registered DbContext that maps
    ///     it. Not necessarily the transaction owner, see <see cref="EfCorePersistencePlan" />.
    /// </summary>
    internal Type DbContextFor(EfCorePersistencePlan plan, IChain chain, Type entityType, IServiceContainer container)
    {
        return chooseDbContext(plan.Designated, plan.Injected, entityType, chain, container);
    }

    private Type chooseDbContext(Type? designated, Type[] injected, Type entityType, IChain chain,
        IServiceContainer container)
    {
        if (designated != null && dbContextMaps(designated, entityType, container))
        {
            return designated;
        }

        var mapping = injected.Where(x => dbContextMaps(x, entityType, container)).ToArray();
        if (mapping.Length == 1)
        {
            return mapping[0];
        }

        if (mapping.Length > 1)
        {
            throw new InvalidOperationException(
                $"Cannot determine the {nameof(DbContext)} type for {entityType.FullNameInCode()} on {chain.Description}, it is mapped by more than one injected {nameof(DbContext)}: {mapping.Select(x => x.Name).Join(", ")}. " +
                $"Designate the one to use with [Transactional(typeof(YourDbContext))] or [Storage(typeof(YourDbContext))] on the handler.");
        }

        return defaultDbContextType(entityType, container);
    }

    /// <summary>
    ///     The first registered DbContext that maps <paramref name="entityType" />, what an entity goes through when
    ///     nothing on the chain says otherwise
    /// </summary>
    private Type defaultDbContextType(Type entityType, IServiceContainer container)
    {
        return TryDetermineDbContextType(entityType, container)
               ?? throw new ArgumentOutOfRangeException("Unable to determine a DbContext type that persists " +
                                                        entityType.FullNameInCode());
    }

    /// <summary>
    ///     The DbContext for <paramref name="entityType" />, from the bound chain's plan when this is a chain-bound view.
    ///     Left open until the chain is finalized when it is not yet, so that the choice sees the chain's final
    ///     middleware.
    /// </summary>
    private DbContextChoice choiceFor(Type entityType, IServiceContainer container)
    {
        if (_chain == null) return DetermineDbContextType(entityType, container);

        var chain = _chain;
        if (chain.IsPersistenceFinalized())
        {
            return DbContextFor(PlanFor(chain, container), chain, entityType, container);
        }

        var choice = new DbContextChoice(() => DbContextFor(PlanFor(chain, container), chain, entityType, container));

        if (!chain.Tags.TryGetValue(PendingChoicesKey, out var raw) || raw is not List<DbContextChoice> choices)
        {
            choices = [];
            chain.Tags[PendingChoicesKey] = choices;
        }

        choices.Add(choice);

        return choice;
    }

    /// <summary>
    ///     The identity type of <paramref name="sagaType" /> on <paramref name="chain" />, from the primary key of the
    ///     DbContext that loads it. An HTTP endpoint needs it while it is being constructed, before that DbContext is
    ///     settled, so until then it comes from every DbContext that maps the type, which have to agree.
    /// </summary>
    public Type DetermineSagaIdType(Type sagaType, IChain chain, IServiceContainer container)
    {
        if (chain.IsPersistenceFinalized() || chain is SagaChain)
        {
            return primaryKeyType(DbContextFor(PlanFor(chain, container), chain, sagaType, container), sagaType, container);
        }

        var keyTypes = EfCoreOpFrames.RegisteredDbContextTypes(container)
            .Where(x => dbContextMaps(x, sagaType, container))
            .Select(x => primaryKeyType(x, sagaType, container))
            .Distinct()
            .ToArray();

        if (keyTypes.Length == 1) return keyTypes[0];

        if (keyTypes.Length > 1)
        {
            throw new InvalidOperationException(
                $"The {nameof(DbContext)} types that map {sagaType.FullNameInCode()} give it different primary key types ({keyTypes.Select(x => x.NameInCode()).Join(", ")}), and {chain.Description} needs its identity type before Wolverine has settled which of them it loads through. Map the same primary key in each of them.");
        }

        return DetermineSagaIdType(sagaType, container);
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain saga)
        {
            return TryDetermineDbContextType(saga.SagaType, container) != null;
        }

        return chain.PlannedServiceDependencies(container, Type.EmptyTypes)
            .Any(x => x.CanBeCastTo<DbContext>() || _abstractions.Contains(x));
    }

    /// <summary>
    ///     Loading through a DbContext with <c>[Entity]</c>, <c>[All]</c>, <c>[FirstOrDefault]</c>, <c>[Queryable]</c>
    ///     or a query plan, or returning a storage action for one, depends on that DbContext as surely as taking it as a
    ///     parameter, but <see cref="CanApply" /> cannot see it. Without this, a handler that only changes what it loaded
    ///     is never saved. Same problem as Marten's GH-2941.
    /// </summary>
    public bool CanApplyThroughLoadsOrWrites(IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain) return false;

        var plan = PlanFor(chain, container);
        return plan.Loaded.Length > 0 || plan.Written.Length > 0;
    }

    /// <summary>
    /// EF Core is the one provider whose transaction owner is a plain service dependency, so it is the one
    /// provider that can tell Wolverine which enrolled store a handler's durable inbox row belongs in
    /// (GH-3870). Read off the same plan the transactional middleware commits through, so the inbox row and the
    /// SaveChanges land in the same database.
    /// </summary>
    public Type? TryDetermineTransactionOwnerType(IChain chain, IServiceContainer container)
    {
        if (!CanApply(chain, container) && !CanApplyThroughLoadsOrWrites(chain, container)) return null;

        try
        {
            // Ambiguous or unresolvable is reported by the transactional middleware at codegen with a chain-specific
            // message. Here, while the inbox routing map is built, the safe answer is "no ancillary store".
            return PlanFor(chain, container).TransactionOwner;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// GH-4631. EF Core is designated by a <c>DbContext</c> type or a registered DbContext abstraction —
    /// the same two spellings <see cref="DetermineDbContextType(IChain,IServiceContainer)" /> already
    /// resolves when a chain depends on more than one DbContext.
    /// </summary>
    public bool OwnsStorageType(Type storageType, IServiceContainer container)
    {
        return storageType.CanBeCastTo<DbContext>() || _abstractions.Contains(storageType);
    }

    /// <summary>
    ///     The DbContext that owns <paramref name="chain" />'s transaction. Throws when the chain designates one it does
    ///     not use, or uses several without designating one.
    /// </summary>
    public Type DetermineDbContextType(IChain chain, IServiceContainer container)
    {
        return PlanFor(chain, container).RequireTransactionOwner();
    }

    internal Type DetermineDbContextType(Type entityType, IServiceContainer container)
    {
        return _chain == null
            ? defaultDbContextType(entityType, container)
            : DbContextFor(PlanFor(_chain, container), _chain, entityType, container);
    }

    /// <summary>
    ///     The DbContext types the chain's handlers and middleware take, once its configuration is complete: directly,
    ///     through a registered DbContext abstraction, or through another service's dependencies. Deliberately not the
    ///     DbContexts it only loads through.
    /// </summary>
    private Type[] injectedDbContextTypes(IChain chain, IServiceContainer container)
    {
        var dependencies = chain.PlannedServiceDependencies(container, Type.EmptyTypes).ToArray();

        var contextTypes = dependencies.Where(x => x.CanBeCastTo<DbContext>()).ToArray();
        var abstractionTypes = dependencies.Where(x => _abstractions.Contains(x)).ToArray();

        return contextTypes
            .Concat(abstractionTypes.Select(x => _abstractions.TryFind(x, out var concrete) ? concrete : null))
            .OfType<Type>() // Removes nullability
            .Distinct()
            .ToArray();
    }

    /// <summary>
    ///     The entity types this chain loads through a DbContext without taking it: its <c>[Entity]</c>,
    ///     <c>[FromEfCore]</c>, <c>[All]</c>, <c>[FirstOrDefault]</c> and <c>[Queryable]</c> parameters. Read off the
    ///     chain's methods rather than its frames, because a message handler only builds those at code generation.
    /// </summary>
    private IEnumerable<Type> loadedEntityTypes(IChain chain, IServiceContainer container)
    {
        foreach (var call in chain.PlannedMethodCalls())
        {
            foreach (var parameter in call.Method.GetParameters())
            {
                foreach (var attribute in parameter.GetCustomAttributes(true))
                {
                    var entityType = attribute switch
                    {
                        // [FromMarten] and the other explicit attributes load from their own store
                        ExplicitEntityAttribute and not FromEfCoreAttribute => null,

                        FromQuerySpecificationAttribute => null,

                        IEntityLoadAttribute load => load.DetermineLoadedEntityType(parameter),
                        _ => null
                    };

                    if (entityType != null && TryDetermineDbContextType(entityType, container) != null)
                    {
                        yield return entityType;
                    }
                }
            }
        }
    }

    /// <summary>
    ///     The DbContexts named by this chain's EF Core query plans, from <c>[FromQuerySpecification]</c> or returned by
    ///     a Load/Before method
    /// </summary>
    private static IEnumerable<Type> queryPlanDbContextTypes(IChain chain)
    {
        foreach (var call in chain.PlannedMethodCalls())
        {
            foreach (var parameter in call.Method.GetParameters())
            {
                if (parameter.GetCustomAttribute<FromQuerySpecificationAttribute>() is { } spec
                    && queryPlanDbContextType(spec.SpecificationType) is { } specDbContextType)
                {
                    yield return specDbContextType;
                }
            }

            foreach (var created in call.Creates)
            {
                if (queryPlanDbContextType(created.VariableType) is { } dbContextType) yield return dbContextType;
            }
        }
    }

    /// <summary>
    ///     The DbContext an EF Core <see cref="IQueryPlan{TDbContext,TResult}" /> or
    ///     <see cref="IBatchQueryPlan{TDbContext,TResult}" /> names, or null for any other type.
    /// </summary>
    private static Type? queryPlanDbContextType(Type type)
    {
        var plan = type.FindInterfaceThatCloses(typeof(IBatchQueryPlan<,>))
                   ?? type.FindInterfaceThatCloses(typeof(IQueryPlan<,>));

        return plan?.GetGenericArguments()[0];
    }

    private static IEnumerable<Type> writtenEntityTypes(IChain chain)
    {
        return chain.Tags.TryGetValue(WrittenEntityTypesKey, out var raw) && raw is List<Type> types ? types : [];
    }

    /// <summary>
    ///     Remember that the chain returns a storage action for <paramref name="entityType" />, which the plan has to
    ///     save through the transaction owner
    /// </summary>
    private static void recordWrittenEntityType(IChain chain, Type entityType)
    {
        if (!chain.Tags.TryGetValue(WrittenEntityTypesKey, out var raw) || raw is not List<Type> types)
        {
            types = [];
            chain.Tags[WrittenEntityTypesKey] = types;
        }

        if (!types.Contains(entityType)) types.Add(entityType);
    }

    /// <summary>
    ///     The designation, if any, as written: <c>[Transactional(typeof(X))]</c> (carried as a chain tag once the
    ///     attribute has been applied, but read off the handler too, because a message handler's <c>[Entity]</c>
    ///     parameters are built before its chain attributes), then <c>[Storage(typeof(X))]</c> (read off the handler for
    ///     the same reason, as well as <see cref="IChain.AncillaryStoreType" />). X may be a DbContext, a registered
    ///     DbContext abstraction, or the store of another integration, which EF Core ignores.
    /// </summary>
    private static (Type Type, string Source)? findDesignation(IChain chain)
    {
        if (chain.Tags.TryGetValue(TransactionalAttribute.TransactionalDbContextTypeKey, out var tagged)
            && tagged is Type taggedType)
        {
            return (taggedType, "[Transactional]");
        }

        foreach (var call in chain.HandlerCalls())
        {
            var att = call.Method.GetCustomAttribute<TransactionalAttribute>(inherit: true)
                      ?? call.HandlerType.GetCustomAttribute<TransactionalAttribute>(inherit: true);

            if (att?.DbContextType != null)
            {
                return (att.DbContextType, "[Transactional]");
            }
        }

        foreach (var call in chain.HandlerCalls())
        {
            var att = call.Method.GetCustomAttribute<StorageAttribute>(inherit: true)
                      ?? call.HandlerType.GetCustomAttribute<StorageAttribute>(inherit: true);

            if (att != null)
            {
                return (att.StoreType, "[Storage]");
            }
        }

        return chain.AncillaryStoreType == null ? null : (chain.AncillaryStoreType, "[Storage]");
    }

    /// <summary>
    ///     The concrete DbContext a designated type stands for, or null when it is not a DbContext or a registered
    ///     abstraction of one
    /// </summary>
    private Type? concreteDbContextType(Type designated)
    {
        var resolved = _abstractions.TryFind(designated, out var concrete) ? concrete : designated;
        return resolved.CanBeCastTo<DbContext>() ? resolved : null;
    }

    /// <summary>
    ///     A multi-tenanted DbContext can be reported as its <c>IDbContextBuilder&lt;T&gt;</c> service type
    /// </summary>
    private static Type normalize(Type dbContextType)
    {
        return dbContextType.IsGenericType && dbContextType.GetGenericTypeDefinition() == typeof(IDbContextBuilder<>)
            ? dbContextType.GetGenericArguments()[0]
            : dbContextType;
    }

    private bool dbContextMaps(Type dbContextType, Type entityType, IServiceContainer container)
    {
        var key = (normalize(dbContextType), entityType);
        if (_maps.TryGetValue(key, out var maps)) return maps;

        maps = modelMaps(key.Item1, entityType, container);
        _maps[key] = maps;
        return maps;
    }

    private static bool modelMaps(Type dbContextType, Type entityType, IServiceContainer container)
    {
        using var nested = container.Services.CreateScope();
        try
        {
            if (nested.ServiceProvider.GetService(dbContextType) is DbContext dbContext)
            {
                return dbContext.Model.FindEntityType(entityType) != null;
            }

            var builderType = typeof(IDbContextBuilder<>).MakeGenericType(dbContextType);
            if (nested.ServiceProvider.GetService(builderType) is IDbContextBuilder builder)
            {
                return builder.BuildForMain().Model.FindEntityType(entityType) != null;
            }
        }
        catch (InvalidOperationException e)
        {
            var logger = container.Services.GetService<ILogger<EFCorePersistenceFrameProvider>>();
            logger?.LogError(e, "Error trying to use DbContext type {DbContextType}", dbContextType.FullNameInCode());
        }

        return false;
    }

    private static Type primaryKeyType(Type dbContextType, Type entityType, IServiceContainer container)
    {
        using var nested = container.Services.CreateScope();
        var context = resolveDbContext(nested, normalize(dbContextType));
        var config = context.Model.FindEntityType(entityType);
        if (config == null)
        {
            throw new InvalidOperationException(
                $"Could not find entity configuration for {entityType.FullNameInCode()} in DbContext {context}");
        }

        return config.FindPrimaryKey()?.GetKeyType() ??
               throw new InvalidOperationException(
                   $"No known primary key for {entityType.FullNameInCode()} in DbContext {context}");
    }
}
