namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
///     The DbContext a frame loads or writes through. Either known when the frame is built, or chosen by the chain's
///     persistence plan, which may not exist yet: an HTTP endpoint builds its <c>[Entity]</c> loads while it is
///     constructed, and <c>AutoApplyTransactions()</c> and returned storage actions are applied while policies run,
///     all before the chain has its final middleware and its plan. The frame asks for the type when it generates
///     code, or when the plan forces the choice at finalization, whichever comes first.
/// </summary>
internal sealed class DbContextChoice
{
    private readonly Func<Type>? _choose;
    private readonly List<Action<Type>> _whenChosen = [];
    private Type? _type;

    public DbContextChoice(Type type)
    {
        _type = type;
    }

    public DbContextChoice(Func<Type> choose)
    {
        _choose = choose;
    }

    public static implicit operator DbContextChoice(Type type) => new(type);

    public bool IsChosen => _type != null;

    public Type Resolve()
    {
        if (_type != null) return _type;

        var type = _choose!();

        // Anything that depends on the DbContext's model, like validating [FromEfCore] Include paths
        foreach (var action in _whenChosen) action(type);

        _type = type;
        return type;
    }

    /// <summary>
    ///     Run <paramref name="action" /> against the chosen DbContext type: now if it is already known, else when it is.
    /// </summary>
    public void WhenChosen(Action<Type> action)
    {
        if (_type != null)
        {
            action(_type);
        }
        else
        {
            _whenChosen.Add(action);
        }
    }
}
