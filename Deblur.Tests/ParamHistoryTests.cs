using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class ParamHistoryTests
{
    private static KernelParams P(float angle) =>
        new KernelParams(BlurType.Motion, angle, 10f, 0.005f, 0f, 0f, AlgorithmType.Wiener);

    [Fact]
    public void Empty_CanUndoFalse_CanRedoFalse()
    {
        var h = new ParamHistory();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void SinglePush_StillCanNotUndo()
    {
        var h = new ParamHistory();
        h.Push(P(10f));
        // One entry = the current state. Nothing to step back TO.
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void TwoPushes_UndoReturnsFirst()
    {
        var h = new ParamHistory();
        h.Push(P(10f));
        h.Push(P(20f));
        Assert.True(h.CanUndo);
        Assert.True(h.TryUndo(out var previous));
        Assert.Equal(10f, previous.Angle);
        Assert.True(h.CanRedo);
    }

    [Fact]
    public void UndoThenRedo_ReturnsSecond()
    {
        var h = new ParamHistory();
        h.Push(P(10f));
        h.Push(P(20f));
        h.TryUndo(out _);
        Assert.True(h.TryRedo(out var next));
        Assert.Equal(20f, next.Angle);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void PushAfterUndo_ClearsRedoStack()
    {
        var h = new ParamHistory();
        h.Push(P(10f));
        h.Push(P(20f));
        h.TryUndo(out _);
        h.Push(P(30f));
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Capacity_DropsOldestOnOverflow()
    {
        var h = new ParamHistory(capacity: 3);
        h.Push(P(1f));
        h.Push(P(2f));
        h.Push(P(3f));
        h.Push(P(4f)); // 1 gets dropped
        Assert.True(h.TryUndo(out var p3));
        Assert.Equal(3f, p3.Angle);
        Assert.True(h.TryUndo(out var p2));
        Assert.Equal(2f, p2.Angle);
        Assert.False(h.CanUndo); // 1 was dropped
    }

    [Fact]
    public void Clear_ResetsBothStacks()
    {
        var h = new ParamHistory();
        h.Push(P(1f));
        h.Push(P(2f));
        h.TryUndo(out _);
        h.Clear();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }
}
