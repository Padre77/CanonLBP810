// SPDX-License-Identifier: GPL-2.0-or-later
// CAPT format based on Nicolas Boichat's 2004 driver and SPECS.
using System.Buffers.Binary;
using System.Text;

namespace Lbp810;

internal sealed record Raster(int Width, int Height, byte[] Data)
{
    public int Stride => (Width + 7) / 8;

    public static Raster ReadPbm(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int p = 0;
        string Token()
        {
            while (p < data.Length)
            {
                if (data[p] == '#')
                {
                    while (p < data.Length && data[p] != '\n') p++;
                }
                else if (IsSpace(data[p])) p++;
                else break;
            }
            int start = p;
            while (p < data.Length && !IsSpace(data[p]) && data[p] != '#') p++;
            if (p == start) throw new InvalidDataException("Incomplete PBM header.");
            return Encoding.ASCII.GetString(data, start, p - start);
        }
        if (Token() != "P4") throw new InvalidDataException("Expected binary PBM (P4), black=1, MSB first.");
        if (!int.TryParse(Token(), out int w) || !int.TryParse(Token(), out int h) ||
            w < 1 || w > 10000 || h < 1 || h > 15000)
            throw new InvalidDataException("PBM dimensions outside supported bounds.");
        if (p >= data.Length || !IsSpace(data[p])) throw new InvalidDataException("Missing PBM raster separator.");
        // Consume exactly the header separator: raster bytes may themselves be whitespace or '#'.
        byte separator = data[p++];
        if (separator == '\r' && p < data.Length && data[p] == '\n') p++;
        int size = checked(((w + 7) / 8) * h);
        if (data.Length - p != size) throw new InvalidDataException("Expected exactly one complete PBM page.");
        return new(w, h, data.AsSpan(p, size).ToArray());
    }

    private static bool IsSpace(byte c) => c is 9 or 10 or 11 or 12 or 13 or 32;

    public byte[] ToPrintable(int left, int top)
    {
        if (left < 0 || top < 0) throw new ArgumentOutOfRangeException(nameof(left));
        var output = new byte[CaptRaster.Stride * CaptRaster.Height];
        for (int y = 0; y < CaptRaster.Height && y + top < Height; y++)
        for (int x = 0; x < CaptRaster.Width && x + left < Width; x++)
        {
            int sourceX = x + left;
            if ((Data[(y + top) * Stride + sourceX / 8] & (0x80 >> (sourceX % 8))) != 0)
                output[y * CaptRaster.Stride + x / 8] |= (byte)(0x80 >> (x % 8));
        }
        return output;
    }

    public static Raster Pattern()
    {
        int w = CaptRaster.Width, h = CaptRaster.Height;
        var data = new byte[CaptRaster.Stride * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool border = x >= 120 && x < w - 120 && y >= 120 && y < h - 120 &&
                (x < 128 || x >= w - 128 || y < 128 || y >= h - 128);
            bool grid = x >= 300 && x < 2100 && y >= 400 && y < 2200 &&
                ((x - 300) % 300 < 4 || (y - 400) % 300 < 4);
            bool bars = x >= 300 && x < 2100 && y >= 2500 && y < 2900 && (x / 30) % 2 == 0;
            if (border || grid || bars) data[y * CaptRaster.Stride + x / 8] |= (byte)(0x80 >> (x % 8));
        }
        return new(w, h, data);
    }

    public void WritePbm(string path)
    {
        using var f = File.Create(path);
        f.Write(Encoding.ASCII.GetBytes($"P4\n{Width} {Height}\n"));
        f.Write(Data);
    }
}

internal static class CaptRaster
{
    public const int Stride = 592, Width = Stride * 8, Height = 6968;
    // The reference's inclusive cnt loop actually writes 105 rows per full band.
    public const int RowsPerBand = 105;

    public static List<byte[]> Encode(byte[] raster)
    {
        if (raster.Length != Stride * Height) throw new ArgumentException("Expected 4736 x 6968 monochrome raster.");
        var packets = new List<byte[]>();
        using var packet = new MemoryStream();
        packet.Write([0xa0, 0xc0, 0, 0]);
        void Flush()
        {
            byte[] bytes = packet.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), checked((ushort)bytes.Length));
            packets.Add(bytes);
            packet.SetLength(0);
            packet.Write([0xa0, 0xc0, 0, 0]);
        }
        for (int y = 0; y < Height; y += RowsPerBand)
        {
            using var band = new MemoryStream();
            band.WriteByte(0x40);
            int count = Math.Min(RowsPerBand, Height - y);
            for (int row = y; row < y + count; row++) EncodeLine(raster.AsSpan(row * Stride, Stride), band);
            band.WriteByte(0x40);
            if (packet.Length + band.Length + 1 > 65500) Flush();
            band.Position = 0;
            band.CopyTo(packet);
        }
        packet.WriteByte(0x42);
        Flush();
        return packets;
    }

    private static void EncodeLine(ReadOnlySpan<byte> line, Stream output)
    {
        int p = 0;
        while (p < line.Length)
        {
            int run = Run(line, p);
            if (run >= 3)
            {
                if (run < 8) output.WriteByte((byte)(0x40 | run << 3));
                else
                {
                    output.WriteByte((byte)(0xa0 | run >> 3));
                    output.WriteByte((byte)(0x80 | (run & 7) << 3));
                }
                output.WriteByte(line[p]);
                p += run;
            }
            else
            {
                int start = p++;
                while (p < line.Length && p - start < 255 && Run(line, p) < 3) p++;
                int n = p - start;
                if (n < 8) output.WriteByte((byte)(n << 3));
                else
                {
                    output.WriteByte((byte)(0xa0 | n >> 3));
                    output.WriteByte((byte)(0xc0 | (n & 7) << 3));
                }
                output.Write(line.Slice(start, n));
            }
        }
    }

    private static int Run(ReadOnlySpan<byte> line, int p)
    {
        int n = 1;
        while (n < 255 && p + n < line.Length && line[p + n] == line[p]) n++;
        return n;
    }
}
