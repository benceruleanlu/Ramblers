using System.Collections.Generic;

namespace Ramblers;

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
