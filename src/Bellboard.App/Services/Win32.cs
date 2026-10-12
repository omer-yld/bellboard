using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Bellboard.Services;

/// <summary>Window chrome, taskbar flashing, capture, and process/port inspection.</summary>
internal static class Win32
{
    // ---- Key and mouse-button state, whichever window has the focus (a terminal's web view usually does) ----
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    public const int VkLButton = 0x01, VkRButton = 0x02, VkEscape = 0x1B;
    public static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    // ---- DWM: dark title bar that blends into the sidebar ----
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void StyleTitleBar(IntPtr hwnd, uint captionBgr, uint textBgr)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);           // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = unchecked((int)captionBgr);
        DwmSetWindowAttribute(hwnd, 35, ref caption, 4);      // DWMWA_CAPTION_COLOR
        int text = unchecked((int)textBgr);
        DwmSetWindowAttribute(hwnd, 36, ref text, 4);         // DWMWA_TEXT_COLOR
    }

    /// <summary>The 1 px window border Windows 11 draws (0x00BBGGRR).</summary>
    public static void SetBorderColor(IntPtr hwnd, uint borderBgr)
    {
        int color = unchecked((int)borderBgr);
        DwmSetWindowAttribute(hwnd, 34, ref color, 4);        // DWMWA_BORDER_COLOR
    }

    // ---- Taskbar flash ----
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    public static void FlashTaskbar(IntPtr hwnd)
    {
        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = 0x2 | 0xC, // FLASHW_TRAY | FLASHW_TIMERNOFG
            uCount = 3,
        };
        FlashWindowEx(ref fi);
    }

    // ---- Window capture (includes WebView2/DirectComposition content) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    public static byte[] CaptureWindowPng(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try { PrintWindow(hwnd, hdc, 2); } // PW_RENDERFULLCONTENT
            finally { g.ReleaseHdc(hdc); }
        }
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    public static byte[] CaptureScreenAreaPng(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    // ---- Process tree ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    public sealed record ProcInfo(int Pid, int ParentPid, string Exe);

    public static List<ProcInfo> Processes()
    {
        var list = new List<ProcInfo>();
        var snap = CreateToolhelp32Snapshot(0x2, 0); // TH32CS_SNAPPROCESS
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return list;
            do list.Add(new ProcInfo((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile));
            while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returned);

    /// <summary>Another process's command line (ProcessCommandLineInformation, Windows 8.1+), or null.</summary>
    public static string? CommandLine(int pid)
    {
        const uint QueryLimitedInformation = 0x1000;
        const int ProcessCommandLineInformation = 60;
        var h = OpenProcess(QueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        var buf = IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out var size);
            if (size <= 0 || size > 1 << 20) return null;
            buf = Marshal.AllocHGlobal(size);
            if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out _) != 0) return null;
            // UNICODE_STRING { ushort Length; ushort MaximumLength; PWSTR Buffer }
            var length = (ushort)Marshal.ReadInt16(buf);
            var text = Marshal.ReadIntPtr(buf, IntPtr.Size);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(h);
        }
    }

    // ---- Listening TCP ports by owning PID ----
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    public static Dictionary<int, HashSet<int>> ListeningPortsByPid()
    {
        var result = new Dictionary<int, HashSet<int>>();
        Read(2, 24, 20, 8);   // AF_INET:  MIB_TCPROW_OWNER_PID  (port @8, pid @20)
        Read(23, 56, 52, 20); // AF_INET6: MIB_TCP6ROW_OWNER_PID (port @20, pid @52)
        return result;

        void Read(int af, int rowSize, int pidOffset, int portOffset)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
            if (size <= 0) return;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, af, 3, 0) != 0) return;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    var row = buf + 4 + i * rowSize;
                    int rawPort = Marshal.ReadInt32(row + portOffset);
                    int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                    int pid = Marshal.ReadInt32(row + pidOffset);
                    if (!result.TryGetValue(pid, out var set)) result[pid] = set = new HashSet<int>();
                    set.Add(port);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
