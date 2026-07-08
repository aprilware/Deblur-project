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

    public static readonly DependencyProperty IsArrowEnabledProperty = DependencyProperty.Register(
        nameof(IsArrowEnabled), typeof(bool), typeof(PreviewCanvas),
        new PropertyMetadata(true));

    public bool IsArrowEnabled
    {
        get => (bool)GetValue(IsArrowEnabledProperty);
        set => SetValue(IsArrowEnabledProperty, value);
    }

    public event EventHandler<ArrowDragEventArgs>? Dragging;
    public event EventHandler<ArrowDragEventArgs>? DragCommitted;

    private Point? _dragStartScreen;
    private double _displayScale = 1.0;
    private double _zoom = 1.0;
    private Point? _panStartScreen;
    private Point _panStartTranslate;

    public PreviewCanvas()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseLeave += OnMouseLeave;
        MouseWheel += OnMouseWheel;
        MouseDown += OnAnyMouseDown;
        MouseUp += OnAnyMouseUp;
    }

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

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Source is null || !IsArrowEnabled) return;
        _dragStartScreen = e.GetPosition(this);
        CaptureMouse();
        UpdateDisplayScale();
        UpdateArrow(_dragStartScreen.Value, _dragStartScreen.Value);
        ArrowShaft.Visibility = ArrowHead.Visibility = Visibility.Visible;
    }

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

        // If a pan is active, re-baseline it against the post-zoom state so the
        // next MouseMove doesn't snap Translate back to the pre-zoom position.
        if (_panStartScreen is not null)
        {
            _panStartScreen = cursor;
            _panStartTranslate = new Point(Translate.X, Translate.Y);
        }
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
        double effectiveScale = _displayScale * _zoom;
        double dx = (cur.X - start.X) / effectiveScale;
        double dy = (cur.Y - start.Y) / effectiveScale;
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
