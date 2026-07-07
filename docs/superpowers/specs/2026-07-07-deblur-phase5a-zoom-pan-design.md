# Deblur — Phase 5a Design (Preview Zoom + Pan)

**Date:** 2026-07-07
**Status:** Approved
**Scope:** First mini-phase of phase 5. Zoom + pan on the preview pane only.

## Context and phasing

Phase 5 in the original roadmap covered "polish: zoom/pan, keyboard shortcuts, batch, undo, cancellation." That description bundles five independent subsystems plus the deferred phase-4b Total Variation deconvolver. Rather than a single mega-phase, phase 5 is decomposed into six mini-phases; this spec (phase 5a) covers only the first — mouse-wheel zoom + middle-drag pan on the preview canvas.

Mini-phase roadmap (each gets its own spec + plan + tag):

- **Phase 5a (this spec)** — Zoom + pan.
- **Phase 5b** — Keyboard shortcuts (Ctrl+O, Ctrl+S, Ctrl+Z, Ctrl+R, and zoom shortcuts like Ctrl+0 fit / Ctrl+1 1:1).
- **Phase 5c** — Undo/redo of parameter changes.
- **Phase 5d** — Batch processing.
- **Phase 5e** — Full-res render cancellation.
- **Phase 4b** — Total Variation deconvolver (still deferred; may land before 5c–5e if the "algorithm axis" is the priority).

## Goal

A user viewing the deblur preview can:
- Roll the mouse wheel to zoom in and out. Zoom is centered on the pixel under the cursor.
- Middle-click and drag to pan the image around inside the preview pane.
- Load a new image; the view resets to fit-to-window at 1×.

Zoom is a purely display-side transform — pixels sent to `WriteableBitmap`, the FFT pipeline, and Save output are all unchanged. The drag-arrow (Motion mode) still produces the same image-space Angle/Length under any zoom, so slider values remain in image pixels.

## Non-goals

