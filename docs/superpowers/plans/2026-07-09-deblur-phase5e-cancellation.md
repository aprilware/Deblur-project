# Deblur Phase 5e Implementation Plan (Full-res Render Cancellation)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thread a `CancellationToken` through the full-res render/save path so the busy overlay can offer a Cancel button.

**Architecture:** `DeblurJobRunner.RenderFullAsync` gains an optional `CancellationToken` parameter and checks it at each progress boundary. `MainViewModel.EnsureFullResRenderedAsync` / `RenderFullAsPngAsync` / `RenderFullAsJpegAsync` propagate the token. `BusyOverlay` gains a Cancel `Button` (hidden by default), a `CancelRequested` routed event, and a `SetCancellable(bool)` method. `MainWindow.xaml.cs`'s Save + Render Executed handlers create a `CancellationTokenSource`, wire `Busy.CancelRequested` to `cts.Cancel()`, pass the token, catch `OperationCanceledException`, and dispose the CTS in `finally`.

**Tech Stack:** .NET 8, WPF, xUnit. No new NuGet packages.

## Global Constraints

- .NET 8 (`net8.0-windows` WPF, `net8.0` Engine + Tests). Nullable + ImplicitUsings enabled.
- No new NuGet packages.
- Files touched: `Deblur.Engine/DeblurJobRunner.cs`, `Deblur/ViewModels/MainViewModel.cs`, `Deblur/Controls/BusyOverlay.xaml`, `Deblur/Controls/BusyOverlay.xaml.cs`, `Deblur/MainWindow.xaml.cs`, `Deblur.Tests/DeblurJobRunnerTests.cs`. Six files total.
- `CancellationToken` parameter is `= default` on every method so existing call sites still compile.
- Cancel button hidden by default (`Visibility="Collapsed"`) — `MainWindow` calls `Busy.SetCancellable(true)` before starting the operation and `Busy.SetCancellable(false)` (or `Hide()` which also resets) in `finally`.
- The runner checks `token.ThrowIfCancellationRequested()` at the start of the task and before each `progress?.Report` call (three checks total: before 0.1, before 0.3, before 1.0).
- All 53 phase-5b tests remain green; new test brings total to 54.
- Phase 5e branches from tag `phase5b` onto branch `phase5e-cancellation`.

---

### Task 1: `DeblurJobRunner.RenderFullAsync` gains `CancellationToken` + test

**Files:**
- Modify: `Deblur.Engine/DeblurJobRunner.cs`
- Modify: `Deblur.Tests/DeblurJobRunnerTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `public Task<ImageBuffer> RenderFullAsync(ImageBuffer fullRes, KernelParams p, float proxyScale, IProgress<double>? progress = null, CancellationToken cancellationToken = default)` — one new optional trailing parameter. All existing call sites still compile.

- [ ] **Step 1: Add the failing test**

Append this `[Fact]` inside the existing `DeblurJobRunnerTests` class (before the closing brace of the class):

```csharp
    [Fact]
    public async Task RenderFullAsync_PrecancelledToken_ThrowsOperationCanceled()
    {
        var kernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 0 };
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);

        var full = SyntheticImages.Checkerboard(200, 200, 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await runner.RenderFullAsync(
                full,
                new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f, 0f, AlgorithmType.Wiener),
                proxyScale: 0.25f,
                progress: null,
                cancellationToken: cts.Token));
    }
```

- [ ] **Step 2: Run the test — verify it fails on compile error (no `cancellationToken` parameter yet)**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~RenderFullAsync_PrecancelledToken"
```
Expected: compile error on `cancellationToken:`.

- [ ] **Step 3: Extend `RenderFullAsync` to accept and check the token**

In `Deblur.Engine/DeblurJobRunner.cs`, replace the existing `RenderFullAsync` method (currently around lines 53–77) with:

```csharp
    public Task<ImageBuffer> RenderFullAsync(
        ImageBuffer fullRes, KernelParams p, float proxyScale,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.1);
            float scaleInv = 1f / Math.Max(proxyScale, 1e-6f);
            var scaledParams = p with
            {
                Length = p.Length * scaleInv,
                Radius = p.Radius * scaleInv,
                Sigma  = p.Sigma  * scaleInv,
            };
            if (IsNoOp(scaledParams))
            {
                progress?.Report(1.0);
                return fullRes.Clone();
            }
            cancellationToken.ThrowIfCancellationRequested();
            var psf = _kernels[scaledParams.Type].Build(scaledParams);
            progress?.Report(0.3);
            cancellationToken.ThrowIfCancellationRequested();
            var result = _deconvolvers[scaledParams.Algorithm].Apply(fullRes, psf, new DeconvolutionParams(K: p.Smoothness));
            progress?.Report(1.0);
            return result;
        }, cancellationToken);
    }
```

