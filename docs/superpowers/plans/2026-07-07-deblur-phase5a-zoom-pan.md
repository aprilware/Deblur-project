# Deblur Phase 5a Implementation Plan (Preview Zoom + Pan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mouse-wheel zoom (toward cursor) + middle-mouse-drag pan on the preview canvas, with the drag arrow's Angle/Length math corrected to stay in image pixels at any zoom level.

**Architecture:** Wrap the existing `PreviewImage` in an inner `ContentHost` Grid that carries a `TransformGroup` (ScaleTransform + TranslateTransform) via `RenderTransform`. The overlay `Canvas` (arrow shaft + head) stays as an untransformed sibling of `ContentHost` so the arrow's stroke width doesn't grow with zoom. `OnMouseWheel` clamps zoom to `[0.1, 10.0]` and adjusts Translate to keep the pixel under the cursor stationary. `OnMouseMove`'s pan branch runs before the arrow branch. `OnSourceChanged` resets zoom to 1.0 and translate to zero. `ToImageSpace` divides screen deltas by `_displayScale * _zoom`.

**Tech Stack:** .NET 8 (`net8.0-windows` WPF), WPF-only change — no engine touch, no new NuGet packages, no ViewModel changes.

## Global Constraints

- Target framework: `net8.0-windows` for the WPF `Deblur` project. `Nullable` and `ImplicitUsings` enabled.
- No new NuGet packages.
- Only `Deblur/Controls/PreviewCanvas.xaml` and `Deblur/Controls/PreviewCanvas.xaml.cs` are modified. Engine, tests, `MainViewModel`, `MainWindow.xaml`, `App.xaml` all untouched.
- All 53 phase-4 tests remain green throughout every task.
- Zoom multiplier per wheel notch: `1.2` (up) or `1/1.2` (down).
- Zoom clamped to `[0.1, 10.0]`.
- Zoom-to-cursor formula: `t_new = cursor - (cursor - t_old) * (s_new / s_old)`.
- Pan trigger: middle mouse button drag. Cursor becomes `Cursors.SizeAll` while panning.
- `OnSourceChanged` resets `_zoom = 1.0`, `Scale.ScaleX = Scale.ScaleY = 1.0`, `Translate.X = Translate.Y = 0.0`.
- `ToImageSpace` uses `_displayScale * _zoom` as the effective scale.
- `IsArrowEnabled` DP (phase 3) is UNCHANGED — arrow behavior under non-Motion modes stays as-is.
- Phase 5a branches from tag `phase4` onto branch `phase5a-zoom-pan`.

---

### Task 1: XAML transform structure + state fields + reset

**Files:**
- Modify: `Deblur/Controls/PreviewCanvas.xaml`
- Modify: `Deblur/Controls/PreviewCanvas.xaml.cs`

**Interfaces:**
- Consumes: existing `PreviewImage`, `OverlayCanvas`, `ArrowShaft`, `ArrowHead`.
- Produces: named XAML elements `Scale` (ScaleTransform) and `Translate` (TranslateTransform) on a new `ContentHost` Grid. A `_zoom` field defaulted to `1.0`. `OnSourceChanged` now resets these to identity when a new image is assigned.

- [ ] **Step 1: Restructure `PreviewCanvas.xaml`**

Replace the entire content of `Deblur/Controls/PreviewCanvas.xaml` with:
```xml
<UserControl x:Class="Deblur.Controls.PreviewCanvas"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="#222">
    <Grid x:Name="Viewport" ClipToBounds="True">
        <Grid x:Name="ContentHost">
            <Grid.RenderTransform>
                <TransformGroup>
                    <ScaleTransform x:Name="Scale" ScaleX="1" ScaleY="1"/>
                    <TranslateTransform x:Name="Translate" X="0" Y="0"/>
                </TransformGroup>
            </Grid.RenderTransform>
            <Image x:Name="PreviewImage"
                   Stretch="Uniform"
                   RenderOptions.BitmapScalingMode="HighQuality"/>
        </Grid>
        <Canvas x:Name="OverlayCanvas" IsHitTestVisible="False">
            <Line x:Name="ArrowShaft" Stroke="#FFEE33" StrokeThickness="2" Visibility="Collapsed"/>
            <Polygon x:Name="ArrowHead" Fill="#FFEE33" Visibility="Collapsed"/>
        </Canvas>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Add the `_zoom` state field in `PreviewCanvas.xaml.cs`**

Find the existing state fields (after the `IsArrowEnabled` DP and events, near `private Point? _dragStartScreen;`). Add directly beneath the existing `_dragStartScreen` and `_displayScale` fields:
```csharp
    private double _zoom = 1.0;
