# Deblur — Phase 5b Design (Keyboard Shortcuts + Discovery)

**Date:** 2026-07-07
**Status:** Approved
**Scope:** Second mini-phase of phase 5. Keyboard shortcuts and their in-app discovery only.

## Context and phasing

Phase 5 was decomposed during phase-5a brainstorming into five mini-phases plus the deferred phase-4b Total Variation deconvolver. Phase 5a shipped mouse-wheel zoom + middle-drag pan on the preview canvas (tag `phase5a`). This spec covers phase 5b: keyboard shortcuts for the most common actions (file open/save, reset, zoom fit / 1:1 / in / out, render full, cancel interaction, help) plus two discovery mechanisms — inline shortcut hints on menu items, and a modal "Keyboard Shortcuts" window listing every accelerator.

Mini-phase roadmap position:

- **Phase 5a (shipped, tag `phase5a`)** — Preview zoom + pan.
- **Phase 5b (this spec)** — Keyboard shortcuts + discovery UI.
- **Phase 5c** — Undo/redo of parameter changes (unlocks Ctrl+Z / Ctrl+Y).
- **Phase 5d** — Batch processing.
- **Phase 5e** — Full-res render cancellation.
- **Phase 4b** — Total Variation deconvolver (still deferred).

## Goal

A user can drive the whole common workflow from the keyboard:

- **File:** Ctrl+O Open, Ctrl+S Save As
- **Params:** Ctrl+R Reset
- **Zoom:** Ctrl+0 Fit-to-window, Ctrl+1 1:1 pixel-perfect, Ctrl++ zoom in, Ctrl+- zoom out
- **Render:** F5 Render full resolution
- **Interaction:** Esc cancel drag/pan
- **Help:** F1 Keyboard Shortcuts…

Discovery is provided in two places: menu items whose action has a shortcut display the accelerator text on the right (`InputGestureText`) as WPF renders it automatically; a Help menu with a Keyboard Shortcuts… item (also bound to F1) opens a small modal `ShortcutsWindow` listing every accelerator in a two-column table.

## Non-goals

- Undo/redo shortcuts (Ctrl+Z / Ctrl+Y) — belong to phase 5c after the history stack lands.
- Ctrl+Q Exit — Alt+F4 already closes the window; adding a second binding is clutter.
- Arrow-key nudge translate — not requested; would add a fourth keyboard interaction model to an app that already has wheel + middle-drag + arrow-drag.
- User-rebindable shortcuts / a preferences UI.
- A zoom percentage HUD on the preview (deferred; the ShortcutsWindow suffices for discoverability).
- Any of the other phase-5 mini-phases (5c undo, 5d batch, 5e cancellation, 4b TV).
- Any engine, ViewModel, or blur-type changes.

## Approach

Adopt WPF's standard `RoutedUICommand` + `Window.InputBindings` + `Window.CommandBindings` pattern. Two existing built-in commands cover File → Open and Save As: `ApplicationCommands.Open` and `ApplicationCommands.SaveAs` carry the default Ctrl+O / Ctrl+S gestures for free. Everything else lives in a new `AppCommands` static class as `RoutedUICommand` fields, each constructed with its `KeyGesture`.

`MainWindow.xaml` gets one `<Window.InputBindings>` block wiring gestures that WPF doesn't already know about (the `AppCommands` set) and one `<Window.CommandBindings>` block routing every command (built-in and custom) to a code-behind `Executed` handler that either calls the existing method (for actions already wired to a button/menu Click) or the new API on `PreviewCanvas` (for the zoom shortcuts). Menu items switch from `Click="…"` to `Command="{x:Static local:AppCommands.…}"` or `Command="ApplicationCommands.Open"` so their `InputGestureText` renders automatically. Buttons in the sidebar switch to `Command=` bindings for the same reason and to keep menu/button/keyboard all firing the same routed command.

`PreviewCanvas` gains four public methods so `MainWindow` can drive zoom and cancellation without touching internals: `FitToWindow()`, `PixelPerfect()`, `Zoom(double factor)`, `CancelInteraction()`.

