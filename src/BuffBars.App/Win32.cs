using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BuffBars.App;

/// <summary>Click-through + topmost plumbing (EQLogParser-proven recipe).</summary>
internal static class Win32
{
    private const int GwlExstyle = -20;
    private const long WsExLayered = 0x80000;
    private const long WsExTransparent = 0x20;
    private const long WsExToolwindow = 0x80;
    private const long WsExNoactivate = 0x08000000;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNoactivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    /// <summary>Layered + transparent (all input passes to the game) + hidden from alt-tab.</summary>
    public static void MakeClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = (long)GetWindowLongPtr(hwnd, GwlExstyle);
        style |= WsExLayered | WsExTransparent | WsExToolwindow | WsExNoactivate;
        SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(style));
    }

    /// <summary>Re-assert HWND_TOPMOST (the game reorders z constantly; call on a cadence).</summary>
    public static void AssertTopmost(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNosize | SwpNomove | SwpNoactivate);
    }
}
