using System.IO;
using System.Windows;
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
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
        };
        if (dlg.ShowDialog(this) == true)
        {
            LoadFile(dlg.FileName);
        }
    }

    private async void OnRenderFullClick(object sender, RoutedEventArgs e)
    {
        if (Vm.CurrentFilePath is null)
        {
            MessageBox.Show(this, "Open an image first.", "Nothing to render", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Busy.Show("Rendering full resolution…");
        try
        {
            var progress = new Progress<double>(v => Busy.SetProgress(v));
            // Populate _fullResBuffer without touching _originalFullRes or the current preview.
            // The proxy-deblurred preview already shows the same params visually; Save will use the cache.
            await Vm.EnsureFullResRenderedAsync(progress);
            Vm.StatusMessage = "Full-resolution render ready. Use File → Save As… to write it.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Render failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Busy.Hide(); }
    }

    private async void OnSaveAsClick(object sender, RoutedEventArgs e)
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
        finally { Busy.Hide(); }
    }
    private void OnExitClick(object sender, RoutedEventArgs e) => Close();
    private void OnResetClick(object sender, RoutedEventArgs e) => Vm.Reset();

    private void OnPreviewDragging(object? sender, ArrowDragEventArgs e)
        => Vm.UpdateKernel(e.Angle, e.Length);

    private void OnPreviewDragCommitted(object? sender, ArrowDragEventArgs e)
        => Vm.UpdateKernel(e.Angle, e.Length);

    private void LoadFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            Vm.LoadImageFromBytes(bytes);
            Vm.CurrentFilePath = path;
            Vm.StatusMessage = System.IO.Path.GetFileName(path);
        }
        catch (Engine.InvalidImageFormatException ex)
        {
            MessageBox.Show(this, $"Couldn't read \"{System.IO.Path.GetFileName(path)}\": {ex.Message}",
                "Open failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