- [ ] **Step 4: Run the full test suite — verify 54/54**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 54` (53 existing + 1 new).

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/DeblurJobRunner.cs Deblur.Tests/DeblurJobRunnerTests.cs
git commit -m "Thread CancellationToken through DeblurJobRunner.RenderFullAsync"
```

---

### Task 2: `BusyOverlay` gains Cancel button + `CancelRequested` event

**Files:**
- Modify: `Deblur/Controls/BusyOverlay.xaml`
- Modify: `Deblur/Controls/BusyOverlay.xaml.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `BusyOverlay` gains:
  - `public event RoutedEventHandler CancelRequested` — fires when the Cancel button is clicked.
  - `public void SetCancellable(bool value)` — shows or hides the Cancel button.
  - `Hide()` also resets the button to hidden and the cancellable state to false.

- [ ] **Step 1: Add the Cancel button to `BusyOverlay.xaml`**

Replace the contents of `Deblur/Controls/BusyOverlay.xaml`:
```xml
<UserControl x:Class="Deblur.Controls.BusyOverlay"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="#AA000000" Visibility="Collapsed" IsHitTestVisible="True">
    <Grid>
        <Border Background="#222" CornerRadius="8" Padding="24"
                HorizontalAlignment="Center" VerticalAlignment="Center">
            <StackPanel>
                <TextBlock x:Name="MessageText" Text="Working…" Foreground="White" FontSize="14" Margin="0,0,0,12"/>
                <ProgressBar x:Name="ProgressBar" Width="240" Height="10" Minimum="0" Maximum="1"/>
                <Button x:Name="CancelButton" Content="Cancel" Margin="0,12,0,0"
                        HorizontalAlignment="Right" Padding="16,4"
                        Visibility="Collapsed" Click="OnCancelClick"/>
            </StackPanel>
        </Border>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Add the event + methods in `BusyOverlay.xaml.cs`**

Replace `Deblur/Controls/BusyOverlay.xaml.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;

namespace Deblur.Controls;

public partial class BusyOverlay : UserControl
{
    public event RoutedEventHandler? CancelRequested;

    public BusyOverlay() { InitializeComponent(); }

    public void Show(string message)
    {
        MessageText.Text = message;
        ProgressBar.Value = 0;
        Visibility = System.Windows.Visibility.Visible;
    }

    public void SetProgress(double value) => ProgressBar.Value = value;

    public void SetCancellable(bool value)
        => CancelButton.Visibility = value ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public void Hide()
    {
        Visibility = System.Windows.Visibility.Collapsed;
        CancelButton.Visibility = System.Windows.Visibility.Collapsed;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
        => CancelRequested?.Invoke(this, e);
}
```

- [ ] **Step 3: Build and confirm no regressions**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 54`.

- [ ] **Step 4: Commit**

```bash
git add Deblur/Controls/BusyOverlay.xaml Deblur/Controls/BusyOverlay.xaml.cs
git commit -m "Add Cancel button and CancelRequested event to BusyOverlay"
```

---

### Task 3: `MainViewModel` + `MainWindow` wiring

**Files:**
- Modify: `Deblur/ViewModels/MainViewModel.cs`
- Modify: `Deblur/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `DeblurJobRunner.RenderFullAsync(..., CancellationToken cancellationToken = default)` (Task 1); `BusyOverlay.SetCancellable(bool)` and `event CancelRequested` (Task 2).
- Produces: `MainViewModel`'s three async methods gain a trailing `CancellationToken cancellationToken = default` parameter and forward it through the runner call. `MainWindow.OnRenderFullExecuted` and `OnSaveAsExecuted` create/dispose a `CancellationTokenSource`, subscribe `Busy.CancelRequested` to `cts.Cancel()`, and catch `OperationCanceledException`.

- [ ] **Step 1: Thread `CancellationToken` through the three `MainViewModel` methods**

In `Deblur/ViewModels/MainViewModel.cs`, replace the three async methods (currently around lines 156–179) with:

```csharp
    public async Task EnsureFullResRenderedAsync(IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        if (_originalFullRes is null) throw new InvalidOperationException("No image loaded.");
        var current = BuildCurrentParams();
        if (_fullResBuffer is not null && _fullResParams.Equals(current))
        {
            progress.Report(1.0);
            return;
        }
        _fullResBuffer = await _runner.RenderFullAsync(_originalFullRes, current, _proxyScale, progress, cancellationToken);
        _fullResParams = current;
    }

    public async Task<byte[]> RenderFullAsPngAsync(IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        await EnsureFullResRenderedAsync(progress, cancellationToken);
        return ImageCodec.EncodePng(_fullResBuffer!);
    }

    public async Task<byte[]> RenderFullAsJpegAsync(int quality, IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        await EnsureFullResRenderedAsync(progress, cancellationToken);
        return ImageCodec.EncodeJpeg(_fullResBuffer!, quality);
    }
```