A new `ShortcutsWindow` (small modal Window with a two-column list of shortcuts and a Close button) opens via `AppCommands.ShowShortcuts` bound to F1 and to a new "Help → Keyboard Shortcuts…" menu item.

No engine changes, no ViewModel changes, no new NuGet packages.

## Solution layout

Three new files under `Deblur/`; three existing files modified:

```
Deblur.sln
├── Deblur/
│   ├── AppCommands.cs                 ← NEW: RoutedUICommand static class
│   ├── ShortcutsWindow.xaml           ← NEW: modal shortcut reference
│   ├── ShortcutsWindow.xaml.cs        ← NEW: 2-line code-behind
│   ├── MainWindow.xaml                ← MODIFIED: InputBindings + CommandBindings + Help menu + Command="..." on menu items and Reset/Render buttons
│   ├── MainWindow.xaml.cs             ← MODIFIED: Executed handlers replace Click handlers; new zoom/help/cancel handlers
│   └── Controls/PreviewCanvas.xaml.cs ← MODIFIED: 4 new public methods
├── Deblur.Engine/                     ← unchanged
└── Deblur.Tests/                      ← unchanged
```

## Components

### `Deblur/AppCommands.cs`

Static class with `public static readonly RoutedUICommand` fields:

```csharp
using System.Windows.Input;

namespace Deblur;

public static class AppCommands
{
    public static readonly RoutedUICommand Reset =
        new("Reset", "Reset", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.R, ModifierKeys.Control) });

    public static readonly RoutedUICommand FitToWindow =
        new("Fit to window", "FitToWindow", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.D0, ModifierKeys.Control) });

    public static readonly RoutedUICommand PixelPerfect =
        new("1:1 pixel", "PixelPerfect", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.D1, ModifierKeys.Control) });

    public static readonly RoutedUICommand ZoomIn =
        new("Zoom in", "ZoomIn", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.OemPlus, ModifierKeys.Control) });

    public static readonly RoutedUICommand ZoomOut =
        new("Zoom out", "ZoomOut", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.OemMinus, ModifierKeys.Control) });

    public static readonly RoutedUICommand RenderFull =
        new("Render full resolution", "RenderFull", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.F5) });

    public static readonly RoutedUICommand ShowShortcuts =
        new("Keyboard shortcuts…", "ShowShortcuts", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.F1) });

    public static readonly RoutedUICommand CancelInteraction =
        new("Cancel interaction", "CancelInteraction", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.Escape) });
}
```

### `Deblur/Controls/PreviewCanvas.xaml.cs` (added members)

Four public methods:

```csharp
public void FitToWindow()
{
    _zoom = 1.0;
    Scale.ScaleX = Scale.ScaleY = 1.0;
    Translate.X = Translate.Y = 0.0;
}

public void PixelPerfect()
{
    if (Source is null) return;
    UpdateDisplayScale();
    if (_displayScale <= 0) return;
    double target = 1.0 / _displayScale;
    _zoom = Math.Clamp(target, 0.1, 10.0);
    Scale.ScaleX = Scale.ScaleY = _zoom;
    Translate.X = Translate.Y = 0.0;
}

public void Zoom(double factor)
{
    if (Source is null) return;
    double newZoom = Math.Clamp(_zoom * factor, 0.1, 10.0);
    if (Math.Abs(newZoom - _zoom) < 1e-6) return;

    // Keyboard zoom has no cursor position — anchor at the pane center.
    var center = new Point(ActualWidth / 2, ActualHeight / 2);
    double ratio = newZoom / _zoom;
    Translate.X = center.X - (center.X - Translate.X) * ratio;
    Translate.Y = center.Y - (center.Y - Translate.Y) * ratio;
    Scale.ScaleX = Scale.ScaleY = newZoom;
    _zoom = newZoom;
}

public void CancelInteraction()
{
    _dragStartScreen = null;
    _panStartScreen = null;
    ArrowShaft.Visibility = ArrowHead.Visibility = Visibility.Collapsed;
    Cursor = System.Windows.Input.Cursors.Arrow;
    ReleaseMouseCapture();
}
```

