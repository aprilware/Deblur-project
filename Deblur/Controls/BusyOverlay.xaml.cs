using System.Windows.Controls;

namespace Deblur.Controls;

public partial class BusyOverlay : UserControl
{
    public BusyOverlay() { InitializeComponent(); }

    public void Show(string message)
    {
        MessageText.Text = message;
        ProgressBar.Value = 0;
        Visibility = System.Windows.Visibility.Visible;
    }

    public void SetProgress(double value) => ProgressBar.Value = value;

    public void Hide() => Visibility = System.Windows.Visibility.Collapsed;
}
