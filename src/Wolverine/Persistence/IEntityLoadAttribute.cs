using System.Reflection;

namespace Wolverine.Persistence;

/// <summary>
///     Implemented by the parameter attributes whose value Wolverine loads through a persistence provider:
///     <c>[Entity]</c>, <c>[All]</c>, <c>[FirstOrDefault]</c> and <c>[Queryable]</c>.
/// </summary>
/// <remarks>
///     The load frame for such a parameter is only built at codegen, after <c>AutoApplyTransactions</c> and
///     <c>[Transactional]</c> have decided whether the chain uses a store. This lets a provider see the load before
///     then, so a chain that reads through a store is transactional exactly as if it had taken the store as a
///     parameter.
/// </remarks>
public interface IEntityLoadAttribute
{
    /// <summary>
    ///     The entity type loaded for <paramref name="parameter" />, or its element type for a collection or
    ///     <c>IQueryable&lt;T&gt;</c> parameter. Null if the parameter is not a shape this attribute can load, which
    ///     the attribute reports itself at codegen.
    /// </summary>
    Type? DetermineLoadedEntityType(ParameterInfo parameter);
}