```

- [ ] **Step 3: Reset transform state in `OnSourceChanged`**

Locate the existing `OnSourceChanged` static method (currently 5 lines: assigns `PreviewImage.Source`, clears `_dragStartScreen`, collapses the arrow, and releases capture). Replace it with:
```csharp
    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PreviewCanvas)d;
        self.PreviewImage.Source = (WriteableBitmap?)e.NewValue;
        self._dragStartScreen = null;
        self.ArrowShaft.Visibility = self.ArrowHead.Visibility = Visibility.Collapsed;
        self.ReleaseMouseCapture();

        // Reset the view transform so a fresh image loads fit-to-window at 1x.
        self._zoom = 1.0;
        self.Scale.ScaleX = self.Scale.ScaleY = 1.0;
        self.Translate.X = self.Translate.Y = 0.0;
    }
```

- [ ] **Step 4: Build and confirm no regressions**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors. The visual tree now has a transform group at identity — no visible behavior change yet.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 53`.

- [ ] **Step 5: Commit**

```bash
git add Deblur/Controls/PreviewCanvas.xaml Deblur/Controls/PreviewCanvas.xaml.cs
git commit -m "Add ContentHost transform group and zoom state to PreviewCanvas"
```

---

### Task 2: Mouse-wheel zoom toward cursor

**Files:**
- Modify: `Deblur/Controls/PreviewCanvas.xaml.cs`

