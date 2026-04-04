using System.Runtime.CompilerServices;

namespace Beutl.Editor;

public sealed class HistoryManager : IDisposable
{
    public void Commit(string? name = null, [CallerArgumentExpression(nameof(name))] string? expression = null)
    {
        throw null!;
    }

    public void Dispose()
    {
        throw null!;
    }
}
