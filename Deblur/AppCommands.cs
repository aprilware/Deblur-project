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

    public static readonly RoutedUICommand Undo =
        new("Undo", "Undo", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.Z, ModifierKeys.Control) });

    public static readonly RoutedUICommand Redo =
        new("Redo", "Redo", typeof(AppCommands),
            new InputGestureCollection { new KeyGesture(Key.Y, ModifierKeys.Control) });
}