`FitToWindow` matches `OnSourceChanged`'s reset semantics — a hard snap to identity. `PixelPerfect` uses `UpdateDisplayScale()` (which is already computed lazily on mouse-down) to derive the display-fit scale; multiplying by `1 / _displayScale` makes `_displayScale * _zoom == 1.0`, i.e., one image pixel = one screen pixel. `Zoom(factor)` mirrors `OnMouseWheel`'s math but anchors at pane center rather than cursor. `CancelInteraction` combines the arrow-cancel path (currently in `OnMouseLeave`) and the pan-cancel path (currently in `OnAnyMouseUp`) into one method for the Esc handler.

### `Deblur/ShortcutsWindow.xaml` + `.xaml.cs`

Small modal `Window`, ~420×360, non-resizable, `WindowStartupLocation="CenterOwner"`, title "Keyboard Shortcuts", `ResizeMode="NoResize"`, no icon in the title bar (`ShowInTaskbar="False"`, `WindowStyle="ToolWindow"`).

Body: an `ItemsControl` or a simple `Grid` with two-column rows (Shortcut | Action). Rows populated statically from XAML (hardcoded — the shortcut list is small and changes rarely; a data-bound approach would add avoidable indirection).

Close button at the bottom right, bound to `IsCancel="True"` so Escape also closes it. Code-behind is just `InitializeComponent()`.

Rows to display:

| Shortcut | Action |
|---|---|
| Ctrl+O | Open image |
| Ctrl+S | Save As |
| F5 | Render full resolution |
| Ctrl+R | Reset current blur type |
| Ctrl+0 | Fit to window |
| Ctrl+1 | 1:1 pixel |
| Ctrl++ | Zoom in |
| Ctrl+- | Zoom out |
| Esc | Cancel drag or pan |
| F1 | Show this window |

### `Deblur/MainWindow.xaml` changes

- Add namespace declaration: `xmlns:local="clr-namespace:Deblur"`.
- Add `<Window.InputBindings>` block with `KeyBinding` entries only for `AppCommands.*` (`ApplicationCommands.Open`/`SaveAs` gestures are built into WPF; MainWindow just needs the CommandBindings for them):

```xml
<Window.InputBindings>
    <!-- ApplicationCommands.Open ships with Ctrl+O built in; SaveAs has no default gesture, wire it here. -->
    <KeyBinding Key="S"          Modifiers="Ctrl" Command="ApplicationCommands.SaveAs"/>

    <KeyBinding Key="R"          Modifiers="Ctrl" Command="{x:Static local:AppCommands.Reset}"/>
    <KeyBinding Key="D0"         Modifiers="Ctrl" Command="{x:Static local:AppCommands.FitToWindow}"/>
    <KeyBinding Key="D1"         Modifiers="Ctrl" Command="{x:Static local:AppCommands.PixelPerfect}"/>
    <KeyBinding Key="OemPlus"    Modifiers="Ctrl" Command="{x:Static local:AppCommands.ZoomIn}"/>
    <KeyBinding Key="OemMinus"   Modifiers="Ctrl" Command="{x:Static local:AppCommands.ZoomOut}"/>
    <KeyBinding Key="F5"                          Command="{x:Static local:AppCommands.RenderFull}"/>
    <KeyBinding Key="F1"                          Command="{x:Static local:AppCommands.ShowShortcuts}"/>
    <KeyBinding Key="Escape"                      Command="{x:Static local:AppCommands.CancelInteraction}"/>
</Window.InputBindings>

<Window.CommandBindings>
    <CommandBinding Command="ApplicationCommands.Open"           Executed="OnOpenExecuted"/>
    <CommandBinding Command="ApplicationCommands.SaveAs"         Executed="OnSaveAsExecuted"/>
    <CommandBinding Command="{x:Static local:AppCommands.Reset}"             Executed="OnResetExecuted"/>
    <CommandBinding Command="{x:Static local:AppCommands.FitToWindow}"       Executed="OnFitExecuted"/>
    <CommandBinding Command="{x:Static local:AppCommands.PixelPerfect}"      Executed="OnPixelPerfectExecuted"/>
    <CommandBinding Command="{x:Static local:AppCommands.ZoomIn}"            Executed="OnZoomInExecuted"/>
    <CommandBinding Command="{x:Static local:AppCommands.ZoomOut}"           Executed="OnZoomOutExecuted"/>
    <CommandBinding Command="{x:Static local:AppCommands.RenderFull}"        Executed="OnRenderFullExecuted"/>
    <CommandBinding Command="{x:Static local:AppCommands.ShowShortcuts}"     Executed="OnShowShortcutsExecuted"/>
    <CommandBinding Command="{x:Static local:AppCommands.CancelInteraction}" Executed="OnCancelInteractionExecuted"/>
</Window.CommandBindings>
```