- [ ] **Step 2: Wire cancellation into `MainWindow.OnRenderFullExecuted`**

In `Deblur/MainWindow.xaml.cs`, replace the existing `OnRenderFullExecuted` method with:

```csharp
    private async void OnRenderFullExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (Vm.CurrentFilePath is null)
        {
            MessageBox.Show(this, "Open an image first.", "Nothing to render", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Vm.IsBusy = true;
        Busy.Show("Rendering full resolution…");
        Busy.SetCancellable(true);
        using var cts = new CancellationTokenSource();
        RoutedEventHandler cancelHandler = (_, __) => cts.Cancel();
        Busy.CancelRequested += cancelHandler;
        try
        {
            var progress = new Progress<double>(v => Busy.SetProgress(v));
            await Vm.EnsureFullResRenderedAsync(progress, cts.Token);
            Vm.StatusMessage = "Full-resolution render ready. Use File → Save As… to write it.";
        }
        catch (OperationCanceledException)
        {
            Vm.StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Render failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Busy.CancelRequested -= cancelHandler;
            Busy.Hide();
            Vm.IsBusy = false;
        }
    }
```

- [ ] **Step 3: Wire cancellation into `MainWindow.OnSaveAsExecuted`**

Replace the existing `OnSaveAsExecuted` method with:

```csharp
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
        Busy.SetCancellable(true);
        using var cts = new CancellationTokenSource();
        RoutedEventHandler cancelHandler = (_, __) => cts.Cancel();
        Busy.CancelRequested += cancelHandler;
        try
        {
            var progress = new Progress<double>(v => Busy.SetProgress(v));
            bool jpeg = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || dlg.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            byte[] bytes = jpeg
                ? await Vm.RenderFullAsJpegAsync(quality: 92, progress, cts.Token)
                : await Vm.RenderFullAsPngAsync(progress, cts.Token);
            File.WriteAllBytes(dlg.FileName, bytes);
            Vm.StatusMessage = $"Saved: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (OperationCanceledException)
        {
            Vm.StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Busy.CancelRequested -= cancelHandler;
            Busy.Hide();
            Vm.IsBusy = false;
        }
    }
```

Notes:
- `using System.Threading;` is already brought in transitively via existing `using`s; verify no new using is needed. If the build complains about `CancellationTokenSource`, add `using System.Threading;` at the top.
- The `OperationCanceledException` catch sits BEFORE the generic `catch (Exception ex)` so cancellation is handled distinctly.
- `Busy.Hide()` also resets the Cancel button visibility per Task 2, so no explicit `Busy.SetCancellable(false)` is needed in `finally`.

- [ ] **Step 4: Build and confirm no regressions**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 54`.

- [ ] **Step 5: Commit**

```bash
git add Deblur/ViewModels/MainViewModel.cs Deblur/MainWindow.xaml.cs
git commit -m "Wire cancellation through MainViewModel and MainWindow"
```

---

### Task 4: Manual smoke test + tag `phase5e`

**Files:** none.

**Interfaces:** none.

- [ ] **Step 1: Run the app**

```bash
dotnet run --project Deblur/Deblur.csproj
```

Walk through the checklist:

- [ ] Load a moderately large image (>3 MP so the full-res render takes at least a couple of seconds).
- [ ] Click "Render full resolution" — busy overlay appears with a Cancel button.
- [ ] Wait a moment, click Cancel — overlay closes, StatusMessage shows "Cancelled".
- [ ] Click Render full resolution again, don't cancel — normal completion; StatusMessage shows "Full-resolution render ready…".
- [ ] File → Save As → PNG. Cancel mid-render — file is NOT written; StatusMessage shows "Cancelled".
- [ ] Save As → PNG normally — file saved; StatusMessage shows "Saved: …".
- [ ] Progress bar still increments during un-cancelled operations.
- [ ] Existing zoom + pan + shortcuts behavior unchanged.

- [ ] **Step 2: Commit any smoke-test-triggered fixes**

If the smoke test surfaces bugs, fix them and commit each fix separately. If nothing was wrong, no commit is needed.

- [ ] **Step 3: Tag phase 5e complete**

```bash
git tag phase5e
```

---

## Summary

Four tasks. Task 1 threads `CancellationToken` through the engine's `RenderFullAsync` with one new test. Task 2 adds the Cancel button + event to `BusyOverlay`. Task 3 propagates the token through `MainViewModel` and wires cancellation into MainWindow's Save + Render Executed handlers (including subscription/unsubscription in try/finally). Task 4 smoke-tests end-to-end and tags `phase5e`.
