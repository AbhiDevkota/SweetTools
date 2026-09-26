using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Xunit;

namespace LuaToolsGui.Tests;

public class IconGeneratorTests
{
    private struct IconEntry
    {
        public byte Width;
        public byte Height;
        public byte Colors;
        public byte Reserved;
        public ushort Planes;
        public ushort Bpp;
        public int Size;
        public int Offset;
    }

    public static byte[] BuildIco(Bitmap original, int[] sizes)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Header
        bw.Write((ushort)0); // reserved
        bw.Write((ushort)1); // type 1 = icon
        bw.Write((ushort)sizes.Length);

        int headerAndEntriesSize = 6 + (16 * sizes.Length);
        int currentOffset = headerAndEntriesSize;

        var entries = new IconEntry[sizes.Length];
        var buffers = new byte[sizes.Length][];

        for (int i = 0; i < sizes.Length; i++)
        {
            int size = sizes[i];
            using var resized = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(original, 0, 0, size, size);
            }

            byte[] buffer;
            if (size <= 128)
            {
                int xorStride = size * 4;
                int andStride = ((size + 31) / 32) * 4;
                int xorSize = xorStride * size;
                int andSize = andStride * size;
                int dibSize = 40 + xorSize + andSize;

                buffer = new byte[dibSize];
                using var dibMs = new MemoryStream(buffer);
                using var dibBw = new BinaryWriter(dibMs);

                // BITMAPINFOHEADER
                dibBw.Write(40);
                dibBw.Write(size);
                dibBw.Write(size * 2);
                dibBw.Write((ushort)1);
                dibBw.Write((ushort)32);
                dibBw.Write(0);
                dibBw.Write(xorSize + andSize);
                dibBw.Write(0);
                dibBw.Write(0);
                dibBw.Write(0);
                dibBw.Write(0);

                var data = resized.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] pixelBytes = new byte[data.Stride * size];
                    Marshal.Copy(data.Scan0, pixelBytes, 0, pixelBytes.Length);

                    // XOR mask: bottom to top
                    for (int y = size - 1; y >= 0; y--)
                    {
                        int rowStart = y * data.Stride;
                        for (int x = 0; x < size; x++)
                        {
                            dibBw.Write(pixelBytes[rowStart + x * 4 + 0]); // B
                            dibBw.Write(pixelBytes[rowStart + x * 4 + 1]); // G
                            dibBw.Write(pixelBytes[rowStart + x * 4 + 2]); // R
                            dibBw.Write(pixelBytes[rowStart + x * 4 + 3]); // A
                        }
                    }

