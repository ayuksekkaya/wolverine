using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
///     A call to one of <see cref="EfCoreStorageActionApplier" />'s helpers, which are closed over the DbContext type,
///     for a storage action whose DbContext was not chosen yet when the frame was built. Builds the call once it is,
///     and otherwise generates exactly the same code the call would.
/// </summary>
internal class DeferredApplierCall : Frame
{
    private readonly DbContextChoice _dbContext;
    private readonly Func<Type, MethodCall> _build;
    private MethodCall? _call;

    public DeferredApplierCall(DbContextChoice dbContext, Func<Type, MethodCall> build) : base(true)
    {
        _dbContext = dbContext;
        _build = build;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _call ??= _build(_dbContext.Resolve());
        return _call.FindVariables(chain);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        _call!.GenerateCode(method, writer);
        Next?.GenerateCode(method, writer);
    }
}
