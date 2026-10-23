// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;

namespace Lbp810;

internal static class SelfTest
{
    public static void Run()
    {
        string expected = @"\\?\usb#vid_04a9&pid_260a#OTHER-SERIAL#{28d78fad-5a12-11d1-ae5b-0000f803a8c2}";
        string unrelated = @"\\?\usb#vid_03f0&pid_0001#OTHER#{28d78fad-5a12-11d1-ae5b-0000f803a8c2}";
        if (UsbPrinter.SelectLbp810([unrelated, expected]) != expected) throw new Exception("USB discovery mismatch.");
        foreach (string[] invalid in new[] { new[] { unrelated }, new[] { expected, expected.Replace("OTHER-SERIAL", "SECOND") } })
        {
            try { UsbPrinter.SelectLbp810(invalid); throw new Exception("Ambiguous/missing printer was accepted."); }
            catch (IOException) { }
        }
        Console.WriteLine("PASS: USB auto discovery ignores serials and rejects missing/ambiguous devices.");
        var random = new Random(810);
        var noise = new byte[CaptRaster.Stride * CaptRaster.Height];
        random.NextBytes(noise);
        byte[][] cases = [new byte[noise.Length], Enumerable.Repeat((byte)255, noise.Length).ToArray(),
            Raster.Pattern().Data, noise];
        foreach (byte[] input in cases)
        {
            var packets = CaptRaster.Encode(input);
            byte[] decoded = Decode(packets);
            if (!input.SequenceEqual(decoded)) throw new Exception("CAPT round trip mismatch.");
            Console.WriteLine($"PASS: {input.Length} raster bytes -> {packets.Count} packets / {packets.Sum(p => p.Length)} bytes");
        }
        // Reply framing reference: 6-byte header + 16 status bytes, aggregate size=22.
        byte[] status = [0xa1, 0xa0, 22, 0, 0, 0x10, 0xd0, 0, 0, 2, 0xb0, 9, 0xb3, 13, 0x7d, 0, 0xfd, 0, 0, 0, 0, 0];
        if (status[16] != 0xfd || BinaryPrimitives.ReadUInt16LittleEndian(status.AsSpan(2)) != status.Length)
            throw new Exception("Status layout mismatch.");
        Console.WriteLine("Encoder verification passed; USB hardware and print completion are NOT tested.");
    }

    private static byte[] Decode(List<byte[]> packets)
    {
        using var output = new MemoryStream();
        int rows = 0, bands = 0;
        bool end = false;
        foreach (var packet in packets)
        {
            if (end || packet[0] != 0xa0 || packet[1] != 0xc0 || packet.Length > 65500 ||
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) != packet.Length)
                throw new Exception("Invalid CAPT packet framing.");
            int p = 4;
            while (p < packet.Length)
            {
                if (packet[p] == 0x42)
                {
                    if (++p != packet.Length) throw new Exception("Trailing bytes after page end.");
                    end = true;
                    break;
                }
                if (packet[p++] != 0x40) throw new Exception("Missing band start.");
                int count = Math.Min(CaptRaster.RowsPerBand, CaptRaster.Height - rows);
                if (count <= 0) throw new Exception("Extra band.");
                for (int row = 0; row < count; row++)
                {
                    int line = 0;
                    while (line < CaptRaster.Stride)
                    {
                        byte a = packet[p++];
                        int n; bool literal;
                        if ((a & 0xe0) == 0xa0)
                        {
                            byte b = packet[p++];
                            n = ((a & 31) << 3) | ((b >> 3) & 7);
                            literal = (b & 0xc0) == 0xc0;
                            if (!literal && (b & 0xc0) != 0x80) throw new Exception("Unsupported long run.");
                        }
                        else
                        {
                            n = (a >> 3) & 7;
                            literal = (a & 0xc0) == 0;
                            if (!literal && (a & 0xc0) != 0x40) throw new Exception("Unsupported short run.");
                        }
                        if (n == 0 || line + n > CaptRaster.Stride) throw new Exception("Invalid line length.");
                        if (literal) { output.Write(packet, p, n); p += n; }
                        else { byte value = packet[p++]; for (int k = 0; k < n; k++) output.WriteByte(value); }
                        line += n;
                    }
                    rows++;
                }
                if (packet[p++] != 0x40) throw new Exception("Missing band end.");
                bands++;
            }
        }
        if (!end || rows != CaptRaster.Height || bands != (CaptRaster.Height + 104) / 105)
            throw new Exception("Incomplete CAPT page.");
        return output.ToArray();
    }
}
