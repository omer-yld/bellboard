using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Bellboard.Services;

namespace Bellboard.Terminal;

/// <summary>
/// One shell process attached to a Windows pseudoconsole (ConPTY).
/// Prefers the redistributable conpty.dll/OpenConsole.exe shipped next to the app
/// and falls back to the inbox kernel32 implementation.
/// </summary>
public sealed class PtySession : IDisposable
{
    public int ProcessId { get; }
    public string Backend { get; }

    /// <summary>Raised on the reader thread with UTF-8 decoded output.</summary>
    public event Action<string>? Output;
    /// <summary>Raised on a worker thread once the process has exited and output is drained.</summary>
    public event Action<int>? Exited;

    private IntPtr _hpc;
    private readonly bool _bundled;
    private readonly IntPtr _hProcess;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Thread _reader;
    private readonly BlockingCollection<byte[]> _writes = new();
    private readonly object _closeLock = new();
    private int _disposed;

    private PtySession(IntPtr hpc, bool bundled, Native.PROCESS_INFORMATION pi, SafeFileHandle input, SafeFileHandle output)
    {
        _hpc = hpc;
        _bundled = bundled;
        _hProcess = pi.hProcess;
        ProcessId = pi.dwProcessId;
        Backend = bundled ? "conpty.dll (OpenConsole)" : "kernel32 (inbox conhost)";
        _input = new FileStream(input, FileAccess.Write, 1, false);
        _output = new FileStream(output, FileAccess.Read, 1, false);

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"pty-read-{ProcessId}" };
        _reader.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = $"pty-write-{ProcessId}" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = $"pty-wait-{ProcessId}" }.Start();
    }

    public static PtySession Start(string commandLine, string? cwd, IDictionary<string, string> env, short cols, short rows, bool preferBundled)
    {
        if (!Native.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!Native.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        var size = new Native.COORD { X = Math.Max((short)2, cols), Y = Math.Max((short)1, rows) };
        IntPtr hpc = IntPtr.Zero;
        int hr = -1;
        bool bundled = false;
        if (preferBundled && BundledAvailable)
        {
            try
            {
                hr = Native.ConptyCreatePseudoConsole(size, inRead, outWrite, 0, out hpc);
                bundled = hr == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                Log.Error("bundled conpty unavailable, using inbox ConPTY", ex);
            }
        }
        if (!bundled)
            hr = Native.CreatePseudoConsole(size, inRead, outWrite, 0, out hpc);
        if (hr != 0) throw Marshal.GetExceptionForHR(hr) ?? new Win32Exception(hr);

        // The pseudoconsole duplicated these; our copies would keep the pipes alive after exit.
        inRead.Dispose();
        outWrite.Dispose();

        IntPtr attrSize = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        IntPtr attrList = Marshal.AllocHGlobal(attrSize);
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize)) throw new Win32Exception();
            if (!Native.UpdateProcThreadAttribute(attrList, 0, Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new Native.STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            // Null std handles + USESTDHANDLES stop the child from inheriting *our* std handles
            // (which are real pipes when Bellboard is launched from another console or WSL).
            si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            si.lpAttributeList = attrList;

            envBlock = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(env));
            var cmd = new StringBuilder(commandLine, 32768);
            if (!Native.CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT,
                    envBlock, cwd, ref si, out var pi))
            {
                var err = new Win32Exception();
                ClosePc(hpc, bundled);
                inWrite.Dispose();
                outRead.Dispose();
                throw new InvalidOperationException($"{err.Message} ({commandLine})", err);
            }
            Native.CloseHandle(pi.hThread);
            // Kill-on-close job: if Bellboard dies, the shell and its console host go with it.
            ProcessJob.Add(pi.hProcess);
            if (bundled) ProcessJob.AddNewChildren("OpenConsole.exe");
            return new PtySession(hpc, bundled, pi, inWrite, outRead);
        }
        finally
        {
            Native.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
        }
    }

    public static bool BundledAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "conpty.dll")) &&
        (File.Exists(Path.Combine(AppContext.BaseDirectory, "x64", "OpenConsole.exe")) ||
         File.Exists(Path.Combine(AppContext.BaseDirectory, "OpenConsole.exe")));

    private static string BuildEnvironmentBlock(IDictionary<string, string> env)
    {
        // Windows expects "K=V\0...\0\0", sorted case-insensitively.
        var sb = new StringBuilder();
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (kv.Key.Length == 0 || kv.Key.Contains('=') && !kv.Key.StartsWith('=')) continue;
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        sb.Append('\0');
        return sb.ToString();
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void WriteBinary(string latin1) => Write(Encoding.Latin1.GetBytes(latin1));

    public void Write(byte[] bytes)
    {
        if (_disposed == 0 && bytes.Length > 0)
        {
            try { _writes.Add(bytes); } catch (InvalidOperationException) { }
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_closeLock)
        {
            if (_hpc == IntPtr.Zero) return;
            var size = new Native.COORD { X = (short)Math.Clamp(cols, 2, 32000), Y = (short)Math.Clamp(rows, 1, 32000) };
            if (_bundled) Native.ConptyResizePseudoConsole(_hpc, size);
            else Native.ResizePseudoConsole(_hpc, size);
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var chunk in _writes.GetConsumingEnumerable())
            {
                _input.Write(chunk, 0, chunk.Length);
                _input.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void ReadLoop()
    {
        var buf = new byte[64 * 1024];
        var chars = new char[64 * 1024 + 8];
        var decoder = new UTF8Encoding(false).GetDecoder();
        try
        {
            int n;
            while ((n = _output.Read(buf, 0, buf.Length)) > 0)
            {
                int c = decoder.GetChars(buf, 0, n, chars, 0, false);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        catch (Exception ex) { Log.Error("pty read loop", ex); }
    }

    private void WaitLoop()
    {
        Native.WaitForSingleObject(_hProcess, Native.INFINITE);
        Native.GetExitCodeProcess(_hProcess, out var code);
        // Closing the pseudoconsole flushes remaining output and ends the reader with EOF.
        ClosePseudoConsole();
        _reader.Join(TimeSpan.FromSeconds(2));
        if (_disposed == 0) Exited?.Invoke(unchecked((int)code));
    }

    private void ClosePseudoConsole()
    {
        IntPtr hpc;
        lock (_closeLock)
        {
            hpc = _hpc;
            _hpc = IntPtr.Zero;
        }
        if (hpc != IntPtr.Zero) ClosePc(hpc, _bundled);
    }

    private static void ClosePc(IntPtr hpc, bool bundled)
    {
        if (bundled) Native.ConptyClosePseudoConsole(hpc);
        else Native.ClosePseudoConsole(hpc);
    }

    public void Dispose() => _ = CloseAsync();

    private Task? _closing;

    /// <summary>
    /// Close like Windows Terminal: closing the pseudoconsole sends CTRL_CLOSE to the console
    /// programs attached to it (shell, dev servers…); GUI apps started from the shell keep running.
    /// A shell that ignores the close is terminated (just that process).
    /// </summary>
    public Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return _closing ?? Task.CompletedTask;
        _writes.CompleteAdding();
        return _closing = Task.Run(() =>
        {
            ClosePseudoConsole();
            if (Native.WaitForSingleObject(_hProcess, 2000) != 0) Native.TerminateProcess(_hProcess, 1);
            try { _input.Dispose(); } catch { }
            try { _output.Dispose(); } catch { }
            Native.CloseHandle(_hProcess);
        });
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const int STARTF_USESTDHANDLES = 0x00000100;
    public const uint INFINITE = 0xFFFFFFFF;
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);
    [DllImport("kernel32.dll")]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
    [DllImport("kernel32.dll")]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("conpty.dll")]
    public static extern int ConptyCreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);
    [DllImport("conpty.dll")]
    public static extern int ConptyResizePseudoConsole(IntPtr hPC, COORD size);
    [DllImport("conpty.dll")]
    public static extern void ConptyClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);
}

