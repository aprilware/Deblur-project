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

public sealed class RoiDrawnEventArgs : EventArgs
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
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

    public static readonly DependencyProperty RoiModeEnabledProperty = DependencyProperty.Register(
        nameof(RoiModeEnabled), typeof(bool), typeof(PreviewCanvas),
        new PropertyMetadata(false, OnRoiModeEnabledChanged));

    public bool RoiModeEnabled
    {
        get => (bool)GetValue(RoiModeEnabledProperty);
        set => SetValue(RoiModeEnabledProperty, value);
    }

    public static readonly DependencyProperty SelectedRoiRectProperty = DependencyProperty.Register(
        nameof(SelectedRoiRect), typeof(Rect?), typeof(PreviewCanvas),
        new PropertyMetadata(null, OnSelectedRoiRectChanged));

    public Rect? SelectedRoiRect
    {
        get => (Rect?)GetValue(SelectedRoiRectProperty);
        set => SetValue(SelectedRoiRectProperty, value);
    }

    public event EventHandler<RoiDrawnEventArgs>? RoiDrawn;

    private Point? _dragStartScreen;
    private double _displayScale = 1.0;
    private double _zoom = 1.0;
    private Point? _panStartScreen;
    private Point _panStartTranslate;

    // Anchor of an in-progress ROI rubber-band drag, in IMAGE coordinates.
    private Point? _roiAnchorImage;

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
        SizeChanged += (_, _) => SyncRoiOverlayVisual();
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PreviewCanvas)d;
        self.PreviewImage.Source = (WriteableBitmap?)e.NewValue;
        self._dragStartScreen = null;
        self.ArrowShaft.Visibility = self.ArrowHead.Visibility = Visibility.Collapsed;
        self.CancelRoiDrag();

        // Reset the view transform so a fresh image loads fit-to-window at 1x.
        self._zoom = 1.0;
        self.Scale.ScaleX = self.Scale.ScaleY = 1.0;
        self.Translate.X = self.Translate.Y = 0.0;
        self.SyncRoiOverlayVisual();
    }

    private static void OnRoiModeEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue) return;
        // Leaving ROI mode cancels any in-progress rubber-band drag.
        ((PreviewCanvas)d).CancelRoiDrag();
    }

    private static void OnSelectedRoiRectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((PreviewCanvas)d).SyncRoiOverlayVisual();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Source is null) return;

        if (RoiModeEnabled)
        {
            _roiAnchorImage = ScreenToImage(e.GetPosition(this));
            CaptureMouse();
            UpdateRoiDragRect(_roiAnchorImage.Value, _roiAnchorImage.Value);
            RoiDragRect.Visibility = Visibility.Visible;
            return;
        }

        if (!IsArrowEnabled) return;
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
            SyncRoiOverlayVisual();
            return;
        }

        if (_roiAnchorImage is not null)
        {
            var curImage = ScreenToImage(e.GetPosition(this));
            UpdateRoiDragRect(_roiAnchorImage.Value, curImage);
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
        if (_roiAnchorImage is not null)
        {
            var endImage = ScreenToImage(e.GetPosition(this));
            FinalizeRoiDrag(_roiAnchorImage.Value, endImage);
            _roiAnchorImage = null;
            RoiDragRect.Visibility = Visibility.Collapsed;
            ReleaseMouseCapture();
            return;
        }

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
        // Cancel any in-progress ROI drag: hide the rubber band, no commit.
        if (_roiAnchorImage is not null)
        {
            CancelRoiDrag();
        }

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
        SyncRoiOverlayVisual();
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
        SyncRoiOverlayVisual();
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
        SyncRoiOverlayVisual();
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
        SyncRoiOverlayVisual();
    }

    public void CancelInteraction()
    {
        _dragStartScreen = null;
        _panStartScreen = null;
        ArrowShaft.Visibility = ArrowHead.Visibility = Visibility.Collapsed;
        Cursor = System.Windows.Input.Cursors.Arrow;
        ReleaseMouseCapture();
        CancelRoiDrag();
    }

    private void CancelRoiDrag()
    {
        _roiAnchorImage = null;
        RoiDragRect.Visibility = Visibility.Collapsed;
        ReleaseMouseCapture();
    }

    private void FinalizeRoiDrag(Point anchorImage, Point releaseImage)
    {
        double x = Math.Min(anchorImage.X, releaseImage.X);
        double y = Math.Min(anchorImage.Y, releaseImage.Y);
        double width = Math.Abs(releaseImage.X - anchorImage.X);
        double height = Math.Abs(releaseImage.Y - anchorImage.Y);

        // Accidental-click guard: too small to be a deliberate selection.
        if (width < 4 || height < 4) return;

        RoiDrawn?.Invoke(this, new RoiDrawnEventArgs
        {
            X = (int)Math.Round(x),
            Y = (int)Math.Round(y),
            Width = (int)Math.Round(width),
            Height = (int)Math.Round(height),
        });
    }

    /// <summary>Positions the active drag rectangle (screen space) from two IMAGE-space corners.</summary>
    private void UpdateRoiDragRect(Point anchorImage, Point curImage)
    {
        var a = ImageToScreen(anchorImage);
        var c = ImageToScreen(curImage);
        Canvas.SetLeft(RoiDragRect, Math.Min(a.X, c.X));
        Canvas.SetTop(RoiDragRect, Math.Min(a.Y, c.Y));
        RoiDragRect.Width = Math.Abs(c.X - a.X);
        RoiDragRect.Height = Math.Abs(c.Y - a.Y);
    }

    /// <summary>Repositions the persistent ROI overlay (screen space) from <see cref="SelectedRoiRect"/>.</summary>
    private void SyncRoiOverlayVisual()
    {
        var rect = SelectedRoiRect;
        if (rect is null || Source is null)
        {
            RoiOverlayRect.Visibility = Visibility.Collapsed;
            return;
        }

        var topLeft = ImageToScreen(new Point(rect.Value.X, rect.Value.Y));
        var bottomRight = ImageToScreen(new Point(rect.Value.X + rect.Value.Width, rect.Value.Y + rect.Value.Height));
        Canvas.SetLeft(RoiOverlayRect, Math.Min(topLeft.X, bottomRight.X));
        Canvas.SetTop(RoiOverlayRect, Math.Min(topLeft.Y, bottomRight.Y));
        RoiOverlayRect.Width = Math.Abs(bottomRight.X - topLeft.X);
        RoiOverlayRect.Height = Math.Abs(bottomRight.Y - topLeft.Y);
        RoiOverlayRect.Visibility = Visibility.Visible;
    }

    private void UpdateDisplayScale()
    {
        if (Source is null) { _displayScale = 1.0; return; }
        double sx = ActualWidth / Source.PixelWidth;
        double sy = ActualHeight / Source.PixelHeight;
        _displayScale = Math.Min(sx, sy);
        if (_displayScale <= 0) _displayScale = 1.0;
    }

    /// <summary>
    /// Converts a mouse position in control-local ("screen") coordinates to IMAGE-pixel
    /// coordinates by inverting the zoom/pan render transform and the uniform display
    /// scale + letterbox centering that <see cref="PreviewImage"/> applies.
    /// </summary>
    private Point ScreenToImage(Point screen)
    {
        UpdateDisplayScale();
        double localX = (screen.X - Translate.X) / Scale.ScaleX;
        double localY = (screen.Y - Translate.Y) / Scale.ScaleY;
        if (Source is null || _displayScale <= 0) return new Point(localX, localY);

        double dispW = Source.PixelWidth * _displayScale;
        double dispH = Source.PixelHeight * _displayScale;
        double offsetX = (ActualWidth - dispW) / 2.0;
        double offsetY = (ActualHeight - dispH) / 2.0;
        return new Point((localX - offsetX) / _displayScale, (localY - offsetY) / _displayScale);
    }

    /// <summary>Inverse of <see cref="ScreenToImage"/>: IMAGE-pixel coordinates to control-local coordinates.</summary>
    private Point ImageToScreen(Point image)
    {
        UpdateDisplayScale();
        double localX = image.X * _displayScale;
        double localY = image.Y * _displayScale;
        if (Source is not null)
        {
            double dispW = Source.PixelWidth * _displayScale;
            double dispH = Source.PixelHeight * _displayScale;
            localX += (ActualWidth - dispW) / 2.0;
            localY += (ActualHeight - dispH) / 2.0;
        }
        return new Point(localX * Scale.ScaleX + Translate.X, localY * Scale.ScaleY + Translate.Y);
    }

    private (float angle, float length) ToImageSpace(Point start, Point cur)
    {
        var s = ScreenToImage(start);
        var c = ScreenToImage(cur);
        double dx = c.X - s.X;
        double dy = c.Y - s.Y;
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
