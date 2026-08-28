using System.Collections.Concurrent;
using System.Text;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// A <see cref="StringBuilder"/> pool that removes per-prompt heap allocations. Thread-safe;
/// buffers are rented and returned within a request scope.
/// </summary>
public static class PromptBufferPool
{
    private const int DefaultCapacity = 32 * 1024;
    private static readonly ConcurrentBag<StringBuilder> s_pool = [];

    /// <summary>Rents a <see cref="StringBuilder"/> and returns a <see cref="PromptBufferLease"/>.</summary>
    public static PromptBufferLease Rent()
    {
        if (!s_pool.TryTake(out var sb)) sb = new StringBuilder(DefaultCapacity);

        return new PromptBufferLease(sb);
    }

    internal static void Return(StringBuilder sb)
    {
        sb.Clear();
        s_pool.Add(sb);
    }
}

/// <summary>
/// A <see cref="StringBuilder"/> lease. <see cref="Dispose"/> clears the buffer and returns it to the pool.
/// </summary>
public struct PromptBufferLease : IDisposable
{
    private StringBuilder? _sb;

    internal PromptBufferLease(StringBuilder sb)
    {
        _sb = sb;
    }

    /// <summary>The rented buffer.</summary>
    public readonly StringBuilder Buffer => _sb ?? throw new ObjectDisposedException(nameof(PromptBufferLease));

    /// <summary>A writer over the rented buffer.</summary>
    public readonly PromptWriter Writer => new(Buffer);

    public void Dispose()
    {
        if (_sb is null) return;
        PromptBufferPool.Return(_sb);
        _sb = null;
    }
}
