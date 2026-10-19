using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Several copies side by side: force a fixed windowed size and move each copy to its own spot in a grid
/// (copy 1 top-left, then left to right, row by row). Emberville applies its saved resolution when the options
/// load (VideoOptions -> Screen.SetResolution), some time after startup, so instead of patching that we watch the
/// window for the first seconds and put it back whenever the game changes it.
/// </summary>
internal static class WindowFix
{
    static ConfigEntry<bool> enabled;
    static ConfigEntry<int> width;
    static ConfigEntry<int> height;
    static ConfigEntry<bool> arrange;
    static ConfigEntry<int> originX;
    static ConfigEntry<int> originY;

    const float WatchSeconds = 30f;
    static float nextCheck;
    static int moveInFrames = -1;

    public static void Bind(ConfigFile config)
    {
        enabled = config.Bind("Window", "ForceWindowed", true, "Force a fixed windowed resolution on startup");
        width = config.Bind("Window", "Width", 1280, "Window width when ForceWindowed is on");
        height = config.Bind("Window", "Height", 720, "Window height when ForceWindowed is on");
        arrange = config.Bind("Window", "ArrangeSideBySide", true, "Move each copy to its own spot in a grid");
        originX = config.Bind("Window", "ArrangeLeft", 0, "Screen X of copy 1's window");
        originY = config.Bind("Window", "ArrangeTop", 0, "Screen Y of the windows");
    }

    /// <summary>Call every frame (cheap: checks twice a second, only during the first seconds).</summary>
    public static void Tick()
    {
        if (enabled == null || !enabled.Value) return;

        if (moveInFrames >= 0 && moveInFrames-- == 0) MoveWindow();   // the resize lands a few frames later

        if (Time.realtimeSinceStartup > WatchSeconds || Time.realtimeSinceStartup < nextCheck) return;
        nextCheck = Time.realtimeSinceStartup + 0.5f;
        if (Screen.width == width.Value && Screen.height == height.Value && Screen.fullScreenMode == FullScreenMode.Windowed) return;

        Screen.SetResolution(width.Value, height.Value, FullScreenMode.Windowed);
        if (arrange.Value) moveInFrames = 10;
    }

    static void MoveWindow()
    {
        var hwnd = FindOwnUnityWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;   // includes the title bar and borders
        int columns = Math.Max(1, (Screen.currentResolution.width - originX.Value) / Math.Max(1, w));
        int index = SaveIsolation.CopyNumber - 1;
        int x = originX.Value + (index % columns) * w;
        int y = originY.Value + (index / columns) * h;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>This process's Unity window (copies run side by side, so match on our PID).</summary>
    static IntPtr FindOwnUnityWindow()
    {
        uint me = (uint)Process.GetCurrentProcess().Id;
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid != me) return true;
            var sb = new StringBuilder(64);
            GetClassNameW(h, sb, sb.Capacity);
            if (sb.ToString() != "UnityWndClass") return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