- Menu changes:
  - `<MenuItem Header="_Open..." Command="ApplicationCommands.Open"/>` (replaces `Click`).
  - `<MenuItem Header="_Save As..." Command="ApplicationCommands.SaveAs" InputGestureText="Ctrl+S"/>` (replaces `Click`; `SaveAs` has no default gesture text, so set it explicitly so the menu displays "Ctrl+S" on the right).
  - `<MenuItem Header="E_xit" Click="OnExitClick"/>` (unchanged — Alt+F4 handles the shortcut).
  - New `<MenuItem Header="_Help">` sibling of File with:
    - `<MenuItem Header="_Keyboard Shortcuts..." Command="{x:Static local:AppCommands.ShowShortcuts}"/>`.

- Sidebar Reset button: `<Button Content="Reset" Margin="0,12,0,0" Command="{x:Static local:AppCommands.Reset}"/>` (replaces `Click="OnResetClick"`).
- Sidebar Render button: `<Button Content="Render full resolution" Margin="0,8,0,0" Command="{x:Static local:AppCommands.RenderFull}"/>` (replaces `Click="OnRenderFullClick"`).

The `Command` bindings on menu items and buttons cause WPF to automatically render each item's `InputGestureText` — no separate attribute needed.

### `Deblur/MainWindow.xaml.cs` changes

Rename the existing Click handlers to Executed handlers with the new signature `(object sender, ExecutedRoutedEventArgs e)`. The behavior inside each is unchanged. Add new handlers for the new commands.

| Old handler | New handler | Body |
|---|---|---|
| `OnOpenClick` | `OnOpenExecuted` | unchanged body |
| `OnSaveAsClick` | `OnSaveAsExecuted` | unchanged body |
| `OnResetClick` | `OnResetExecuted` | `Vm.Reset();` |
| `OnRenderFullClick` | `OnRenderFullExecuted` | unchanged body |
| — | `OnFitExecuted` | `Preview.FitToWindow();` |
| — | `OnPixelPerfectExecuted` | `Preview.PixelPerfect();` |
| — | `OnZoomInExecuted` | `Preview.Zoom(1.2);` |
| — | `OnZoomOutExecuted` | `Preview.Zoom(1.0 / 1.2);` |
| — | `OnShowShortcutsExecuted` | `new ShortcutsWindow { Owner = this }.ShowDialog();` |
| — | `OnCancelInteractionExecuted` | `Preview.CancelInteraction();` |

`OnExitClick` (menu → Exit) stays a Click handler — no keyboard binding for it and no reason to route through a command.

