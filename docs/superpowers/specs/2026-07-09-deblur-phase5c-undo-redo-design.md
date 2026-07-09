# Deblur — Phase 5c Design (Undo / Redo of Parameter Changes)

**Date:** 2026-07-09
**Status:** Approved
**Scope:** Third mini-phase of phase 5. Undo/redo of parameter changes only.

## Context

Phases 1-4 delivered the deconvolution pipeline; phases 5a (zoom+pan), 5b (keyboard shortcuts), 5e (cancellation) are shipped. This spec covers 5c: an undo/redo stack of `KernelParams` snapshots so the user can step back through their tuning history via Ctrl+Z / Ctrl+Y.

## Goal

The user can press Ctrl+Z to revert the last committed parameter change (slider drag-commit, blur-type switch, algorithm switch, or Reset) and Ctrl+Y to re-apply it. The preview + status update immediately as if the user had made the change again by hand.

## Non-goals

- Snapshotting every intermediate slider tick during a drag (would explode the stack; user could never step back past a single drag).
- Persisting history across app sessions.
- Multi-image history (each new image load clears the stack).
- Any of the other phase-5 mini-phases (5d batch, 4b TV).

## Approach

New `Deblur/ViewModels/ParamHistory.cs` — two `Stack<KernelParams>` (past + future) plus a bounded capacity (50 entries; pop-oldest on overflow). Public API: `Push(KernelParams)`, `TryUndo(out KernelParams previous)`, `TryRedo(out KernelParams next)`, `Clear()`, `CanUndo`/`CanRedo` bools.

`MainViewModel` owns a `ParamHistory _history` and a `_suppressHistory` guard flag. Snapshots are pushed ONLY at commit-shaped events:
- Drag arrow commit (`UpdateKernel` — currently called from `Dragging` AND `DragCommitted`; the spec narrows it to `DragCommitted` only for history purposes via a new `Vm.CommitArrowDrag(angle, length)` method that's called from `OnPreviewDragCommitted` alone; `UpdateKernel` from `Dragging` continues to update sliders without snapshotting).
- Blur-type dropdown change (`OnSelectedBlurTypeChanged`).
- Algorithm dropdown change (`OnSelectedAlgorithmChanged`).
- `Reset()` button.
- New image load (via `LoadImageFromBytes`) — clears the history instead of pushing.

Slider drag ticks (mouse-move on the arrow, mid-slider changes) do NOT snapshot. This gives one history entry per user "commit action" without exploding the stack.

`Undo()` / `Redo()` on `MainViewModel` pop the target snapshot, set `_suppressHistory = true`, assign each property from the snapshot (which triggers `OnXxxChanged` partial methods that call `PushCurrentParams` — proxy re-renders normally), then clear `_suppressHistory = false` and fire `OnPropertyChanged(nameof(CanUndo)) / (CanRedo))`. Because `_suppressHistory` is checked at the top of the snapshot-pushing code path, undo/redo doesn't re-record.

`AppCommands` gains `Undo` (Ctrl+Z) and `Redo` (Ctrl+Y). `MainWindow.xaml` adds two `KeyBinding`s + two `CommandBinding`s + `CanExecute` handlers that gate on `Vm.CanUndo` / `Vm.CanRedo`. `ShortcutsWindow.xaml` gets two new rows.

## Files touched

- `Deblur/ViewModels/ParamHistory.cs` (new)
- `Deblur/ViewModels/MainViewModel.cs` — history field, `_suppressHistory` guard, `PushSnapshot()` helper, `Undo()` / `Redo()` methods, `CanUndo` / `CanRedo` computed, `CommitArrowDrag(angle, length)` method, edits to `OnSelectedBlurTypeChanged`, `OnSelectedAlgorithmChanged`, `Reset()`, `LoadImageFromBytes`.
- `Deblur/AppCommands.cs` — Undo (Ctrl+Z), Redo (Ctrl+Y).
- `Deblur/MainWindow.xaml` — two KeyBindings, two CommandBindings with CanExecute.
- `Deblur/MainWindow.xaml.cs` — `OnUndoExecuted` / `OnRedoExecuted` handlers, `CanExecute` handlers, change `OnPreviewDragCommitted` to call `Vm.CommitArrowDrag` instead of `Vm.UpdateKernel`.
- `Deblur/ShortcutsWindow.xaml` — two new rows (Ctrl+Z Undo, Ctrl+Y Redo).
- `Deblur.Tests/ParamHistoryTests.cs` (new) — unit tests on the bounded stack + undo/redo state machine.

## Constraints

- .NET 8. `net8.0-windows` WPF, `net8.0` Engine + Tests. Nullable + ImplicitUsings enabled.
- No new NuGet packages.
- `ParamHistory` lives in `Deblur/ViewModels/` (WPF-side) — it doesn't touch the engine, but it references `KernelParams` from `Deblur.Engine` which the WPF project already depends on.
- Bounded capacity: 50 entries. When Push overflows, the oldest past entry is dropped.
- On any successful Push, the redo (future) stack is cleared — standard "diverge branches on new edit" semantics.
- `_suppressHistory` guards the snapshot-recording path, not the parameter-setting path — sliders still fire `OnXxxChanged` and the runner still re-renders.
- All 54 phase-5e tests remain green; new `ParamHistoryTests` bring total to 60ish (small class, ~6 tests).

## Testing

Engine-adjacent unit tests on `ParamHistory` (this lives in the WPF project's namespace but is a pure C# class with no WPF dependencies — testable from `Deblur.Tests` if `Deblur.Tests` gets a project reference to `Deblur`). Alternative: put `ParamHistory` under `Deblur.Engine/` since it doesn't depend on WPF. **Decision: put it under `Deblur.Engine/` to avoid a new project reference from Tests → WPF.** `ParamHistory.cs` lives in `Deblur.Engine/ParamHistory.cs`, in namespace `Deblur.Engine`.

Tests:
- `Empty_CanUndoFalse_CanRedoFalse`.
- `Push_MakesUndoAvailable`.
- `TryUndo_ReturnsPushedValue_MakesRedoAvailable`.
- `TryRedo_ReturnsUndone_Value_MakesUndoAvailable`.
- `PushAfterUndo_ClearsRedoStack`.
- `Capacity50_DropsOldestOnOverflow`.

Manual smoke:
- Load image, drag arrow to Angle=45 → release. Ctrl+Z → arrow returns to Angle=0. Ctrl+Y → arrow back to 45.
- Do a series of drag-commits + a Reset + a blur-type switch. Step back through them one at a time.
- Ctrl+Z at start-of-history (empty stack) does nothing (no error).
- Load a new image → history clears (Ctrl+Z no longer restores earlier state).
- Verify Ctrl+Z / Ctrl+Y also appear in Help → Keyboard Shortcuts.

## Branch

Phase 5c branches from tag `phase5e` onto `phase5c-undo-redo`.
