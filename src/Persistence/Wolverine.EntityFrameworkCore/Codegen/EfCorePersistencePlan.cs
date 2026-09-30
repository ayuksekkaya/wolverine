using JasperFx.Core;
using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
///     Which DbContexts one chain uses, and which of them owns its transaction. Built once the chain's configuration
///     is complete (see <see cref="Wolverine.Persistence.IPersistenceFrameProvider.FinalizePersistence" />) and then
///     read by everything that has to agree about it: the chain's loads, its storage actions, the transactional
///     middleware's <c>SaveChangesAsync</c> and abstraction cast, and the durable inbox routing.
/// </summary>
/// <remarks>
///     Loads and the transaction are kept apart on purpose. Reading through one DbContext and writing through another
///     is legitimate, so a load goes through the DbContext chosen for its entity type (see
///     <see cref="EFCorePersistenceFrameProvider.DbContextFor" />), which is not necessarily the transaction owner.
/// </remarks>
internal sealed class EfCorePersistencePlan
{
    public const string ChainKey = "efcore_persistence_plan";

    public EfCorePersistencePlan(Type? designated, Type[] injected, Type[] loaded, Type[] written)
    {
        Designated = designated;
        Injected = injected;
        Loaded = loaded;
        Written = written;
    }

    /// <summary>
    ///     The concrete DbContext designated with <c>[Transactional(typeof(X))]</c> or <c>[Storage(typeof(X))]</c>, if any
    /// </summary>
    public Type? Designated { get; }

    /// <summary>
    ///     The DbContexts the chain's handlers and middleware take, directly, through a registered abstraction, or
    ///     through another service's dependencies
    /// </summary>
    public Type[] Injected { get; }

    /// <summary>
    ///     The DbContexts the chain loads through without taking them: <c>[Entity]</c>, <c>[All]</c>,
    ///     <c>[FirstOrDefault]</c>, <c>[Queryable]</c> and query plans
    /// </summary>
    public Type[] Loaded { get; }

    /// <summary>
    ///     The DbContexts the chain's returned storage actions are applied to
    /// </summary>
    public Type[] Written { get; }

    /// <summary>
    ///     The DbContext whose <c>SaveChangesAsync</c> commits the chain, or null with <see cref="OwnerError" /> saying why
    /// </summary>
    public Type? TransactionOwner { get; private set; }

    public string? OwnerError { get; private set; }

    public bool UsesAnyDbContext => Designated != null || Injected.Length > 0 || Loaded.Length > 0 || Written.Length > 0;

    public Type RequireTransactionOwner()
    {
        return TransactionOwner ?? throw new InvalidOperationException(OwnerError);
    }

    internal void OwnedBy(Type owner)
    {
        TransactionOwner = owner;
    }

    internal void Unowned(string error)
    {
        OwnerError = error;
    }

    /// <summary>
    ///     The owner among <paramref name="candidates" />, if exactly one. Several is an error Wolverine will not
    ///     guess its way out of; none leaves the decision to the next rule.
    /// </summary>
    internal bool TryOwnBy(Type[] candidates, string description)
    {
        if (candidates.Length == 0) return false;

        if (candidates.Length == 1)
        {
            OwnedBy(candidates[0]);
            return true;
        }

        Unowned(
            $"Cannot determine the {nameof(DbContext)} type for {description}, multiple {nameof(DbContext)} types detected: {candidates.Select(x => x.Name).Join(", ")}. " +
            $"Wolverine will not guess which one owns the transaction. Either remove the automatic transactional middleware from this handler (e.g. with [NonTransactional] or by not calling AutoApplyTransactions), " +
            $"or explicitly designate the transactional {nameof(DbContext)} with [Transactional(typeof(YourDbContext))] or [Storage(typeof(YourDbContext))] on the handler.");

        return true;
    }
}