Existing guards (`if (Vm.CurrentFilePath is null) { MessageBox.Show(...); return; }` in `OnSaveAsExecuted` and `OnRenderFullExecuted`, and the `Vm.IsBusy` guard in `OnOpenExecuted`) remain inside the handlers verbatim — cleaner than adding `CanExecute` handlers, and this is a phase-5b UX concern anyway (users hitting the shortcut before loading get the same modal they'd see clicking the menu).

The drag handlers (`OnPreviewDragging`, `OnPreviewDragCommitted`), drag-drop handlers (`OnFileDragEnter`, `OnFileDrop`), large-image guard in `LoadFile`, `Closed` disposer, and every other existing method stay verbatim.

## Data flow

1. User presses Ctrl+O → WPF looks up the gesture in `Window.InputBindings` (or in `ApplicationCommands.Open`'s built-in binding), fires `ApplicationCommands.Open` → `CommandBinding` matches → `OnOpenExecuted` runs → identical to today's `OnOpenClick`.
2. Ctrl+0 → `AppCommands.FitToWindow` → `OnFitExecuted` → `Preview.FitToWindow()` → transform snaps to identity.
3. Ctrl+1 → `Preview.PixelPerfect()` → computes `_zoom = 1 / _displayScale` (clamped), Scale updated, Translate zeroed.
4. Ctrl++/Ctrl+- → `Preview.Zoom(1.2)` / `Preview.Zoom(1/1.2)` → applies the wheel handler's clamp + math with pane center as anchor.
5. F1 → `Preview.ShortcutsWindow.ShowDialog()` → modal opens; Esc or Close button dismisses it.
6. Esc from main window → `AppCommands.CancelInteraction` → `Preview.CancelInteraction()` → arrow + pan state cleared.
7. Any menu item with an `InputGestureText` shows the shortcut string on hover automatically.

## Error handling

- `Preview.PixelPerfect()` and `Preview.Zoom()` no-op silently when `Source is null` — no image to zoom.
- `OnSaveAsExecuted` and `OnRenderFullExecuted` retain their existing "Open an image first" MessageBox guards.
- `OnOpenExecuted` retains the `Vm.IsBusy` early-return.
- `AppCommands` static field initialization order doesn't matter (each `RoutedUICommand` is independent).
- If the ShortcutsWindow is already open and the user presses F1 again, `ShowDialog()` blocks the parent thread until the first is closed; a second F1 press is a no-op (the parent isn't listening for key events while modal is up). Acceptable.

## Testing philosophy

Pure WPF UI change. No engine tests, no ViewModel tests added. Manual smoke follows the phase-1..5a pattern with these additions:

- File menu open → each item shows its accelerator (e.g. "Ctrl+O") right-justified.
- Ctrl+O opens the file dialog.
- Ctrl+S opens Save As (or shows "Open an image first" when no image).
- Ctrl+R resets the current blur type's params + Smoothness (identical to clicking Reset).
- With image loaded + zoomed/panned, Ctrl+0 snaps back to fit-to-window.
- Ctrl+1 shows 1:1 pixels (verify visually — image details appear crisp; if the image is larger than the pane the sides get clipped).
- Ctrl++ zooms in (1.2× per press); Ctrl+- zooms out. Bounds `[0.1, 10.0]` respected.
- F5 renders full resolution and shows the busy overlay.
- F1 opens the ShortcutsWindow; Esc or Close closes it; the parent regains focus.
- Under Motion mid-drag, Esc cancels the arrow; sliders retain their last committed value.
- Under any blur type mid-pan (middle-drag active), Esc cancels the pan; cursor restored.
- Help menu → Keyboard Shortcuts… opens the same window as F1.
- Reset button on the sidebar still works (via `Command` binding).
- Render full-res button on the sidebar still works (via `Command` binding).
- The existing arrow drag, drag-drop, and progress bar behavior are unchanged.

## Compatibility

- All 53 tests remain green (no engine changes; XAML/code-behind refactor only).
- Existing behaviors preserved: the arrow overlay, IsArrowEnabled DP, drag-drop, large-image guard, error modals, progress bar, Idle-under-lock + debounce.
- Menu Click handlers replaced by `Executed` handlers with identical bodies — no behavior change.
- The Reset and Render-full sidebar buttons switch from `Click=` to `Command=` bindings; users won't observe a difference except that Reset now also displays "Ctrl+R" in a tooltip if hovered (WPF adds this automatically for commands with gestures).
- `phase5a` tag remains anchored. Phase 5b lands on new branch `phase5b-keyboard-shortcuts`.
