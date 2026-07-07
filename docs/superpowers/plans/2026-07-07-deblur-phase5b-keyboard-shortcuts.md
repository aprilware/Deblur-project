# Deblur Phase 5b Implementation Plan (Keyboard Shortcuts + Discovery)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add keyboard shortcuts for the common workflow (open, save, reset, zoom fit/1:1/in/out, render, cancel drag, help) with in-app discovery via menu-item `InputGestureText` and a modal "Keyboard Shortcuts" window.

**Architecture:** New `AppCommands` static class holds `RoutedUICommand`s for actions not already covered by WPF's built-in `ApplicationCommands`. `MainWindow.xaml` gains `Window.InputBindings` (gestures WPF doesn't wire natively) and `Window.CommandBindings` (routing every command to an `Executed` handler). Menu items and sidebar buttons switch to `Command=` bindings so `InputGestureText` renders automatically. A new `ShortcutsWindow` (small modal) lists every accelerator; F1 and a new "Help → Keyboard Shortcuts…" menu item open it. `PreviewCanvas` exposes four public methods (`FitToWindow`, `PixelPerfect`, `Zoom(double factor)`, `CancelInteraction`) so `MainWindow` can drive zoom without touching internals.

**Tech Stack:** .NET 8 (`net8.0-windows` WPF), WPF-only change — no engine touch, no new NuGet packages, no ViewModel changes.

## Global Constraints

- Target framework: `net8.0-windows` for the WPF `Deblur` project. `Nullable` and `ImplicitUsings` enabled.
- No new NuGet packages.
- Files touched: `Deblur/AppCommands.cs` (new), `Deblur/ShortcutsWindow.xaml` (new), `Deblur/ShortcutsWindow.xaml.cs` (new), `Deblur/MainWindow.xaml`, `Deblur/MainWindow.xaml.cs`, `Deblur/Controls/PreviewCanvas.xaml.cs`. Engine, tests, ViewModel, other Controls untouched.
- All 53 phase-5a tests remain green throughout every task.
- Shortcut set: Ctrl+O Open, Ctrl+S Save As, Ctrl+R Reset, Ctrl+0 Fit to window, Ctrl+1 Pixel-perfect (1:1), Ctrl++ Zoom in (1.2×), Ctrl+- Zoom out (1/1.2), F5 Render full, F1 Show shortcuts, Esc Cancel interaction.
- Zoom shortcuts share the same `[0.1, 10.0]` clamp used by mouse-wheel zoom in phase 5a.
- Keyboard zoom anchors at the pane center (no cursor position on a keyboard event).
- `PixelPerfect` sets `_zoom = 1.0 / _displayScale` clamped to `[0.1, 10.0]`; leaves `Translate` at `(0, 0)`.
- `CancelInteraction` clears both `_dragStartScreen` and `_panStartScreen`, collapses arrow visibility, restores cursor to `Arrow`, and releases mouse capture.
- `ApplicationCommands.Open` has a built-in Ctrl+O gesture — MainWindow needs a `CommandBinding` but no `KeyBinding`.
- `ApplicationCommands.SaveAs` has NO built-in gesture — MainWindow needs both an explicit `KeyBinding` for Ctrl+S AND `InputGestureText="Ctrl+S"` on the Save As menu item so it renders inline.
- `OnExitClick` stays a `Click` handler (no shortcut mapped; Alt+F4 handles window close).
- Phase 5b branches from tag `phase5a` onto branch `phase5b-keyboard-shortcuts`.

---

### Task 1: `AppCommands` static class

**Files:**
- Create: `Deblur/AppCommands.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `public static class AppCommands` with eight `public static readonly RoutedUICommand` fields — `Reset`, `FitToWindow`, `PixelPerfect`, `ZoomIn`, `ZoomOut`, `RenderFull`, `ShowShortcuts`, `CancelInteraction` — each pre-bound to its `KeyGesture`.

- [ ] **Step 1: Create `AppCommands.cs`**

Create `Deblur/AppCommands.cs`:
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

- [ ] **Step 2: Build and confirm no regressions**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors. The class is declared but unreferenced — that's fine.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 53`.

- [ ] **Step 3: Commit**

```bash
git add Deblur/AppCommands.cs
git commit -m "Add AppCommands static class with RoutedUICommand fields"
```

---

