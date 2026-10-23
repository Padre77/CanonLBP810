// SPDX-License-Identifier: GPL-2.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Lbp810;

internal static class WindowsTestPage
{
    private const string PrinterName = "Canon LBP-810 Native";

    public static void Print(int pages = 1, bool landscape = false)
    {
        nint dc = CreatePrinterDc(landscape);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDC failed.");
        bool started = false;
        nint font = 0, oldFont = 0;
        try
        {
            int width = GetDeviceCaps(dc, 8), height = GetDeviceCaps(dc, 10);
            Console.WriteLine($"GDI printable area: {width} x {height}; landscape={landscape}");
            if (landscape && width <= height) throw new InvalidOperationException("Driver did not enable landscape orientation.");
            var doc = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), Name = landscape ?
                "LBP-810 landscape Windows GDI test" : "LBP-810 native Windows GDI test" };
            if (StartDoc(dc, ref doc) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDoc failed.");
            started = true;
            font = CreateFont(-80, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Arial");
            if (font == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            oldFont = SelectObject(dc, font);
            for (int page = 1; page <= pages; page++)
            {
            if (StartPage(dc) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPage failed.");
            if (!Rectangle(dc, 120, 120, width - 120, height - 120)) throw new Win32Exception(Marshal.GetLastWin32Error());
            string[] lines = ["Canon LBP-810 - Native Windows x64", "Тест печати: русский текст и латиница.",
                "Windows GDI -> Unidrv -> CAPT -> USB", $"600 dpi / A4 / {(landscape ? "LANDSCAPE" : "PORTRAIT")} - Page {page} of {pages}"];
            for (int i = 0; i < lines.Length; i++)
                if (!TextOut(dc, 200, 250 + i * 150, lines[i], lines[i].Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            if (landscape)
            {
                string right = "RIGHT / ПРАВЫЙ КРАЙ";
                string bottom = "BOTTOM / НИЖНИЙ КРАЙ";
                if (!TextOut(dc, width - 1500, 250, right, right.Length) ||
                    !TextOut(dc, 200, height - 350, bottom, bottom.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (EndPage(dc) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "EndPage failed.");
            }
            if (EndDoc(dc) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "EndDoc failed.");
            started = false;
            Console.WriteLine("Windows GDI test submitted to Canon LBP-810 Native.");
        }
        finally
        {
            if (started) AbortDoc(dc);
            if (oldFont != 0) SelectObject(dc, oldFont);
            if (font != 0) DeleteObject(font);
            DeleteDC(dc);
        }
    }

    private static nint CreatePrinterDc(bool landscape)
    {
        if (!landscape) return CreateDC("WINSPOOL", PrinterName, null, 0);
        if (!OpenPrinter(PrinterName, out nint printer, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint mode = 0;
        try
        {
            int size = DocumentProperties(0, printer, PrinterName, 0, 0, 0);
            if (size < 220) throw new InvalidOperationException("Invalid Unicode DEVMODE size.");
            mode = Marshal.AllocHGlobal(size);
            if (DocumentProperties(0, printer, PrinterName, mode, 0, 2) != 1)
                throw new InvalidOperationException("Cannot read printer DEVMODE.");
            // Offsets in the public Unicode DEVMODEW, followed by preserved driver-private data.
            Marshal.WriteInt32(mode, 72, Marshal.ReadInt32(mode, 72) | 1); // DM_ORIENTATION
            Marshal.WriteInt16(mode, 76, 2); // DMORIENT_LANDSCAPE
            if (DocumentProperties(0, printer, PrinterName, mode, mode, 10) != 1 || Marshal.ReadInt16(mode, 76) != 2)
                throw new InvalidOperationException("Landscape DEVMODE was rejected.");
            return CreateDC("WINSPOOL", PrinterName, null, mode);
        }
        finally
        {
            if (mode != 0) Marshal.FreeHGlobal(mode);
            ClosePrinter(printer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo { public int Size; public string Name; public string? Output, DataType; public uint Type; }
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenPrinter(string name, out nint printer, nint defaults);
    [DllImport("winspool.drv")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(nint printer);
    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode)]
    private static extern int DocumentProperties(nint window, nint printer, string name, nint output, nint input, uint mode);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(nint dc, int index);
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDC(string driver, string device, string? output, nint devmode);
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDoc(nint dc, ref DocInfo doc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StartPage(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndPage(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndDoc(nint dc);
    [DllImport("gdi32.dll")] private static extern int AbortDoc(nint dc);
    [DllImport("gdi32.dll", EntryPoint = "TextOutW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TextOut(nint dc, int x, int y, string text, int count);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Rectangle(nint dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFont(int height, int width, int escape, int orientation, int weight,
        uint italic, uint underline, uint strikeout, uint charset, uint outPrecision, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
