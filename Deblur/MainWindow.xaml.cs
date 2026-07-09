using System.IO;
using System.Threading;
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
    private void OnExitClick(object sender, RoutedEventArgs e) => Close();
    private void OnResetExecuted(object sender, ExecutedRoutedEventArgs e) => Vm.Reset();

    private void OnFitExecuted(object sender, ExecutedRoutedEventArgs e) => Preview.FitToWindow();
    private void OnPixelPerfectExecuted(object sender, ExecutedRoutedEventArgs e) => Preview.PixelPerfect();
    private void OnZoomInExecuted(object sender, ExecutedRoutedEventArgs e) => Preview.Zoom(1.2);
    private void OnZoomOutExecuted(object sender, ExecutedRoutedEventArgs e) => Preview.Zoom(1.0 / 1.2);
    private void OnShowShortcutsExecuted(object sender, ExecutedRoutedEventArgs e)
        => new ShortcutsWindow { Owner = this }.ShowDialog();
    private void OnCancelInteractionExecuted(object sender, ExecutedRoutedEventArgs e) => Preview.CancelInteraction();

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

    private void OnPreviewDragging(object? sender, ArrowDragEventArgs e)
        => Vm.UpdateKernel(e.Angle, e.Length);

    private void OnPreviewDragCommitted(object? sender, ArrowDragEventArgs e)
        => Vm.CommitArrowDrag(e.Angle, e.Length);

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