**Interfaces:**
- Consumes: `Scale`, `Translate` (from Task 1's XAML), `_zoom` field (from Task 1).
- Produces: an `OnMouseWheel` handler wired in the constructor. After this task, rotating the wheel over the preview magnifies/demagnifies the pixel under the cursor and keeps it stationary. Zoom is clamped to `[0.1, 10.0]`.

- [ ] **Step 1: Wire `MouseWheel` in the constructor**

Locate the `PreviewCanvas()` constructor (already wires `MouseLeftButtonDown`, `MouseMove`, `MouseLeftButtonUp`, `MouseLeave`). Add one line after the existing wiring, before the closing brace:
```csharp
        MouseWheel += OnMouseWheel;
```

- [ ] **Step 2: Add the `OnMouseWheel` handler**

Add this method inside the class, near the existing mouse handlers (e.g., right after `OnMouseLeave`):
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
```

- [ ] **Step 3: Build and confirm no regressions**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors. Any new warnings on `PreviewCanvas.xaml.cs` are findings.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 53`.

- [ ] **Step 4: Commit**

```bash
git add Deblur/Controls/PreviewCanvas.xaml.cs
git commit -m "Add mouse-wheel zoom toward cursor to PreviewCanvas"
```

---

### Task 3: Middle-mouse-drag pan + arrow math scale correction

**Files:**
- Modify: `Deblur/Controls/PreviewCanvas.xaml.cs`

**Interfaces:**
- Consumes: `Scale`, `Translate`, `_zoom`, `_displayScale`, existing `_dragStartScreen` field.
- Produces: `OnAnyMouseDown` / `OnAnyMouseUp` middle-button handlers, a middle-drag branch at the top of `OnMouseMove`, and an updated `ToImageSpace` that divides by `_displayScale * _zoom`. After this task, middle-drag pans the image and left-drag under Motion produces correct Angle/Length at any zoom.

- [ ] **Step 1: Add pan state fields**

Below the existing `_zoom = 1.0;` line (from Task 1), add:
```csharp
    private Point? _panStartScreen;
    private Point _panStartTranslate;
```

- [ ] **Step 2: Wire the general `MouseDown` and `MouseUp` events in the constructor**

The existing constructor wires `MouseLeftButtonDown` / `MouseLeftButtonUp`. Add two lines wiring the general `MouseDown` / `MouseUp` for middle-button handling, directly beneath the existing lines (before the `MouseWheel += OnMouseWheel;` from Task 2 is fine):
```csharp
        MouseDown += OnAnyMouseDown;
        MouseUp += OnAnyMouseUp;
```

- [ ] **Step 3: Add the middle-button handlers**

Add these methods inside the class near the other mouse handlers:
```csharp
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

- [ ] **Step 4: Add the middle-drag branch to `OnMouseMove`**

Locate the existing `OnMouseMove` handler (currently: guards on `_dragStartScreen is null || Source is null`, then updates the arrow and fires `Dragging`). Replace the entire method with:
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

        if (_dragStartScreen is null || Source is null) return;
        var arrowCur = e.GetPosition(this);
        UpdateArrow(_dragStartScreen.Value, arrowCur);
        var (angle, length) = ToImageSpace(_dragStartScreen.Value, arrowCur);
        Dragging?.Invoke(this, new ArrowDragEventArgs { Angle = angle, Length = length });
    }
```

- [ ] **Step 5: Update `ToImageSpace` to account for zoom**

Locate the existing `ToImageSpace` method (currently divides by `_displayScale`). Replace it with:
```csharp
    private (float angle, float length) ToImageSpace(Point start, Point cur)
    {
        double effectiveScale = _displayScale * _zoom;
        double dx = (cur.X - start.X) / effectiveScale;
        double dy = (cur.Y - start.Y) / effectiveScale;
        double lenPx = Math.Sqrt(dx * dx + dy * dy);
        double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (angleDeg < 0) angleDeg += 360.0;
        double clampedLen = Math.Clamp(lenPx, 1.0, 100.0);
        return ((float)angleDeg, (float)clampedLen);
    }
```

- [ ] **Step 6: Build and confirm no regressions**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors. Any new warnings on `PreviewCanvas.xaml.cs` are findings.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 53`.

- [ ] **Step 7: Commit**

```bash
git add Deblur/Controls/PreviewCanvas.xaml.cs
git commit -m "Add middle-mouse-drag pan and zoom-aware arrow math"
```

---

### Task 4: Manual smoke test pass + tag `phase5a`

**Files:** none.

**Interfaces:** none.

- [ ] **Step 1: Run the app**

```bash
dotnet run --project Deblur/Deblur.csproj
```

Walk through the checklist:

- [ ] Launch app and open a PNG.
- [ ] Rotate the mouse wheel up while hovering over a specific feature — that feature magnifies and stays under the cursor.
- [ ] Rotate wheel down — image shrinks, ultimately below 1×. Confirm can't go below `0.1×` (~7 wheel notches down from 1.0).
- [ ] Wheel up to maximum — confirm can't go above `10.0×` (~13 wheel notches up from 1.0).
- [ ] Middle-click and drag on the preview — image translates with the cursor. Cursor becomes a four-arrow hand while dragging. Releasing the middle button restores the arrow cursor.
- [ ] Under Motion (default), left-click-drag at 1× — arrow renders, Angle/Length sliders update.
- [ ] Zoom in to ~2× (a couple of wheel notches). Left-click-drag over the SAME visual endpoint as before — Angle should be the same, Length should be roughly the same image-pixel value (not doubled).
- [ ] Switch blur type to OutOfFocus — arrow does NOT render on left-drag. Zoom and pan still work.
- [ ] Switch to Gaussian — same: no arrow, zoom + pan work.
- [ ] Open a second image via File → Open — view resets to fit-to-window at 1× regardless of prior zoom/pan.
- [ ] Drag-drop a second image — same reset behavior.
- [ ] Render full resolution + Save As → PNG at a high zoom level. Reopen the saved file externally — image is at full resolution and unaffected by the display zoom.
- [ ] Zoom to ~3×, middle-drag the image partially off-screen; the sidebar/status area is not overwritten (clip works).
- [ ] Progress bar behavior unchanged from phase 4 (thin indeterminate bar during compute).

- [ ] **Step 2: Commit any smoke-test-triggered fixes**

If the smoke test surfaces bugs, fix them and commit each fix separately with a message describing the failure and the fix. If nothing was wrong, no commit is needed for this step.

- [ ] **Step 3: Tag phase 5a complete**

```bash
git tag phase5a
```

---

## Summary

Four tasks, each an independently reviewable commit. Task 1 restructures the XAML visual tree with a transform group + `ClipToBounds` viewport and adds the `_zoom` state field with a reset in `OnSourceChanged`. Task 2 adds mouse-wheel zoom toward the cursor with `[0.1, 10.0]` clamping. Task 3 adds middle-mouse-drag pan and updates the drag-arrow math to divide by `_displayScale * _zoom` so Angle/Length stay in image pixels at any zoom. Task 4 smoke-tests end-to-end and tags `phase5a`.
