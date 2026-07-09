# Deblur Phase 5c Implementation Plan (Undo / Redo)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ctrl+Z undoes and Ctrl+Y redoes the most recent parameter change (drag-commit, blur-type switch, algorithm switch, or Reset).

**Architecture:** New `ParamHistory` class in `Deblur.Engine` (pure C#, no WPF deps) — two `Stack<KernelParams>` with 50-entry capacity. `MainViewModel` snapshots on commit-shaped events only (not per-slider-tick), with a `_suppressHistory` guard that prevents Undo/Redo from re-recording. `MainWindow` drops in two new commands (Ctrl+Z / Ctrl+Y) via existing `AppCommands` + `Window.InputBindings` / `CommandBindings` plumbing with `CanExecute` gating.

**Tech Stack:** .NET 8, WPF, xUnit. No new NuGet packages.

## Global Constraints

- .NET 8. Nullable + ImplicitUsings enabled everywhere.
- No new NuGet packages.
- `ParamHistory` lives in `Deblur.Engine` namespace (no WPF deps; testable from existing `Deblur.Tests`).
- 50-entry capacity; drop-oldest on overflow.
- Push clears the redo stack (standard divergence).
- Snapshots pushed only on: DragCommitted (via new `Vm.CommitArrowDrag`), `OnSelectedBlurTypeChanged`, `OnSelectedAlgorithmChanged`, `Reset()`. NOT on per-slider-tick / per-arrow-move.
- `LoadImageFromBytes` clears history (does NOT push).
- `_suppressHistory` guard makes Undo/Redo not re-record.
- All 54 phase-5e tests remain green.
- Phase 5c branches from tag `phase5e` onto branch `phase5c-undo-redo`.

---

### Task 1: `ParamHistory` class + tests (TDD)

**Files:**
- Create: `Deblur.Engine/ParamHistory.cs`
- Create: `Deblur.Tests/ParamHistoryTests.cs`

**Interfaces:**
```csharp
public sealed class ParamHistory
{
    public ParamHistory(int capacity = 50);
    public bool CanUndo { get; }
    public bool CanRedo { get; }
    public void Push(KernelParams p);
    public bool TryUndo(out KernelParams previous);
    public bool TryRedo(out KernelParams next);
    public void Clear();
}
```
Push semantics: appends to the past stack; drops the OLDEST past entry if capacity exceeded; clears the future stack.
Undo semantics: pops the current top of past into the future stack; returns the NEW top of past (i.e., the state we're stepping back TO). Returns `false` if the past stack has fewer than 2 entries (nothing to step back TO).
Redo semantics: pops the top of the future stack, pushes it back onto past, returns it. Returns `false` if future is empty.

- [ ] **Step 1: Write failing tests**

Create `Deblur.Tests/ParamHistoryTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run tests — verify compile failure**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~ParamHistoryTests"
```
Expected: compile errors — `ParamHistory` not defined.

- [ ] **Step 3: Implement `ParamHistory`**

Create `Deblur.Engine/ParamHistory.cs`:
```csharp
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
```

- [ ] **Step 4: Run filtered + full tests**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~ParamHistoryTests"
```
Expected: 7 passing.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 61` (54 existing + 7 new).

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/ParamHistory.cs Deblur.Tests/ParamHistoryTests.cs
git commit -m "Add ParamHistory: bounded undo/redo stack of KernelParams"
```

---

### Task 2: `MainViewModel` history wiring

**Files:**
- Modify: `Deblur/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `ParamHistory` from Task 1.
- Produces on `MainViewModel`:
  - `private readonly ParamHistory _history = new();`
  - `private bool _suppressHistory;`
  - `public bool CanUndo => _history.CanUndo;`
  - `public bool CanRedo => _history.CanRedo;`
  - `public void CommitArrowDrag(float angle, float length)` — sets Angle+Length like `UpdateKernel` but also snapshots on completion.
  - `public void Undo()` / `public void Redo()`.
  - `private void PushSnapshot()` — no-op when `_suppressHistory` or `_proxy is null`; otherwise pushes `BuildCurrentParams()` to `_history` and fires CanUndo/CanRedo notifications.

Snapshotting call sites: `OnSelectedBlurTypeChanged`, `OnSelectedAlgorithmChanged`, `Reset()`, `CommitArrowDrag()`. `UpdateKernel` (called from `Dragging` events during a live drag) does NOT snapshot. `LoadImageFromBytes` calls `_history.Clear()` (and notifies CanUndo/CanRedo) BEFORE `Reset()`.

- [ ] **Step 1: Add the history field, guard, computed props, and `PushSnapshot` helper**

In `Deblur/ViewModels/MainViewModel.cs`, add after the `_runner` field declaration (near the top of the class, around the other private fields):
```csharp
    private readonly ParamHistory _history = new();
    private bool _suppressHistory;
```

Then add the computed properties in the block where the other `Is*Selected` and `HasImage` computed props live (near the top):
```csharp
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
```

Then add the `PushSnapshot` helper method (place it near `BuildCurrentParams` at the bottom of the class):
```csharp
    private void PushSnapshot()
    {
        if (_suppressHistory || _proxy is null) return;
        _history.Push(BuildCurrentParams());
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }
```

- [ ] **Step 2: Snapshot on commit-shaped events**

Edit `OnSelectedBlurTypeChanged` (currently ~lines 78–87). Append `PushSnapshot();` after `PushCurrentParams();`:
```csharp
    partial void OnSelectedBlurTypeChanged(BlurType value)
    {
        OnPropertyChanged(nameof(IsMotionSelected));
        OnPropertyChanged(nameof(IsOutOfFocusSelected));
        OnPropertyChanged(nameof(IsGaussianSelected));

        // Preserve each type's own params across switches; the user can hit Reset
        // if they want to clear the active type.
        PushCurrentParams();
        PushSnapshot();
    }
```

Edit `OnSelectedAlgorithmChanged` (currently ~lines 89–95) similarly:
```csharp
    partial void OnSelectedAlgorithmChanged(AlgorithmType value)
    {
        OnPropertyChanged(nameof(IsWienerSelected));
        OnPropertyChanged(nameof(IsTikhonovSelected));
        InvalidateFullResCache();
        PushCurrentParams();
        PushSnapshot();
    }
```

Edit `Reset()` (currently ~lines 132–150). Append `PushSnapshot();` after the closing brace of the switch and after `PushCurrentParams();`:
```csharp
    public void Reset()
    {
        switch (SelectedBlurType)
        {
            case BlurType.Motion:
                Angle = 0f;
                Length = 0f;
                break;
            case BlurType.OutOfFocus:
                Radius = 0f;
                break;
            case BlurType.Gaussian:
                Sigma = 0f;
                break;
        }
        Smoothness = 0.005f;
        PushCurrentParams();
        PushSnapshot();
    }
```

- [ ] **Step 3: Add `CommitArrowDrag`**

Add this method just below `UpdateKernel` (currently ~lines 117–124):
```csharp
    public void CommitArrowDrag(float angle, float length)
    {
        if (SelectedBlurType != BlurType.Motion) return;
        Angle = angle;
        Length = length;
        PushCurrentParams();
        PushSnapshot();
    }
```

- [ ] **Step 4: Clear history on image load**

Modify `LoadImageFromBytes` (currently ~lines 97–115). Insert `_history.Clear();` + notifications right before `Reset();`:
```csharp
        PreviewBitmap = ImageBufferInterop.NewCompatibleBitmap(pw, ph);
        _runner.SetProxy(_proxy);
        OnPropertyChanged(nameof(HasImage));
        _history.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        Reset();
```

- [ ] **Step 5: Add `Undo()` / `Redo()` public methods**

Add these methods just above `PushSnapshot` at the bottom of the class:
```csharp
    public void Undo()
    {
        if (!_history.TryUndo(out var previous)) return;
        ApplySnapshot(previous);
    }

    public void Redo()
    {
        if (!_history.TryRedo(out var next)) return;
        ApplySnapshot(next);
    }

    private void ApplySnapshot(KernelParams p)
    {
        _suppressHistory = true;
        try
        {
            SelectedBlurType  = p.Type;
            SelectedAlgorithm = p.Algorithm;
            Angle             = p.Angle;
            Length            = p.Length;
            Radius            = p.Radius;
            Sigma             = p.Sigma;
            Smoothness        = p.Smoothness;
        }
        finally
        {
            _suppressHistory = false;
        }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }
```

- [ ] **Step 6: Build + test**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 61`.

- [ ] **Step 7: Commit**

```bash
git add Deblur/ViewModels/MainViewModel.cs
git commit -m "Wire MainViewModel with ParamHistory-backed undo/redo"
```

---

### Task 3: `AppCommands` + `MainWindow` + `ShortcutsWindow` — Ctrl+Z / Ctrl+Y

**Files:**
- Modify: `Deblur/AppCommands.cs`
- Modify: `Deblur/MainWindow.xaml`
- Modify: `Deblur/MainWindow.xaml.cs`
- Modify: `Deblur/ShortcutsWindow.xaml`

**Interfaces:**
- Consumes: `MainViewModel.Undo()`, `Redo()`, `CanUndo`, `CanRedo`; `MainViewModel.CommitArrowDrag(angle, length)`.
- Produces: `AppCommands.Undo` (Ctrl+Z) and `AppCommands.Redo` (Ctrl+Y). MainWindow wires KeyBinding + CommandBinding pair for each with `CanExecute` gating and a `Executed` handler. `ShortcutsWindow` picks up two new rows. `OnPreviewDragCommitted` now calls `Vm.CommitArrowDrag(...)` instead of `Vm.UpdateKernel(...)`.

- [ ] **Step 1: Add Undo + Redo to `AppCommands`**

In `Deblur/AppCommands.cs`, add these two fields at the end of the class (before the closing brace):
```csharp
    public static readonly RoutedUICommand Undo =
        new("Undo", "Undo", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.Z, ModifierKeys.Control) });

    public static readonly RoutedUICommand Redo =
        new("Redo", "Redo", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.Y, ModifierKeys.Control) });
