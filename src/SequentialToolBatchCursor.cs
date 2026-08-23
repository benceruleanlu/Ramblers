namespace Ramblers;

/// <summary>
/// Pure ordering state for one Realtime response's function calls. Exactly one
/// call may be active; the next call cannot be routed until the current call's
/// immediate result or verified job completion has been consumed.
/// </summary>
internal sealed class SequentialToolBatchCursor
{
    private readonly int _count;
    private int _nextIndex;
    private int _activeIndex = -1;

    internal SequentialToolBatchCursor(int count)
    {
        if (count < 1)
            throw new System.ArgumentOutOfRangeException(nameof(count));
        _count = count;
    }

    internal int ActiveIndex => _activeIndex;
    internal bool HasUndispatched => _nextIndex < _count;
    internal bool IsComplete => _activeIndex < 0 && _nextIndex >= _count;

    internal bool TryBeginNext(out int index)
    {
        index = -1;
        if (_activeIndex >= 0 || _nextIndex >= _count)
            return false;

        index = _nextIndex++;
        _activeIndex = index;
        return true;
    }

    internal bool TryCompleteActive(int index)
    {
        if (index < 0 || index != _activeIndex)
            return false;
        _activeIndex = -1;
        return true;
    }
}
