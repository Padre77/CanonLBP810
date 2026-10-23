// SPDX-License-Identifier: GPL-2.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Lbp810;

internal static class WindowsService
{
    public const string Name = "Lbp810NativeBridge";
    private static readonly ServiceMain MainCallback = OnServiceMain;
    private static readonly ServiceHandler HandlerCallback = Handle;
    private static readonly CancellationTokenSource Stop = new();
    private static nint statusHandle;
    private static uint state = 2;
    private static string logs = "";

    public static void Run(string logDirectory)
    {
        logs = logDirectory;
        var table = new[] { new ServiceEntry { Name = Name, Main = MainCallback }, new ServiceEntry() };
        if (!StartServiceCtrlDispatcher(table)) throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(MainCallback);
        GC.KeepAlive(HandlerCallback);
    }

    private static void OnServiceMain(uint argc, nint argv)
    {
        statusHandle = RegisterServiceCtrlHandler(Name, HandlerCallback);
        if (statusHandle == 0) return;
        Report(2, 0);
        uint error = 0;
        try
        {
            Directory.CreateDirectory(logs);
            using var output = new StreamWriter(Path.Combine(logs, "service.log"), append: true) { AutoFlush = true };
            var writer = TextWriter.Synchronized(output);
            Console.SetOut(writer);
            Console.SetError(writer);
            writer.WriteLine($"Service started {DateTimeOffset.Now:O}");
            // Serve binds before its first await. Do not report RUNNING if binding failed.
            Task serve = SpoolBridge.Serve("auto", Path.Combine(logs, "jobs"), false, Stop.Token);
            if (serve.IsFaulted) serve.GetAwaiter().GetResult();
            Report(4, 0);
            try { serve.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
            writer.WriteLine($"Service stopped {DateTimeOffset.Now:O}");
        }
        catch (Exception e)
        {
            error = 1064;
            try { File.AppendAllText(Path.Combine(logs, "service-error.log"), $"{DateTimeOffset.Now:O} {e}\n"); } catch { }
        }
        finally { Report(1, error); }
    }

    private static void Handle(uint control)
    {
        if (control is 1 or 5)
        {
            Report(3, 0);
            Stop.Cancel();
        }
        else if (control == 4) Report(state, 0);
    }

    private static void Report(uint next, uint error)
    {
        state = next;
        var status = new ServiceStatus { Type = 0x10, State = next,
            Accepted = next == 4 ? 5u : 0u, ExitCode = error,
            CheckPoint = next is 2 or 3 ? 1u : 0u, WaitHint = next is 2 or 3 ? 10000u : 0u };
        SetServiceStatus(statusHandle, ref status);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMain(uint count, nint args);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceHandler(uint control);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceEntry { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public ServiceMain? Main; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, State, Accepted, ExitCode, SpecificExitCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartServiceCtrlDispatcher([In] ServiceEntry[] table);
    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint RegisterServiceCtrlHandler(string name, ServiceHandler handler);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceStatus(nint handle, ref ServiceStatus status);
}