### Task 2: `PreviewCanvas` public zoom + cancel API

**Files:**
- Modify: `Deblur/Controls/PreviewCanvas.xaml.cs`

**Interfaces:**
- Consumes: existing private fields `_zoom`, `_displayScale`, `_dragStartScreen`, `_panStartScreen`; existing named XAML elements `Scale`, `Translate`, `ArrowShaft`, `ArrowHead`; existing method `UpdateDisplayScale`.
- Produces:
```csharp
public void FitToWindow();
public void PixelPerfect();
public void Zoom(double factor);
public void CancelInteraction();
```

- [ ] **Step 1: Add the four public methods**

Locate `Deblur/Controls/PreviewCanvas.xaml.cs`. Add these four methods inside the class, placed after the existing `OnMouseWheel` handler and before the existing `UpdateDisplayScale` (or anywhere else in the class — placement inside the class is what matters):

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

- [ ] **Step 2: Build and confirm no regressions**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors. Any new warnings on `PreviewCanvas.xaml.cs` are findings.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 53`.

- [ ] **Step 3: Commit**

```bash
git add Deblur/Controls/PreviewCanvas.xaml.cs
git commit -m "Add public FitToWindow, PixelPerfect, Zoom, CancelInteraction to PreviewCanvas"
```

---

### Task 3: `ShortcutsWindow` modal

**Files:**
- Create: `Deblur/ShortcutsWindow.xaml`
- Create: `Deblur/ShortcutsWindow.xaml.cs`

**Interfaces:**
- Consumes: none.
- Produces: `public partial class Deblur.ShortcutsWindow : Window` — a modal reference window listing every shortcut with a Close button. Callers open it with `new ShortcutsWindow { Owner = this }.ShowDialog()`.

- [ ] **Step 1: Create `ShortcutsWindow.xaml`**

Create `Deblur/ShortcutsWindow.xaml`:
```xml
<Window x:Class="Deblur.ShortcutsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Keyboard Shortcuts"
        Width="420" Height="360"
        WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize"
        ShowInTaskbar="False"
        WindowStyle="ToolWindow">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <Grid Grid.Row="0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <TextBlock Grid.Row="0" Grid.Column="0" Text="Ctrl+O" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="0" Grid.Column="1" Text="Open image" Margin="0,4,0,4"/>

            <TextBlock Grid.Row="1" Grid.Column="0" Text="Ctrl+S" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="1" Grid.Column="1" Text="Save As" Margin="0,4,0,4"/>

            <TextBlock Grid.Row="2" Grid.Column="0" Text="F5" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="2" Grid.Column="1" Text="Render full resolution" Margin="0,4,0,4"/>

            <TextBlock Grid.Row="3" Grid.Column="0" Text="Ctrl+R" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="3" Grid.Column="1" Text="Reset current blur type" Margin="0,4,0,4"/>

            <TextBlock Grid.Row="4" Grid.Column="0" Text="Ctrl+0" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="4" Grid.Column="1" Text="Fit to window" Margin="0,4,0,4"/>

            <TextBlock Grid.Row="5" Grid.Column="0" Text="Ctrl+1" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="5" Grid.Column="1" Text="1:1 pixel" Margin="0,4,0,4"/>

            <TextBlock Grid.Row="6" Grid.Column="0" Text="Ctrl++" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="6" Grid.Column="1" Text="Zoom in" Margin="0,4,0,4"/>

            <TextBlock Grid.Row="7" Grid.Column="0" Text="Ctrl+-" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="7" Grid.Column="1" Text="Zoom out" Margin="0,4,0,4"/>

            <TextBlock Grid.Row="8" Grid.Column="0" Text="Esc" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="8" Grid.Column="1" Text="Cancel drag or pan" Margin="0,4,0,4"/>

            <TextBlock Grid.Row="9" Grid.Column="0" Text="F1" FontFamily="Consolas" Margin="0,4,16,4"/>
            <TextBlock Grid.Row="9" Grid.Column="1" Text="Show this window" Margin="0,4,0,4"/>
        </Grid>

        <Button Grid.Row="1" Content="Close" IsCancel="True" IsDefault="True"
                HorizontalAlignment="Right" Padding="16,4" Margin="0,12,0,0"/>
    </Grid>
