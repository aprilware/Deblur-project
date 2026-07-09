namespace Deblur.Engine.Validation;

public static class Quality
{
    public static double Psnr(ImageBuffer reference, ImageBuffer test)
    {
        if (reference.Width != test.Width || reference.Height != test.Height)
            throw new ArgumentException("size mismatch");
        double mse = (Mse(reference.R, test.R) + Mse(reference.G, test.G) + Mse(reference.B, test.B)) / 3.0;
        if (mse <= 0) return double.PositiveInfinity;
        return 10.0 * Math.Log10(1.0 / mse);
    }

    public static double Ssim(ImageBuffer reference, ImageBuffer test)
    {
        double sR = ChannelSsim(reference.R, test.R, reference.Width, reference.Height);
        double sG = ChannelSsim(reference.G, test.G, reference.Width, reference.Height);
        double sB = ChannelSsim(reference.B, test.B, reference.Width, reference.Height);
        return (sR + sG + sB) / 3.0;
    }

    private static double Mse(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; s += d * d; }
        return s / a.Length;
    }

    private static double ChannelSsim(float[] a, float[] b, int w, int h)
    {
        // 11x11 Gaussian window, sigma=1.5.
        const int R = 5;
        var win = new double[11, 11];
        double wsum = 0;
        for (int j = -R; j <= R; j++)
            for (int i = -R; i <= R; i++)
            {
                double v = Math.Exp(-(i * i + j * j) / (2.0 * 1.5 * 1.5));
                win[j + R, i + R] = v; wsum += v;
            }
        for (int j = 0; j < 11; j++)
            for (int i = 0; i < 11; i++)
                win[j, i] /= wsum;

        const double K1 = 0.01, K2 = 0.03, L = 1.0;
        double C1 = (K1 * L) * (K1 * L);
        double C2 = (K2 * L) * (K2 * L);

        double total = 0; long count = 0;
        for (int y = R; y < h - R; y++)
        {
            for (int x = R; x < w - R; x++)
            {
                double muA = 0, muB = 0;
                for (int j = -R; j <= R; j++)
                    for (int i = -R; i <= R; i++)
                    {
                        double wij = win[j + R, i + R];
                        muA += wij * a[(y + j) * w + (x + i)];
                        muB += wij * b[(y + j) * w + (x + i)];
                    }
                double sA = 0, sB = 0, sAB = 0;
                for (int j = -R; j <= R; j++)
                    for (int i = -R; i <= R; i++)
                    {
                        double wij = win[j + R, i + R];
                        double dA = a[(y + j) * w + (x + i)] - muA;
                        double dB = b[(y + j) * w + (x + i)] - muB;
                        sA += wij * dA * dA;
                        sB += wij * dB * dB;
                        sAB += wij * dA * dB;
                    }
                double num = (2 * muA * muB + C1) * (2 * sAB + C2);
                double den = (muA * muA + muB * muB + C1) * (sA + sB + C2);
                total += num / den; count++;
            }
        }
        return count > 0 ? total / count : 1.0;
    }
}