```

- [ ] **Step 2: Add `KeyBinding`s + `CommandBinding`s in `MainWindow.xaml`**

In `Deblur/MainWindow.xaml`, locate the existing `<Window.InputBindings>` block. Add two new KeyBindings at the end (before `</Window.InputBindings>`):
```xml
        <KeyBinding Key="Z"          Modifiers="Ctrl" Command="{x:Static local:AppCommands.Undo}"/>
        <KeyBinding Key="Y"          Modifiers="Ctrl" Command="{x:Static local:AppCommands.Redo}"/>
```

In the existing `<Window.CommandBindings>` block, add two new CommandBindings at the end (before `</Window.CommandBindings>`):
```xml
        <CommandBinding Command="{x:Static local:AppCommands.Undo}"              Executed="OnUndoExecuted"              CanExecute="OnCanUndoExecute"/>
        <CommandBinding Command="{x:Static local:AppCommands.Redo}"              Executed="OnRedoExecuted"              CanExecute="OnCanRedoExecute"/>
```

- [ ] **Step 3: Add code-behind handlers in `MainWindow.xaml.cs`**

In `Deblur/MainWindow.xaml.cs`, add these four handlers (place them alongside the other `On*Executed` handlers):
```csharp
    private void OnUndoExecuted(object sender, ExecutedRoutedEventArgs e) => Vm.Undo();
    private void OnRedoExecuted(object sender, ExecutedRoutedEventArgs e) => Vm.Redo();

    private void OnCanUndoExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = Vm?.CanUndo == true;
        e.Handled = true;
    }

    private void OnCanRedoExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = Vm?.CanRedo == true;
        e.Handled = true;
    }
