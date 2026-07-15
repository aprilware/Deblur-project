using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Deblur.Controls;

public partial class PsfDisplay : UserControl
{
    public static readonly DependencyProperty KernelProperty =
        DependencyProperty.Register(nameof(Kernel), typeof(float[,]), typeof(PsfDisplay),
            new PropertyMetadata(null, OnKernelChanged));

    public float[,]? Kernel
    {
        get => (float[,]?)GetValue(KernelProperty);
        set => SetValue(KernelProperty, value);
    }

    public PsfDisplay() { InitializeComponent(); }

    private static void OnKernelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((PsfDisplay)d).Rebuild();
    }

    private void Rebuild()
    {
        if (Kernel is null)
        {
            KernelImage.Source = null;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;

        int kh = Kernel.GetLength(0), kw = Kernel.GetLength(1);
        // Normalize to [0,1] for display (kernels sum to 1 so pixel values are small).
        float max = 0;
        for (int y = 0; y < kh; y++)
            for (int x = 0; x < kw; x++)
                if (Kernel[y, x] > max) max = Kernel[y, x];
        float inv = max > 0 ? 1f / max : 1f;

        int display = 128;
        int cell = Math.Max(1, display / Math.Max(kw, kh));
        int outW = cell * kw;
        int outH = cell * kh;

        var pixels = new byte[outW * outH * 4];
        for (int oy = 0; oy < outH; oy++)
        {
            int ky = oy / cell;
            for (int ox = 0; ox < outW; ox++)
            {
                int kx = ox / cell;
                byte g = (byte)Math.Clamp((int)MathF.Round(Kernel[ky, kx] * inv * 255f), 0, 255);
                int p = (oy * outW + ox) * 4;
                pixels[p] = g; pixels[p + 1] = g; pixels[p + 2] = g; pixels[p + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(outW, outH, 96, 96, PixelFormats.Bgra32, null, pixels, outW * 4);
        KernelImage.Source = bmp;
    }
}
