namespace Deblur.Engine;

public sealed class ParamHistory
{
    private readonly int _capacity;
    private readonly LinkedList<KernelParams> _past = new();
    private readonly Stack<KernelParams> _future = new();

    public ParamHistory(int capacity = 50)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public bool CanUndo => _past.Count >= 2;
    public bool CanRedo => _future.Count > 0;

    public void Push(KernelParams p)
    {
        _past.AddLast(p);
        while (_past.Count > _capacity) _past.RemoveFirst();
        _future.Clear();
    }

    public bool TryUndo(out KernelParams previous)
    {
        if (_past.Count < 2)
        {
            previous = default;
            return false;
        }
        var current = _past.Last!.Value;
        _past.RemoveLast();
        _future.Push(current);
        previous = _past.Last!.Value;
        return true;
    }

    public bool TryRedo(out KernelParams next)
    {
        if (_future.Count == 0)
        {
            next = default;
            return false;
        }
        next = _future.Pop();
        _past.AddLast(next);
        while (_past.Count > _capacity) _past.RemoveFirst();
        return true;
    }

    public void Clear()
    {
        _past.Clear();
        _future.Clear();
    }
}