```

- [ ] **Step 4: Switch `OnPreviewDragCommitted` to call `CommitArrowDrag`**

In `Deblur/MainWindow.xaml.cs`, locate `OnPreviewDragCommitted` (currently a one-liner calling `Vm.UpdateKernel(...)`). Change it to:
```csharp
    private void OnPreviewDragCommitted(object? sender, ArrowDragEventArgs e)
        => Vm.CommitArrowDrag(e.Angle, e.Length);
```

Leave `OnPreviewDragging` (which fires during the drag) UNCHANGED — it still calls `Vm.UpdateKernel(...)` for smooth mid-drag preview updates.

- [ ] **Step 5: Add two new rows to `ShortcutsWindow.xaml`**

In `Deblur/ShortcutsWindow.xaml`, extend the `<Grid.RowDefinitions>` block by adding two more `<RowDefinition Height="Auto"/>` entries. Then append two new pairs of TextBlocks for rows 10 and 11:

```xml
            <TextBlock Grid.Row="10" Grid.Column="0" Text="Ctrl+Z" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="10" Grid.Column="1" Text="Undo" Margin="0,4,0,4"/>

            <TextBlock Grid.Row="11" Grid.Column="0" Text="Ctrl+Y" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="11" Grid.Column="1" Text="Redo" Margin="0,4,0,4"/>
