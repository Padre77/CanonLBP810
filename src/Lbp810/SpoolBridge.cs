// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lbp810;

internal static class SpoolBridge
{
    public static async Task Serve(string device, string directory, bool captureOnly, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var listener = new TcpListener(IPAddress.Loopback, 18100);
        listener.Start(8);
        Console.WriteLine($"LBP-810 bridge listening at 127.0.0.1:18100; captureOnly={captureOnly}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(ct);
                string job = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                using var log = new StreamWriter(job + ".log") { AutoFlush = true };
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromMinutes(10));
                    using var stream = new BufferedStream(client.GetStream(), 65536);
                    await Receive(stream, device, job, log, captureOnly, deadline.Token);
                    Console.WriteLine($"Job processed: {job}");
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.WriteLine(e.ToString());
                    Console.Error.WriteLine($"Job failed (no automatic reprint): {job}: {e.Message}");
                }
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task Receive(Stream stream, string device, string job, TextWriter log, bool captureOnly, CancellationToken ct)
    {
        byte[]? raster = null;
        int rows = 0, pages = 0, x = 0, y = 0;
        bool started = false, finished = false;
        using var usb = captureOnly ? null : new UsbPrinter(UsbPrinter.Resolve(device));
        var session = usb is null ? null : new CaptSession(usb, log);
        while (true)
        {
            string? line = await ReadLine(stream, ct);
            if (line is null) break;
            switch (line)
            {
                case "J" when !started:
                    started = true;
                    break;
                case "P" when started && !finished && raster is null:
                    raster = new byte[CaptRaster.Stride * CaptRaster.Height];
                    rows = 0;
                    x = y = 0;
                    break;
                case "E" when raster is not null:
                    log.WriteLine($"Page: {rows} raster rows, last cursor ({x}, {y}).");
                    pages++;
                    if (captureOnly) new Raster(CaptRaster.Width, CaptRaster.Height, raster).WritePbm($"{job}-page{pages}.pbm");
                    if (session is not null) await session.Print(CaptRaster.Encode(raster), ct, firstPage: pages == 1);
                    raster = null;
                    break;
                case "Q" when started && raster is null:
                    finished = true;
                    break;
                case "L" when raster is not null:
                    y++;
                    break;
                default:
                    if (raster is not null && line.Length > 2 && line[1] == ' ' &&
                        (line[0] == 'X' || line[0] == 'Y') && int.TryParse(line.AsSpan(2), out int coordinate))
                    {
                        log.WriteLine(line);
                        if (coordinate < 0 || coordinate >= (line[0] == 'X' ? CaptRaster.Width : CaptRaster.Height))
                            throw new InvalidDataException("Raster cursor outside page.");
                        if (line[0] == 'X') x = coordinate; else y = coordinate;
                        break;
                    }
                    if (raster is null || !line.StartsWith("D ") || !int.TryParse(line.AsSpan(2), out int length) ||
                        length < 1 || length > CaptRaster.Stride || y >= CaptRaster.Height || x + length * 8 > CaptRaster.Width)
                        throw new InvalidDataException($"Unexpected Unidrv command: {line}");
                    var block = new byte[length];
                    await stream.ReadExactlyAsync(block, ct);
                    for (int bit = 0; bit < length * 8; bit++)
                        if ((block[bit / 8] & (0x80 >> (bit % 8))) != 0)
                            raster[y * CaptRaster.Stride + (x + bit) / 8] |= (byte)(0x80 >> ((x + bit) % 8));
                    rows++;
                    y++;
                    x = 0;
                    break;
            }
        }
        if (!started || !finished || raster is not null || pages == 0)
            throw new InvalidDataException("Incomplete spool job.");
        log.WriteLine($"Completed stream: {pages} page(s). Physical output requires confirmation.");
    }

    private static async Task<string?> ReadLine(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, ct) != 0)
        {
            if (one[0] == 10) return Encoding.ASCII.GetString(bytes.ToArray());
            if (bytes.Count >= 80) throw new InvalidDataException("Spool command exceeds 80 bytes.");
            bytes.Add(one[0]);
        }
        if (bytes.Count != 0) throw new EndOfStreamException("Partial spool command.");
        return null;
    }
}
