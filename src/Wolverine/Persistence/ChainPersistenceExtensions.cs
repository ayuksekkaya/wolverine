using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Persistence;

public static class ChainPersistenceExtensions
{
    private const string PersistenceFinalizedKey = "wolverine_persistence_finalized";

    /// <summary>
    ///     True once <see cref="IPersistenceFrameProvider.FinalizePersistence" /> has run for this chain, so its
    ///     configuration is complete and a provider can plan its persistence for good.
    /// </summary>
    public static bool IsPersistenceFinalized(this IChain chain) => chain.Tags.ContainsKey(PersistenceFinalizedKey);

    /// <summary>
    ///     Let every persistence provider plan this chain's persistence, once. Wolverine calls this after the
    ///     chain's policies have run: for message handlers at the end of <c>HandlerGraph.Compile()</c>, for HTTP
    ///     endpoints once endpoint discovery has applied every policy, and again before generating code for any
    ///     chain that neither of those reached.
    /// </summary>
    public static void FinalizePersistence(this IChain chain, GenerationRules rules, IServiceContainer container)
    {
        if (chain.IsPersistenceFinalized()) return;

        // Marked first, so the providers see a finalized chain while they plan it
        chain.Tags[PersistenceFinalizedKey] = true;

        foreach (var provider in rules.PersistenceProviders())
        {
            provider.FinalizePersistence(chain, container);
        }
    }
}
