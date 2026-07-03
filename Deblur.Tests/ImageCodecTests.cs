using Deblur.Engine;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class ImageCodecTests
{
    [Fact]
    public void PngRoundTrip_IsLossless()
    {
        var original = SyntheticImages.Checkerboard(32, 32, 4);
        byte[] png = ImageCodec.EncodePng(original);
        var decoded = ImageCodec.DecodeFromBytes(png);
        Assert.Equal(original.Width, decoded.Width);
        Assert.Equal(original.Height, decoded.Height);
        // 8-bit round-trip: allow 1/255 tolerance.
        for (int i = 0; i < original.PixelCount; i++)
        {
            Assert.InRange(decoded.R[i], original.R[i] - 1f / 255f, original.R[i] + 1f / 255f);
            Assert.InRange(decoded.G[i], original.G[i] - 1f / 255f, original.G[i] + 1f / 255f);
            Assert.InRange(decoded.B[i], original.B[i] - 1f / 255f, original.B[i] + 1f / 255f);
        }
    }

    [Fact]
    public void JpegRoundTrip_Quality92_HighFidelity()
    {
        var original = SyntheticImages.Checkerboard(64, 64, 8);
        byte[] jpeg = ImageCodec.EncodeJpeg(original, quality: 92);
        var decoded = ImageCodec.DecodeFromBytes(jpeg);
        Assert.True(SyntheticImages.Psnr(original, decoded) > 40f);
    }

    [Fact]
    public void GarbageInput_ThrowsInvalidImageFormat()
    {
        var garbage = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44 };
        Assert.Throws<InvalidImageFormatException>(() => ImageCodec.DecodeFromBytes(garbage));
    }
}