```

Also increase the Window's `Height` from `"360"` to `"400"` so the two extra rows and the Close button all fit.

- [ ] **Step 6: Build + test**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors, no new warnings.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 61`.

- [ ] **Step 7: Commit**

```bash
git add Deblur/AppCommands.cs Deblur/MainWindow.xaml Deblur/MainWindow.xaml.cs Deblur/ShortcutsWindow.xaml
git commit -m "Wire Ctrl+Z / Ctrl+Y for undo/redo through AppCommands + MainWindow"
```

---

### Task 4: Manual smoke test + tag `phase5c`

**Files:** none.

- [ ] **Step 1: Run the app**

```bash
dotnet run --project Deblur/Deblur.csproj
```

Walk the checklist:

- [ ] Load an image. Drag the arrow (Motion) to Angle ~45, Length ~20. Release — preview updates.
- [ ] Ctrl+Z → sliders and preview jump back to the previous state (Angle=0, Length=0 initial).
- [ ] Ctrl+Y → sliders and preview jump forward to Angle=45, Length=20.
- [ ] Drag again to Angle=90, Length=30. Release.
- [ ] Ctrl+Z twice → back to Angle=0, Length=0.
- [ ] Ctrl+Z once more → nothing happens (at start of history; no error).
- [ ] Redo → Angle=45, Length=20. Redo → Angle=90, Length=30. Third Redo → no-op.
- [ ] Do an undo, then drag arrow (new commit) — Redo should now do nothing (branch diverged).
- [ ] Switch blur type Motion → OutOfFocus. Ctrl+Z → back to Motion.
- [ ] Switch algorithm Wiener → Tikhonov. Ctrl+Z → back to Wiener.
- [ ] Reset button — snapshots the reset state. Ctrl+Z → previous state.
- [ ] Open a NEW image → history clears. Ctrl+Z does nothing.
- [ ] F1 (or Help → Keyboard Shortcuts…) — the window lists Ctrl+Z Undo and Ctrl+Y Redo.
- [ ] Existing shortcuts (Ctrl+O/S/R, Ctrl+0/1, Ctrl++/-, F5, Esc) still work.
- [ ] Existing zoom/pan/drag/save/cancellation behavior unchanged.

- [ ] **Step 2: Commit any smoke-triggered fixes**

If bugs surface, commit each fix separately.

- [ ] **Step 3: Tag phase 5c**

```bash
git tag phase5c
```

---

## Summary

Four tasks. Task 1 adds `ParamHistory` (pure C#) with 7 unit tests. Task 2 wires `MainViewModel` with the history, snapshot triggers, `CommitArrowDrag`, `Undo()` / `Redo()`, and `_suppressHistory` guard. Task 3 adds `AppCommands.Undo` / `Redo`, wires KeyBindings + CommandBindings with `CanExecute` gating, updates `OnPreviewDragCommitted` to call `CommitArrowDrag`, and extends `ShortcutsWindow` with two rows. Task 4 smoke-tests end-to-end and tags `phase5c`.