</Window>
```

- [ ] **Step 2: Create `ShortcutsWindow.xaml.cs`**

Create `Deblur/ShortcutsWindow.xaml.cs`:
```csharp
using System.Windows;

namespace Deblur;

public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 3: Build and confirm no regressions**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors. Any new warnings are findings.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 53`.

- [ ] **Step 4: Commit**

```bash
git add Deblur/ShortcutsWindow.xaml Deblur/ShortcutsWindow.xaml.cs
git commit -m "Add ShortcutsWindow modal listing keyboard accelerators"
```

---

### Task 4: Rewire `MainWindow` for commands + Help menu

**Files:**
- Modify: `Deblur/MainWindow.xaml`
- Modify: `Deblur/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `AppCommands` (Task 1), `PreviewCanvas.FitToWindow/PixelPerfect/Zoom/CancelInteraction` (Task 2), `ShortcutsWindow` (Task 3).
- Produces: MainWindow that wires every shortcut through the `RoutedUICommand` pattern. Menu items + sidebar buttons switch from `Click=` to `Command=` bindings. New Help menu with Keyboard Shortcuts item. Code-behind renames the four Click handlers to Executed handlers and adds six new Executed handlers for the new commands.

- [ ] **Step 1: Replace the `<Window>` opening line and Resources with the extended header**

In `Deblur/MainWindow.xaml`, replace lines 1–17 with:
```xml
<Window x:Class="Deblur.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:controls="clr-namespace:Deblur.Controls"
        xmlns:vm="clr-namespace:Deblur.ViewModels"
        xmlns:engine="clr-namespace:Deblur.Engine;assembly=Deblur.Engine"
        xmlns:sys="clr-namespace:System;assembly=mscorlib"
        xmlns:local="clr-namespace:Deblur"
        Title="Deblur" Height="720" Width="1200"
        AllowDrop="True">
    <Window.DataContext><vm:MainViewModel/></Window.DataContext>
    <Window.Resources>
        <ObjectDataProvider x:Key="BlurTypeValues" MethodName="GetValues" ObjectType="{x:Type sys:Enum}">
            <ObjectDataProvider.MethodParameters>
                <x:Type TypeName="engine:BlurType"/>
            </ObjectDataProvider.MethodParameters>
        </ObjectDataProvider>
    </Window.Resources>

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
        <CommandBinding Command="ApplicationCommands.Open"                       Executed="OnOpenExecuted"/>
        <CommandBinding Command="ApplicationCommands.SaveAs"                     Executed="OnSaveAsExecuted"/>
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

- [ ] **Step 2: Replace the File menu block and add Help menu**

Replace the existing `<Menu DockPanel.Dock="Top">` block (currently lines 21–28) with:
```xml
            <Menu DockPanel.Dock="Top">
                <MenuItem Header="_File">
                    <MenuItem Header="_Open..." Command="ApplicationCommands.Open"/>
                    <MenuItem Header="_Save As..." Command="ApplicationCommands.SaveAs" InputGestureText="Ctrl+S"/>
                    <Separator/>
                    <MenuItem Header="E_xit" Click="OnExitClick"/>
                </MenuItem>
                <MenuItem Header="_Help">
                    <MenuItem Header="_Keyboard Shortcuts..." Command="{x:Static local:AppCommands.ShowShortcuts}"/>
                </MenuItem>
            </Menu>
```

- [ ] **Step 3: Switch the sidebar Reset and Render buttons to `Command=` bindings**

In `Deblur/MainWindow.xaml`, locate the Reset and Render-full buttons inside the `<StackPanel Margin="0,12,0,0" Visibility="{Binding HasImage...}">` block (currently lines 84–85). Replace those two lines with:
```xml
                        <Button Content="Reset" Margin="0,12,0,0" Command="{x:Static local:AppCommands.Reset}"/>
                        <Button Content="Render full resolution" Margin="0,8,0,0" Command="{x:Static local:AppCommands.RenderFull}"/>
```

- [ ] **Step 4: Rewrite `MainWindow.xaml.cs` to use `Executed` handlers**

Replace `Deblur/MainWindow.xaml.cs` entirely with:
```csharp
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Deblur.Controls;
using Deblur.ViewModels;

