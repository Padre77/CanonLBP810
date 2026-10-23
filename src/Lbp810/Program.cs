// SPDX-License-Identifier: GPL-2.0-or-later
using Lbp810;

try
{
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    if (args.Length == 0 || args[0] == "help")
    {
        Console.WriteLine("""
        LBP-810 native x64 CAPT prototype with Unidrv queue bridge
        list                         List present USBPRINT device paths
        pattern FILE.pbm             Save a monochrome test pattern (no printing)
        encode FILE.pbm OUT.capt      Encode printable-area PBM, no USB access
        selftest                     Verify encoder with independent decoder
        serve DEVICE LOGDIR          Run local Unidrv-to-USB bridge at 127.0.0.1:18100
        service LOGDIR               Run under Windows Service Control Manager
        capture LOGDIR               Capture Unidrv output without USB/printing
        windows-test                 Submit GDI text/graphics test to the installed queue
        windows-test-two-pages       Submit two pages as one GDI print job
        windows-test-landscape       Submit wide text/edge-marker test in landscape
        probe DEVICE                 Query CAPT status (no page commands)
        reset DEVICE                 Standard USB printer soft reset (cancels device state)
        print-pbm DEVICE FILE.pbm     Print one printable-area PBM via USB
        test-page DEVICE             Print one generated test pattern via USB
        PBM input is 600 dpi; cropped/padded to 4736 x 6968 pixels, black=1.
        DEVICE must be the exact USBPRINT path from 'list'. Ctrl+C cancels I/O.
        Use DEVICE=auto to find a single connected LBP-810 regardless of its serial.
        """);
        return 0;
    }
    switch (args[0])
    {
        case "list" when args.Length == 1:
            var devices = UsbPrinter.List();
            foreach (var device in devices) Console.WriteLine(device);
            if (devices.Count == 0) Console.WriteLine("No present USBPRINT interfaces. Connect and power on the printer.");
            break;
        case "pattern" when args.Length == 2:
            Raster.Pattern().WritePbm(args[1]);
            Console.WriteLine(Path.GetFullPath(args[1]));
            break;
        case "encode" when args.Length == 3:
            var packets = CaptRaster.Encode(Raster.ReadPbm(args[1]).ToPrintable(0, 0));
            using (var f = File.Create(args[2])) foreach (var packet in packets) f.Write(packet);
            Console.WriteLine($"{packets.Count} packets, {packets.Sum(p => p.Length)} bytes; no USB access.");
            break;
        case "selftest" when args.Length == 1:
            SelfTest.Run();
            break;
        case "windows-test" when args.Length == 1:
            WindowsTestPage.Print();
            break;
        case "windows-test-two-pages" when args.Length == 1:
            WindowsTestPage.Print(2);
            break;
        case "windows-test-landscape" when args.Length == 1:
            WindowsTestPage.Print(landscape: true);
            break;
        case "serve" when args.Length == 3:
            await SpoolBridge.Serve(args[1], args[2], false, cancel.Token);
            break;
        case "service" when args.Length == 2:
            WindowsService.Run(args[1]);
            break;
        case "capture" when args.Length == 2:
            await SpoolBridge.Serve("", args[1], true, cancel.Token);
            break;
        case "probe" when args.Length == 2:
        case "reset" when args.Length == 2:
        case "test-page" when args.Length == 2:
        case "print-pbm" when args.Length == 3:
            // Never select a device automatically: more than one USB printer may be present.
            byte[]? raster = args[0] == "test-page" ? Raster.Pattern().Data :
                args[0] == "print-pbm" ? Raster.ReadPbm(args[2]).ToPrintable(0, 0) : null;
            var encoded = raster is null ? null : CaptRaster.Encode(raster);
            using (var usb = new UsbPrinter(UsbPrinter.Resolve(args[1])))
            {
                var session = new CaptSession(usb, Console.Out);
                if (args[0] == "reset") { usb.SoftReset(); await Task.Delay(500, cancel.Token); }
                if (encoded is null) await session.Probe(cancel.Token);
                else await session.Print(encoded, cancel.Token);
            }
            break;
        default:
            throw new ArgumentException("Invalid arguments. Run 'help'.");
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled or USB operation timed out. No automatic retry."); return 2; }
catch (Exception e) { Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}"); return 1; }
