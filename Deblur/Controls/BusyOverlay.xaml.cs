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
