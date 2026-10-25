using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ACRSaveManager.UI;

/// <summary>
/// Keeps a WindowChrome window, which draws its own caption, off the taskbar
/// when maximised. Shared by the main window and the setup viewer.
/// </summary>
internal static partial class WorkAreaMaximize
{
    /// <summary>Hooks <paramref name="window"/>; call once its handle exists (OnSourceInitialized).</summary>
    public static void Attach(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
            source.AddHook(WindowProc);
    }

    /// <summary>
    /// Without this, a WindowChrome window maximises over the taskbar: clamp the
    /// maximised size and position to the monitor's work area. The message is
    /// left unhandled so WPF's own processing still runs after this: it fills in
    /// the minimum tracking size from MinWidth / MinHeight, and marking it handled
    /// let the window shrink to any size, its content clipped.
    /// </summary>
    private static IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;

        nint monitor = MonitorFromWindow(hwnd, 2);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.MaxPosition.X = info.WorkArea.Left - info.MonitorArea.Left;
        minMax.MaxPosition.Y = info.WorkArea.Top - info.MonitorArea.Top;
        minMax.MaxSize.X = info.WorkArea.Right - info.WorkArea.Left;
        minMax.MaxSize.Y = info.WorkArea.Bottom - info.WorkArea.Top;
        Marshal.StructureToPtr(minMax, lParam, true);
        return IntPtr.Zero;
    }

    // Source-generated P/Invoke (SYSLIB1054): the marshalling stub is written at
    // compile time instead of at startup. Both signatures are blittable — handles,
    // ints and a struct of ints — so nothing is marshalled but the BOOL return.
    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    // The entry point is spelled out: user32 exports GetMonitorInfoA and
    // GetMonitorInfoW, never a bare GetMonitorInfo. DllImport with CharSet.Auto
    // used to try the suffixes; LibraryImport takes the name as given.
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInt { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointInt Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInt { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectInt MonitorArea, WorkArea;
        public uint Flags;
    }
}
