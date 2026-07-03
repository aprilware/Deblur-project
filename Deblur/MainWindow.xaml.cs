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

    private void OnSaveAsClick(object sender, RoutedEventArgs e) { /* implemented in Task 11 */ }
    private void OnRenderFullClick(object sender, RoutedEventArgs e) { /* implemented in Task 11 */ }
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
