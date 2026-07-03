namespace Deblur.Engine;

public sealed class ImageBuffer
{
    public int Width { get; }
    public int Height { get; }
    public float[] R { get; }
    public float[] G { get; }
    public float[] B { get; }

    public int PixelCount => Width * Height;

    public ImageBuffer(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        R = new float[width * height];
        G = new float[width * height];
        B = new float[width * height];
    }

    public ImageBuffer(int width, int height, float[] r, float[] g, float[] b)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        int expected = width * height;
        if (r.Length != expected || g.Length != expected || b.Length != expected)
            throw new ArgumentException("Channel lengths must equal width * height.");
        Width = width;
        Height = height;
        R = r;
        G = g;
        B = b;
    }

    public ImageBuffer Clone()
    {
        return new ImageBuffer(
            Width, Height,
            (float[])R.Clone(),
            (float[])G.Clone(),
            (float[])B.Clone());
    }
}
