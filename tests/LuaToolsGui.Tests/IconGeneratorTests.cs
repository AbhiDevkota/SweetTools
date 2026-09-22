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

    public static byte[] BuildIco(string sourcePngPath, int[] sizes)
    {
        using var original = new Bitmap(sourcePngPath);
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
    public void GenerateAndValidateAppIcon()
    {
        string projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LuaToolsGui"));
        string sourcePng = Path.Combine(projectDir, "logo ST.png");
        string targetIco = Path.Combine(projectDir, "icon.ico");

        Assert.True(File.Exists(sourcePng), $"Source PNG must exist at {sourcePng}");

        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
        byte[] icoBytes = BuildIco(sourcePng, sizes);

        // Validate with System.Drawing.Icon
        using (var ms = new MemoryStream(icoBytes))
        using (var icon = new Icon(ms))
        {
            Assert.NotNull(icon);
        }

        // Validate with WPF IconBitmapDecoder
        using (var ms = new MemoryStream(icoBytes))
        {
            var decoder = new IconBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.Default);
            Assert.Equal(sizes.Length, decoder.Frames.Count);
            foreach (var frame in decoder.Frames)
            {
                Assert.True(frame.PixelWidth > 0);
                Assert.True(frame.PixelHeight > 0);
            }
        }

        // Write to destination
        File.WriteAllBytes(targetIco, icoBytes);
        Assert.True(File.Exists(targetIco));
    }
}
