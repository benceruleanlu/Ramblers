using System.Collections.Generic;

namespace Ramblers;

/// <summary>
/// Reports a persistent condition once instead of on every frame it holds.
/// <see cref="Reset"/> re-arms it so the next occurrence is reported again.
/// </summary>
internal sealed class LogLatch
{
    private bool _fired;

    internal bool ShouldLog()
    {
        if (_fired)
            return false;

        _fired = true;
        return true;
    }

    internal void Reset()
    {
        _fired = false;
    }
}

/// <summary>
/// Reports a per-frame value only when it differs from the last reported one,
/// so a steady state is logged on its transitions rather than continuously.
/// </summary>
internal sealed class LogChange<T>
{
    private bool _hasValue;
    private T _value;

    internal bool ShouldLog(T value)
    {
        if (_hasValue && EqualityComparer<T>.Default.Equals(_value, value))
            return false;

        _hasValue = true;
        _value = value;
        return true;
    }

    internal void Reset()
    {
        _hasValue = false;
        _value = default;
    }
}
