// SPDX-License-Identifier: GPL-2.0-or-later
// Initialization/page commands adapted from Nicolas Boichat's capt.c (2004).
namespace Lbp810;

internal sealed class CaptSession(UsbPrinter usb, TextWriter trace)
{
    private async Task<byte[]> Command(byte a, byte b, bool reply, CancellationToken ct, byte[]? payload = null)
    {
        payload ??= [];
        var command = new byte[4 + payload.Length];
        command[0] = a; command[1] = b;
        command[2] = checked((byte)command.Length);
        payload.CopyTo(command, 4);
        trace.WriteLine($"TX {Convert.ToHexString(command)}");
        await usb.Write(command, ct);
        await Task.Delay(8, ct);
        if (!reply) return [];
        byte[] response = await usb.ReadReply(ct);
        trace.WriteLine($"RX {Convert.ToHexString(response)}");
        if (response[0] != a || response[1] != b)
            throw new InvalidDataException("CAPT response opcode mismatch; see trace.");
        await Task.Delay(8, ct);
        return response;
    }

    public async Task Probe(CancellationToken ct)
    {
        await Command(0xa0, 0xe0, true, ct);
        await Command(0xa1, 0xa0, true, ct);
    }

    public async Task Print(IReadOnlyList<byte[]> packets, CancellationToken ct, bool firstPage = true)
    {
        if (firstPage) await Command(0xa1, 0xa1, true, ct);
        bool paper = false;
        for (int i = 0; i < 120; i++)
        {
            await Command(0xa0, 0xa0, true, ct);
            byte[] status = await Command(0xa1, 0xa0, true, ct);
            // The reference reads a 6-byte header followed by 16 status bytes.
            if (status.Length >= 22 && status[16] == 0xfd) { paper = true; break; }
            await Task.Delay(1000, ct);
        }
        if (!paper) throw new TimeoutException("Printer did not report paper ready within 120 seconds.");
        if (firstPage)
        {
        await Command(0xa0, 0xa2, true, ct);
        await Command(0xa0, 0xe0, true, ct);
        await Command(0xa1, 0xa0, true, ct);
        await Command(0xa4, 0xe0, true, ct);
        await Command(0xa5, 0xe0, true, ct, [0xee, 0xdb, 0xea, 0xad, 0, 0, 0, 0]);
        await Command(0xa0, 0xe0, true, ct);
        await Command(0xa0, 0xa0, true, ct);
        await Command(0xa1, 0xa0, true, ct);
        await Command(0xa0, 0xe0, true, ct);
        }
        await Command(0xa0, 0xd0, false, ct, [
            0, 0, 0xa4, 1, 2, 1, 0, 0, 0x1f, 0x1f, 0x1f, 0x1f,
            0, 0x11, 3, 1, 1, 1, 2, 0, 0, 0, 0x70, 0, 0x78, 0,
            0x50, 2, 0x7a, 0x1a, 0x60, 0x13, 0x67, 0x1b]);
        await Command(0xa0, 0xe0, true, ct);
        await Command(0xa1, 0xd0, false, ct);
        await Command(0xa0, 0xe0, true, ct);
        await Command(0xa0, 0xa0, true, ct);
        await Command(0xa1, 0xa0, true, ct);
        foreach (byte[] packet in packets)
        {
            bool ready = false;
            for (int attempt = 0; attempt < 300; attempt++)
            {
                byte[] status = await Command(0xa0, 0xe0, true, ct);
                if (status.Length >= 6 && (status[4] & 8) == 0) { ready = true; break; }
                await Task.Delay(100, ct);
            }
            if (!ready) throw new TimeoutException("Printer buffer did not become ready.");
            await Task.Delay(16, ct);
            trace.WriteLine($"DATA {packet.Length} bytes");
            for (int offset = 0; offset < packet.Length; offset += 4096)
            {
                await usb.Write(packet.AsSpan(offset, Math.Min(4096, packet.Length - offset)).ToArray(), ct);
                await Task.Delay(16, ct);
            }
            await Task.Delay(40, ct);
        }
        await Command(0xa0, 0xe0, true, ct);
        await Command(0xa2, 0xd0, false, ct);
        await Command(0xa0, 0xe0, true, ct);
        await Command(0xa1, 0xa0, true, ct);
        trace.WriteLine("Page sent. Physical output/completion must be checked on the printer.");
    }
}