/// <summary>
/// A job object that kills its processes when Bellboard exits or crashes (KILL_ON_JOB_CLOSE).
/// Only the shells and their console hosts are added; SILENT_BREAKAWAY lets anything they start
/// (an editor, a browser) live on outside the job.
/// </summary>
internal static class ProcessJob
{
    private static readonly IntPtr Job = Create();
    private static readonly System.Collections.Generic.HashSet<int> Added = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct BASIC_LIMIT
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXTENDED_LIMIT
    {
        public BASIC_LIMIT Basic;
        public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref EXTENDED_LIMIT info, int length);
    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static IntPtr Create()
    {
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            var info = new EXTENDED_LIMIT();
            info.Basic.LimitFlags = 0x2000 | 0x1000; // KILL_ON_JOB_CLOSE | SILENT_BREAKAWAY_OK
            SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref info, Marshal.SizeOf<EXTENDED_LIMIT>());
            return job;
        }
        catch (Exception ex)
        {
            Log.Error("creating the process job failed", ex);
            return IntPtr.Zero;
        }
    }

    public static void Add(IntPtr process)
    {
        if (Job != IntPtr.Zero) AssignProcessToJobObject(Job, process);
    }

    /// <summary>Adds our direct children with this exe name that aren't in the job yet (conpty's OpenConsole).</summary>
    public static void AddNewChildren(string exeName)
    {
        if (Job == IntPtr.Zero) return;
        try
        {
            var me = Environment.ProcessId;
            foreach (var p in Bellboard.Services.Win32.Processes())
            {
                if (p.ParentPid != me || !p.Exe.Equals(exeName, StringComparison.OrdinalIgnoreCase)) continue;
                lock (Added) { if (!Added.Add(p.Pid)) continue; }
                var h = OpenProcess(0x0100 | 0x0001, false, p.Pid); // PROCESS_SET_QUOTA | PROCESS_TERMINATE
                if (h == IntPtr.Zero) continue;
                AssignProcessToJobObject(Job, h);
                CloseHandle(h);
            }
        }
        catch (Exception ex)
        {
            Log.Error("adding console hosts to the job", ex);
        }
    }
}
