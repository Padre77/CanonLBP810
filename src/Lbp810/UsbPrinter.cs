// SPDX-License-Identifier: GPL-2.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lbp810;

internal sealed class UsbPrinter : IDisposable
{
    private readonly FileStream stream;
    private readonly string devicePath;
    private static readonly Guid InterfaceId = new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");

    public static string Resolve(string device)
    {
        if (device != "auto") return device;
        return SelectLbp810(List());
    }

    internal static string SelectLbp810(IEnumerable<string> devices)
    {
        var matches = devices.Where(d => d.Contains("usb#vid_04a9&pid_260a#", StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new IOException("Canon LBP-810 not connected or powered off."),
            _ => throw new IOException("Multiple LBP-810 printers found. Select an explicit USBPRINT path.")
        };
    }

    public static IReadOnlyList<string> List()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("USB transport requires Windows.");
        Guid guid = InterfaceId;
        nint set = SetupDiGetClassDevs(ref guid, null, 0, 0x12); // PRESENT | DEVICEINTERFACE
        if (set == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        var paths = new List<string>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var info = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                SetupDiGetDeviceInterfaceDetail(set, ref info, 0, 0, out uint required, 0);
                if (required < 8) throw new Win32Exception(Marshal.GetLastWin32Error());
                nint detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref info, detail, required, out _, 0))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    paths.Add(Marshal.PtrToStringUni(detail + 4)!);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    public UsbPrinter(string path)
    {
        devicePath = path;
        if (!List().Contains(path, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Path is not a currently present USBPRINT interface.");
        var handle = CreateFile(path, 0xc0000000, 3, 0, 3, 0x40000000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Cannot open USB printer for bidirectional I/O.");
        }
        stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: true);
    }

    public void SoftReset()
    {
        // Standard USB printer class reset via Microsoft's usbprint.sys; no driver replacement.
        using var control = CreateFile(devicePath, 0xc0000000, 3, 0, 3, 0, 0);
        if (control.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        const uint ioctlUsbprintSoftReset = (0x22u << 16) | (16u << 2);
        if (!DeviceIoControl(control, ioctlUsbprintSoftReset, 0, 0, 0, 0, out _, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "USB printer soft reset failed.");
    }

    public async Task Write(byte[] data, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await stream.WriteAsync(data, deadline.Token);
        await stream.FlushAsync(deadline.Token);
    }

    public async Task<byte[]> ReadReply(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var reply = new List<byte>();
        int length = 4;
        while (reply.Count < length)
        {
            var chunk = new byte[256];
            int n = await stream.ReadAsync(chunk, deadline.Token);
            if (n == 0) throw new IOException("USB printer returned an empty reply.");
            reply.AddRange(chunk.AsSpan(0, n).ToArray());
            if (reply.Count >= 4)
            {
                length = reply[2] | reply[3] << 8;
                if (length < 4 || length > 256) throw new InvalidDataException($"Unexpected CAPT reply: {Convert.ToHexString(reply.ToArray())}");
            }
        }
        if (reply.Count != length) throw new InvalidDataException("CAPT reply has extra bytes; inspect trace before continuing.");
        return reply.ToArray();
    }

    public void Dispose() => stream.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public nuint Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern nint SetupDiGetClassDevs(ref Guid guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint device, ref Guid guid, uint index, ref DeviceInterfaceData info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref DeviceInterfaceData info, nint detail, uint size, out uint required, nint device);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputLength,
        nint output, uint outputLength, out uint returned, nint overlapped);
}
