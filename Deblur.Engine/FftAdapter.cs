using System.Numerics;

namespace Deblur.Engine;

public static class FftAdapter
{
    public static int NextPow2(int n)
    {
        if (n <= 1) return 1;
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    public static Complex[,] Forward2D(float[,] real)
    {
        int h = real.GetLength(0);
        int w = real.GetLength(1);

        // Row-wise FFT.
        var rows = new Complex[h][];
        for (int y = 0; y < h; y++)
        {
            var row = new Complex[w];
            for (int x = 0; x < w; x++) row[x] = new Complex(real[y, x], 0);
            FftSharp.FFT.Forward(row);
            rows[y] = row;
        }

        // Column-wise FFT.
        var result = new Complex[h, w];
        var col = new Complex[h];
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) col[y] = rows[y][x];
            FftSharp.FFT.Forward(col);
            for (int y = 0; y < h; y++) result[y, x] = col[y];
        }
        return result;
    }

    public static float[,] Inverse2DReal(Complex[,] freq)
    {
        int h = freq.GetLength(0);
        int w = freq.GetLength(1);

        // Column-wise inverse.
        var cols = new Complex[w][];
        for (int x = 0; x < w; x++)
        {
            var col = new Complex[h];
            for (int y = 0; y < h; y++) col[y] = freq[y, x];
            FftSharp.FFT.Inverse(col);
            cols[x] = col;
        }

        // Row-wise inverse.
        var result = new float[h, w];
        var row = new Complex[w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) row[x] = cols[x][y];
            FftSharp.FFT.Inverse(row);
            for (int x = 0; x < w; x++) result[y, x] = (float)row[x].Real;
        }
        return result;
    }
}
