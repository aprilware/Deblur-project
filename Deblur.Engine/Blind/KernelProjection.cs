namespace Deblur.Engine.Blind;

public static class KernelProjection
{
    public static float[,] Project(float[,] rawKernel, int windowSize, float sparsityThreshold)
    {
        if (windowSize < 1 || windowSize % 2 == 0)
            throw new ArgumentException("windowSize must be a positive odd integer", nameof(windowSize));

        int rh = rawKernel.GetLength(0);
        int rw = rawKernel.GetLength(1);

        // Find argmax.
        float rawMax = float.NegativeInfinity;
        int argY = 0, argX = 0;
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
                if (rawKernel[y, x] > rawMax) { rawMax = rawKernel[y, x]; argY = y; argX = x; }

        int radius = windowSize / 2;
        // Clamp crop origin so output is still windowSize × windowSize.
        int y0 = Math.Clamp(argY - radius, 0, Math.Max(0, rh - windowSize));
        int x0 = Math.Clamp(argX - radius, 0, Math.Max(0, rw - windowSize));

        var result = new float[windowSize, windowSize];
        for (int y = 0; y < windowSize; y++)
        {
            int sy = y0 + y;
            if (sy < 0 || sy >= rh) continue;
            for (int x = 0; x < windowSize; x++)
            {
                int sx = x0 + x;
                if (sx < 0 || sx >= rw) continue;
                result[y, x] = rawKernel[sy, sx];
            }
        }

        // Sparsity threshold (against post-crop max).
        float postMax = 0;
        for (int y = 0; y < windowSize; y++)
            for (int x = 0; x < windowSize; x++)
                if (result[y, x] > postMax) postMax = result[y, x];
        float minVal = postMax * sparsityThreshold;
        for (int y = 0; y < windowSize; y++)
            for (int x = 0; x < windowSize; x++)
                if (result[y, x] < minVal) result[y, x] = 0f;

        // Non-negativity.
        for (int y = 0; y < windowSize; y++)
            for (int x = 0; x < windowSize; x++)
                if (result[y, x] < 0f) result[y, x] = 0f;

        // Normalize to sum = 1.
        float sum = 0;
        for (int y = 0; y < windowSize; y++)
            for (int x = 0; x < windowSize; x++)
                sum += result[y, x];
        if (sum > 0f)
        {
            float inv = 1f / sum;
            for (int y = 0; y < windowSize; y++)
                for (int x = 0; x < windowSize; x++)
                    result[y, x] *= inv;
        }
        else
        {
            // Degenerate: return a centered delta.
            result[windowSize / 2, windowSize / 2] = 1f;
        }
        return result;
    }
}