- "Fit to window" or "1:1 pixel" keyboard shortcuts (phase 5b).
- A zoom percentage readout, +/- toolbar buttons, or a minimap.
- Undo/redo of zoom+pan state — these are transient view controls, not model state.
- Any of the other phase-5 mini-phases.
- Rendering the arrow inside the transformed content (arrow stays screen-relative so it doesn't grow with zoom).
- Zoom bounds tied to image dimensions or DPI-aware zoom (fixed `[0.1, 10.0]` range).

## Approach

Wrap the existing `PreviewImage` in a new inner `ContentHost` Grid that carries a `TransformGroup` on its `RenderTransform`: a `ScaleTransform` and a `TranslateTransform`. The overlay `Canvas` (arrow shaft + head) stays as an untransformed sibling — inside the outer `ClipToBounds="True"` viewport Grid but outside the transformed content. That keeps the arrow's stroke width and head size fixed on screen regardless of zoom, while the shaft's *length* still tracks the drag correctly because it's redrawn from mouse-move deltas in screen coords.

Mouse-wheel zoom uses a fixed 1.2× multiplier per notch, clamped to `[0.1, 10.0]`. On each wheel step, the Translate is adjusted so the pixel under the cursor stays under the cursor: `t_new = cursor - (cursor - t_old) * (s_new / s_old)`.

Middle-mouse-drag pans by the raw screen delta (no scale factor — panning is in viewport pixels). The mouse is captured on middle-button-down and released on up. Cursor changes to `SizeAll` (four-arrow hand) while panning.

The existing left-button drag-arrow (Motion mode) continues to work; its `ToImageSpace(start, cur)` divides the screen-space delta by `_displayScale * _zoom` instead of just `_displayScale`, so a screen drag at 2× produces the same image-pixel Angle/Length as a drag of the same visual endpoint at 1×.

`OnSourceChanged` resets `Scale = 1`, `Translate = (0, 0)`, and `_zoom = 1.0` so each new image starts fit-to-window.

## Solution layout

No new files. Only `Deblur/Controls/PreviewCanvas.xaml` and `Deblur/Controls/PreviewCanvas.xaml.cs` are modified. No engine changes, no `MainViewModel` changes, no `MainWindow.xaml` changes.

## Components

### `Deblur/Controls/PreviewCanvas.xaml`

Structural change to the visual tree. The current shape is:

```xml
<UserControl>
  <Grid>
    <Image x:Name="PreviewImage" Stretch="Uniform" .../>
    <Canvas x:Name="OverlayCanvas" IsHitTestVisible="False">
      <Line x:Name="ArrowShaft" .../>
      <Polygon x:Name="ArrowHead" .../>
    </Canvas>
  </Grid>
</UserControl>
```

New shape:

```xml
<UserControl>
  <Grid x:Name="Viewport" ClipToBounds="True">
    <Grid x:Name="ContentHost">
      <Grid.RenderTransform>
        <TransformGroup>
          <ScaleTransform x:Name="Scale" ScaleX="1" ScaleY="1"/>
          <TranslateTransform x:Name="Translate" X="0" Y="0"/>
        </TransformGroup>
      </Grid.RenderTransform>
      <Image x:Name="PreviewImage" Stretch="Uniform" .../>
    </Grid>
    <Canvas x:Name="OverlayCanvas" IsHitTestVisible="False">
      <Line x:Name="ArrowShaft" .../>
      <Polygon x:Name="ArrowHead" .../>
    </Canvas>
  </Grid>
</UserControl>
```

The outer `Viewport` Grid provides `ClipToBounds="True"` so panning past the pane edges doesn't overflow into the sidebar or status area. The overlay `Canvas` is a sibling of `ContentHost`, not a child — its coordinates stay in raw UserControl space.

### `Deblur/Controls/PreviewCanvas.xaml.cs`

New state fields:
- `private double _zoom = 1.0;`
- `private Point? _panStartScreen;`
- `private Point _panStartTranslate;`

Constructor additions (in the existing `PreviewCanvas()` after `InitializeComponent()`):
- Wire `MouseWheel += OnMouseWheel;`
- Wire `MouseDown += OnAnyMouseDown;` (for the middle-button handling — the existing `MouseLeftButtonDown` is unchanged).
- Wire `MouseUp += OnAnyMouseUp;`

New handlers:

```csharp
private void OnMouseWheel(object sender, MouseWheelEventArgs e)
{
    if (Source is null) return;
    var cursor = e.GetPosition(this);
    double factor = e.Delta > 0 ? 1.2 : 1.0 / 1.2;
    double newZoom = Math.Clamp(_zoom * factor, 0.1, 10.0);
    if (Math.Abs(newZoom - _zoom) < 1e-6) return;

    // Zoom toward the cursor: keep the point under the cursor stationary.
    double ratio = newZoom / _zoom;
    Translate.X = cursor.X - (cursor.X - Translate.X) * ratio;
    Translate.Y = cursor.Y - (cursor.Y - Translate.Y) * ratio;
    Scale.ScaleX = Scale.ScaleY = newZoom;
    _zoom = newZoom;
    e.Handled = true;
}

private void OnAnyMouseDown(object sender, MouseButtonEventArgs e)
{
    if (e.ChangedButton != MouseButton.Middle || Source is null) return;
    _panStartScreen = e.GetPosition(this);
    _panStartTranslate = new Point(Translate.X, Translate.Y);
    Cursor = System.Windows.Input.Cursors.SizeAll;
    CaptureMouse();
    e.Handled = true;
}

private void OnAnyMouseUp(object sender, MouseButtonEventArgs e)
{
    if (e.ChangedButton != MouseButton.Middle || _panStartScreen is null) return;
    _panStartScreen = null;
    Cursor = System.Windows.Input.Cursors.Arrow;
    ReleaseMouseCapture();
    e.Handled = true;
}
```

Modification to the existing `OnMouseMove` handler (which currently only handles left-drag): fold in the middle-drag branch first:

```csharp
private void OnMouseMove(object sender, MouseEventArgs e)
{
    if (_panStartScreen is not null)
    {
        var cur = e.GetPosition(this);
        Translate.X = _panStartTranslate.X + (cur.X - _panStartScreen.Value.X);
        Translate.Y = _panStartTranslate.Y + (cur.Y - _panStartScreen.Value.Y);
        return;
    }
    // ... existing left-drag arrow branch unchanged ...
}
```

Modification to `OnSourceChanged`:

```csharp
private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
{
    var self = (PreviewCanvas)d;
    self.PreviewImage.Source = (WriteableBitmap?)e.NewValue;
    self._dragStartScreen = null;
    self.ArrowShaft.Visibility = self.ArrowHead.Visibility = Visibility.Collapsed;
    self.ReleaseMouseCapture();

    // Reset the view transform so a fresh image loads fit-to-window.
    self._zoom = 1.0;
    self.Scale.ScaleX = self.Scale.ScaleY = 1.0;
    self.Translate.X = self.Translate.Y = 0.0;
}
```

Modification to `ToImageSpace`:

```csharp
private (float angle, float length) ToImageSpace(Point start, Point cur)
{
    double effectiveScale = _displayScale * _zoom;
    double dx = (cur.X - start.X) / effectiveScale;
    double dy = (cur.Y - start.Y) / effectiveScale;
    // ... rest unchanged ...
}
```

Everything else (`OnMouseLeave`, `UpdateArrow`, `UpdateDisplayScale`, `ReflectIndex`, the `IsArrowEnabled` DP and its guard in `OnMouseDown`) is unchanged.

## Data flow

1. User rotates the mouse wheel over the preview → `OnMouseWheel` computes the new zoom and translate, updates `Scale.ScaleX/Y` and `Translate.X/Y`. WPF re-renders on the next frame. The pixel under the cursor stays put. `_zoom` is now the effective magnification.
2. User presses middle-mouse and drags → `OnAnyMouseDown` captures the mouse and stores start-of-pan state; `OnMouseMove` (middle-drag branch) translates by the raw screen delta; `OnAnyMouseUp` releases capture.
3. User loads a new image via File → Open or drag-drop → `MainViewModel.LoadImageFromBytes` reassigns `PreviewBitmap` → `Source` DP change → `OnSourceChanged` resets zoom and translate.
4. Under Motion, user does a left-click-drag → `OnMouseDown` (left branch, unchanged) starts the arrow; `OnMouseMove` (left branch, unchanged except for the `_panStartScreen` short-circuit at the top) calls the updated `ToImageSpace(start, cur)` which now divides by `_displayScale * _zoom`; the `Dragging` event still carries image-pixel Angle/Length to the ViewModel.
5. Save / Render full resolution — no change. The transform is display-only; the pipeline reads `_originalFullRes` and writes `_fullResBuffer` at native resolution.

## Error handling

- `_displayScale * _zoom` cannot be zero — `_displayScale` has an existing `> 0` guard in `UpdateDisplayScale`, and `_zoom` is clamped to `>= 0.1` in `OnMouseWheel`.
- Middle-drag while `Source is null` is guarded in `OnAnyMouseDown`. If Source becomes null mid-pan, `OnSourceChanged` releases mouse capture (already does this for the left-drag arrow) which implicitly cancels the pan.
- Extreme zoom + pan positions can move the entire image off-screen; users can reset by loading any image (Source change) or pressing something in phase 5b's Ctrl+0 shortcut (out of scope here).

## Testing philosophy

Pure WPF UI change — no engine work. No unit tests added. Manual smoke follows the phase-1..4 pattern with new items:

- Load image → wheel-up over a specific feature → that feature magnifies and stays under the cursor. Wheel-down zooms out.
- Zoom below 1.0 (down to 0.1) → image shrinks below fit-to-window.
- Middle-click and drag → image translates with the cursor. Cursor becomes a four-arrow hand while dragging.
- Load a second image → view resets to fit-to-window at 1× regardless of prior zoom/pan.
- Under Motion, drag arrow at 2× zoom; drag the same visual endpoint at 1× — Angle and Length sliders read the same values in both cases (they're in image pixels).
- Under OutOfFocus/Gaussian, arrow does not render on left-drag; zoom + pan still work.
- Full-res render + Save under any zoom level — reopened saved file is unchanged (no zoom pixels baked in).
- Wheel at an extreme zoom-out (say 0.1×) → doesn't go lower. Wheel at extreme zoom-in (10.0×) → doesn't go higher.

## Compatibility

- All 53 phase-4 tests remain green (no engine changes).
- No existing WPF binding changes; `MainViewModel`, `MainWindow.xaml`, and every kernel/deconvolver untouched.
- The `IsArrowEnabled` DP and its guard from phase 3 remain effective — arrow behavior under non-Motion modes is unchanged.
- Phase-4 tag remains anchored. Phase 5a lands on a new branch `phase5a-zoom-pan`.