namespace Deblur;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        PreviewDragEnter += OnFileDragEnter;
        Drop += OnFileDrop;
        Closed += (_, __) => (DataContext as IDisposable)?.Dispose();
    }

    private void OnOpenExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (Vm.IsBusy) return;
        var dlg = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
        };
        if (dlg.ShowDialog(this) == true)
        {
            LoadFile(dlg.FileName);
        }
    }

    private async void OnRenderFullExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (Vm.CurrentFilePath is null)
        {
            MessageBox.Show(this, "Open an image first.", "Nothing to render", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Vm.IsBusy = true;
        Busy.Show("Rendering full resolution…");
        try
        {
            var progress = new Progress<double>(v => Busy.SetProgress(v));
            await Vm.EnsureFullResRenderedAsync(progress);
            Vm.StatusMessage = "Full-resolution render ready. Use File → Save As… to write it.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Render failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Busy.Hide(); Vm.IsBusy = false; }
    }

    private async void OnSaveAsExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (Vm.CurrentFilePath is null)
        {
            MessageBox.Show(this, "Open an image first.", "Nothing to save", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "PNG image|*.png|JPEG image|*.jpg;*.jpeg",
            FileName = System.IO.Path.GetFileNameWithoutExtension(Vm.CurrentFilePath) + "_deblurred",
            DefaultExt = ".png",
        };
        if (dlg.ShowDialog(this) != true) return;

        Vm.IsBusy = true;
        Busy.Show("Rendering and saving…");
        try
        {
            var progress = new Progress<double>(v => Busy.SetProgress(v));
            bool jpeg = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || dlg.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            byte[] bytes = jpeg
                ? await Vm.RenderFullAsJpegAsync(quality: 92, progress)
                : await Vm.RenderFullAsPngAsync(progress);
            File.WriteAllBytes(dlg.FileName, bytes);
            Vm.StatusMessage = $"Saved: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Busy.Hide(); Vm.IsBusy = false; }
    }
    private void OnExitClick(object sender, RoutedEventArgs e) => Close();
    private void OnResetExecuted(object sender, ExecutedRoutedEventArgs e) => Vm.Reset();

    private void OnFitExecuted(object sender, ExecutedRoutedEventArgs e) => Preview.FitToWindow();
    private void OnPixelPerfectExecuted(object sender, ExecutedRoutedEventArgs e) => Preview.PixelPerfect();
    private void OnZoomInExecuted(object sender, ExecutedRoutedEventArgs e) => Preview.Zoom(1.2);
    private void OnZoomOutExecuted(object sender, ExecutedRoutedEventArgs e) => Preview.Zoom(1.0 / 1.2);
    private void OnShowShortcutsExecuted(object sender, ExecutedRoutedEventArgs e)
        => new ShortcutsWindow { Owner = this }.ShowDialog();
    private void OnCancelInteractionExecuted(object sender, ExecutedRoutedEventArgs e) => Preview.CancelInteraction();

    private void OnPreviewDragging(object? sender, ArrowDragEventArgs e)
        => Vm.UpdateKernel(e.Angle, e.Length);

    private void OnPreviewDragCommitted(object? sender, ArrowDragEventArgs e)
        => Vm.UpdateKernel(e.Angle, e.Length);

    private void OnFileDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (Vm.IsBusy) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 0) return;
        LoadFile(files[0]);
    }

    private void LoadFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);

            // Pre-check pixel count via lightweight decode.
            using (var stream = new MemoryStream(bytes))
            {
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                    System.Windows.Media.Imaging.BitmapCacheOption.None);
                long pixels = (long)frame.PixelWidth * frame.PixelHeight;
                if (pixels > 100_000_000)
                {
                    double mp = pixels / 1_000_000.0;
                    var choice = MessageBox.Show(this,
                        $"Image is very large ({mp:0.0} MP); may be slow. Continue?",
                        "Large image", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (choice != MessageBoxResult.Yes) return;
                }
            }

            Vm.LoadImageFromBytes(bytes);
            Vm.CurrentFilePath = path;
            Vm.StatusMessage = System.IO.Path.GetFileName(path);
        }
        catch (Engine.InvalidImageFormatException ex)
        {
            MessageBox.Show(this, $"Couldn't read \"{System.IO.Path.GetFileName(path)}\": {ex.Message}",
                "Open failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OutOfMemoryException)
        {
            MessageBox.Show(this, "Ran out of memory. Try a smaller image.",
                "Out of memory", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
```

Notes on the rewrite:
- `OnOpenClick` → `OnOpenExecuted` (signature `(object, ExecutedRoutedEventArgs)`); body unchanged.
- `OnSaveAsClick` → `OnSaveAsExecuted`; body unchanged.
- `OnResetClick` → `OnResetExecuted`; body unchanged (`Vm.Reset()`).
- `OnRenderFullClick` → `OnRenderFullExecuted`; body unchanged.
- `OnExitClick` stays exactly as before (menu Click handler; no shortcut).
- Six new `Executed` handlers for the new commands.
- All drag-drop, LoadFile, OnPreviewDragging/Committed handlers unchanged.
- `using System.Windows.Input;` added at the top to expose `ExecutedRoutedEventArgs`.

- [ ] **Step 5: Build and confirm no regressions**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors. Any new warnings on `MainWindow.xaml` or `MainWindow.xaml.cs` are findings.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 53`.

- [ ] **Step 6: Commit**

```bash
git add Deblur/MainWindow.xaml Deblur/MainWindow.xaml.cs
git commit -m "Rewire MainWindow through RoutedUICommand pattern; add Help menu"
```

---

### Task 5: Manual smoke test pass + tag `phase5b`

**Files:** none.

**Interfaces:** none.

- [ ] **Step 1: Run the app**

```bash
dotnet run --project Deblur/Deblur.csproj
```

Walk through the checklist:

- [ ] Open the File menu — Open shows "Ctrl+O" on the right; Save As shows "Ctrl+S"; Exit shows no shortcut.
- [ ] Open the Help menu — Keyboard Shortcuts… shows "F1".
- [ ] Ctrl+O opens the file dialog (identical to menu-click behavior).
- [ ] Ctrl+S opens Save As (or "Open an image first" MessageBox if no image loaded).
- [ ] F5 triggers Render full resolution (or the "Nothing to render" MessageBox).
- [ ] Ctrl+R resets the currently-selected blur type's params + Smoothness (same as clicking the Reset button).
- [ ] With image loaded, zoom in via wheel or Ctrl++; press Ctrl+0 — view snaps back to fit-to-window at 1×.
- [ ] Press Ctrl+1 — view snaps to 1:1 pixel (each image pixel is one screen pixel; if the image is larger than the pane, edges get clipped).
- [ ] Press Ctrl++ multiple times — zoom in toward pane center (1.2× per press), stops at 10×.
- [ ] Press Ctrl+- multiple times — zoom out toward pane center, stops at 0.1×.
- [ ] Press F1 — ShortcutsWindow opens as a centered modal listing every shortcut. Esc or Close closes it. Focus returns to MainWindow.
- [ ] Under Motion (default), start a left-drag arrow; press Esc mid-drag — arrow disappears, sliders keep their last committed value.
- [ ] Start a middle-drag pan; press Esc mid-pan — pan cancels, cursor returns to Arrow.
- [ ] Help → Keyboard Shortcuts… menu item opens the same window as F1.
- [ ] Reset button on the sidebar still works (via Command binding).
- [ ] Render-full button on the sidebar still works (via Command binding).
- [ ] Existing arrow drag, drag-drop, and progress bar behavior unchanged from phase 5a.
- [ ] Under OutOfFocus / Gaussian, all shortcuts still function (no Motion-only assumption).

- [ ] **Step 2: Commit any smoke-test-triggered fixes**

If the smoke test surfaces bugs, fix them and commit each fix separately with a message describing the failure and the fix. If nothing was wrong, no commit is needed for this step.

- [ ] **Step 3: Tag phase 5b complete**

```bash
git tag phase5b
```

---

## Summary

Five tasks, each an independently reviewable commit. Task 1 introduces the `AppCommands` static class with pre-bound `KeyGesture`s. Task 2 exposes four public methods on `PreviewCanvas` (`FitToWindow`, `PixelPerfect`, `Zoom`, `CancelInteraction`) so MainWindow can drive zoom without touching internals. Task 3 adds a small modal `ShortcutsWindow` listing every accelerator. Task 4 rewires MainWindow's XAML with `Window.InputBindings` + `Window.CommandBindings`, switches menu items and sidebar buttons to `Command=` bindings, adds a Help menu, and renames the four Click handlers to Executed handlers plus adds six new ones. Task 5 smoke-tests every shortcut end-to-end and tags `phase5b`.
