using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Deblur.Controls;

public sealed class ArrowDragEventArgs : EventArgs
{
    public float Angle { get; init; }
    public float Length { get; init; }
}

public partial class PreviewCanvas : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(WriteableBitmap), typeof(PreviewCanvas),
        new PropertyMetadata(null, OnSourceChanged));

    public WriteableBitmap? Source
    {
        get => (WriteableBitmap?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public event EventHandler<ArrowDragEventArgs>? Dragging;
    public event EventHandler<ArrowDragEventArgs>? DragCommitted;

    private Point? _dragStartScreen;
    private double _displayScale = 1.0;

    public PreviewCanvas()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseLeave += OnMouseLeave;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PreviewCanvas)d;
        self.PreviewImage.Source = (WriteableBitmap?)e.NewValue;
        self._dragStartScreen = null;
        self.ArrowShaft.Visibility = self.ArrowHead.Visibility = Visibility.Collapsed;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Source is null) return;
        _dragStartScreen = e.GetPosition(this);
        CaptureMouse();
        UpdateDisplayScale();
        UpdateArrow(_dragStartScreen.Value, _dragStartScreen.Value);
        ArrowShaft.Visibility = ArrowHead.Visibility = Visibility.Visible;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartScreen is null || Source is null) return;
        var cur = e.GetPosition(this);
        UpdateArrow(_dragStartScreen.Value, cur);
        var (angle, length) = ToImageSpace(_dragStartScreen.Value, cur);
        Dragging?.Invoke(this, new ArrowDragEventArgs { Angle = angle, Length = length });
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStartScreen is null || Source is null) return;
        var end = e.GetPosition(this);
        var (angle, length) = ToImageSpace(_dragStartScreen.Value, end);
        DragCommitted?.Invoke(this, new ArrowDragEventArgs { Angle = angle, Length = length });
        _dragStartScreen = null;
        ArrowShaft.Visibility = ArrowHead.Visibility = Visibility.Collapsed;
        ReleaseMouseCapture();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // Cancel drag: clear arrow, no commit.
        if (_dragStartScreen is null) return;
        _dragStartScreen = null;
        ArrowShaft.Visibility = ArrowHead.Visibility = Visibility.Collapsed;
        ReleaseMouseCapture();
    }

    private void UpdateDisplayScale()
    {
        if (Source is null) { _displayScale = 1.0; return; }
        double sx = ActualWidth / Source.PixelWidth;
        double sy = ActualHeight / Source.PixelHeight;
        _displayScale = Math.Min(sx, sy);
        if (_displayScale <= 0) _displayScale = 1.0;
    }

    private (float angle, float length) ToImageSpace(Point start, Point cur)
    {
        double dx = (cur.X - start.X) / _displayScale;
        double dy = (cur.Y - start.Y) / _displayScale;
        double lenPx = Math.Sqrt(dx * dx + dy * dy);
        double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (angleDeg < 0) angleDeg += 360.0;
        double clampedLen = Math.Clamp(lenPx, 1.0, 100.0);
        return ((float)angleDeg, (float)clampedLen);
    }

    private void UpdateArrow(Point start, Point cur)
    {
        ArrowShaft.X1 = start.X; ArrowShaft.Y1 = start.Y;
        ArrowShaft.X2 = cur.X;   ArrowShaft.Y2 = cur.Y;

        // Simple 8-pixel head on the tip.
        double dx = cur.X - start.X;
        double dy = cur.Y - start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 4) { ArrowHead.Points.Clear(); return; }
        double ux = dx / len, uy = dy / len;
        double bx = cur.X - 8 * ux, by = cur.Y - 8 * uy;
        double px = -uy, py = ux;

        ArrowHead.Points = new PointCollection {
            new Point(cur.X, cur.Y),
            new Point(bx + 4 * px, by + 4 * py),
            new Point(bx - 4 * px, by - 4 * py),
        };
    }
}