                    // AND mask: bottom to top
                    for (int y = size - 1; y >= 0; y--)
                    {
                        int rowStart = y * data.Stride;
                        byte currentByte = 0;
                        int bitIndex = 7;
                        int bytesWritten = 0;

                        for (int x = 0; x < size; x++)
                        {
                            byte a = pixelBytes[rowStart + x * 4 + 3];
                            if (a == 0)
                            {
                                currentByte |= (byte)(1 << bitIndex);
                            }
                            bitIndex--;
                            if (bitIndex < 0)
                            {
                                dibBw.Write(currentByte);
                                bytesWritten++;
                                currentByte = 0;
                                bitIndex = 7;
                            }
                        }
                        if (bitIndex != 7)
                        {
                            dibBw.Write(currentByte);
                            bytesWritten++;
                        }
                        while (bytesWritten < andStride)
                        {
                            dibBw.Write((byte)0);
                            bytesWritten++;
                        }
                    }
                }
                finally
                {
                    resized.UnlockBits(data);
                }
            }
            else
            {
                using var pngMs = new MemoryStream();
                resized.Save(pngMs, ImageFormat.Png);
                buffer = pngMs.ToArray();
            }

            buffers[i] = buffer;
            byte bSize = size >= 256 ? (byte)0 : (byte)size;
            entries[i] = new IconEntry
            {
                Width = bSize,
                Height = bSize,
                Colors = 0,
                Reserved = 0,
                Planes = 1,
                Bpp = 32,
                Size = buffer.Length,
                Offset = currentOffset
            };
            currentOffset += buffer.Length;
        }

        for (int i = 0; i < sizes.Length; i++)
        {
            var e = entries[i];
            bw.Write(e.Width);
            bw.Write(e.Height);
            bw.Write(e.Colors);
            bw.Write(e.Reserved);
            bw.Write(e.Planes);
            bw.Write(e.Bpp);
            bw.Write(e.Size);
            bw.Write(e.Offset);
        }

        for (int i = 0; i < sizes.Length; i++)
        {
            bw.Write(buffers[i]);
        }

        return ms.ToArray();
    }

    [Fact]
    public void AppIcon_ExistsAndHasAllStandardResolutions()
    {
        string projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LuaToolsGui"));
        string targetIco = Path.Combine(projectDir, "icon.ico");

        Assert.True(File.Exists(targetIco), $"Target icon.ico must exist at {targetIco}");

        byte[] icoBytes = File.ReadAllBytes(targetIco);

        // Validate with System.Drawing.Icon
        using (var ms = new MemoryStream(icoBytes))
        using (var icon = new Icon(ms))
        {
            Assert.NotNull(icon);
            Assert.True(icon.Width > 0);
            Assert.True(icon.Height > 0);
        }

        // Validate with WPF IconBitmapDecoder
        using (var ms = new MemoryStream(icoBytes))
        {
            var decoder = new IconBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.Default);
            int[] expectedSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
            Assert.Equal(expectedSizes.Length, decoder.Frames.Count);

            var actualSizes = decoder.Frames.Select(f => f.PixelWidth).OrderBy(w => w).ToArray();
            Assert.Equal(expectedSizes, actualSizes);
        }
    }

    [Fact]
    public void BuildIco_GeneratesValidIconFromBitmap()
    {
        using var testBmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(testBmp))
        {
            g.Clear(Color.Red);
        }

        int[] sizes = [16, 32, 64];
        byte[] icoBytes = BuildIco(testBmp, sizes);

        using var ms = new MemoryStream(icoBytes);
        var decoder = new IconBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.Default);
        Assert.Equal(3, decoder.Frames.Count);

        var actualSizes = decoder.Frames.Select(f => f.PixelWidth).OrderBy(w => w).ToArray();
        Assert.Equal([16, 32, 64], actualSizes);
    }

    [Fact]
    public void SweetToolsIconPngBase64_MatchesIconIcoDerivedPng()
    {
        string projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LuaToolsGui"));
        string targetIco = Path.Combine(projectDir, "icon.ico");
        byte[] icoBytes = File.ReadAllBytes(targetIco);

        // Find PNG chunk in ICO (starts with standard 8-byte PNG header)
        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        int pngOffset = -1;
        for (int i = 0; i < icoBytes.Length - 8; i++)
        {
            if (icoBytes.AsSpan(i, 8).SequenceEqual(pngHeader))
            {
                pngOffset = i;
                break;
            }
        }
        Assert.True(pngOffset >= 0, "PNG frame must exist in icon.ico");
        byte[] png256Bytes = icoBytes[pngOffset..];

        using var ms256 = new MemoryStream(png256Bytes);
        using var bmp256 = new Bitmap(ms256);
        Assert.Equal(256, bmp256.Width);
        Assert.Equal(256, bmp256.Height);

        // Generate 48x48 crisp PNG at standard 96 DPI
        using var resized48 = new Bitmap(48, 48, PixelFormat.Format32bppArgb);
        resized48.SetResolution(96f, 96f);
        using (var g = Graphics.FromImage(resized48))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(bmp256, 0, 0, 48, 48);
        }

        using var ms48 = new MemoryStream();
        resized48.Save(ms48, ImageFormat.Png);
        byte[] png48Bytes = ms48.ToArray();

        // Validate that Image.FromStream can load it
        using var checkMs = new MemoryStream(png48Bytes);
        using var loaded = Image.FromStream(checkMs);
        Assert.Equal(48, loaded.Width);
        Assert.Equal(48, loaded.Height);

        string actualB64 = Convert.ToBase64String(png48Bytes);
        Assert.Equal(LuaToolsGui.Services.HttpServerService.SweetToolsIconPngBase64, actualB64);
    }
}
