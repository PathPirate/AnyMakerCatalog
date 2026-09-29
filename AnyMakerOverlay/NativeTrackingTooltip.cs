using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AnyMakerOverlay;

// A Windows common-controls tracking tooltip. Unlike WinForms ToolTip.Show,
// TTM_TRACKACTIVATE does not require the overlay to be the foreground window.
internal sealed class NativeTrackingTooltip : IDisposable
{
    private const uint WmUser = 0x0400;
    private const uint TtmAddToolW = WmUser + 50;
    private const uint TtmUpdateTipTextW = WmUser + 57;
    private const uint TtmTrackActivate = WmUser + 17;
    private const uint TtmTrackPosition = WmUser + 18;
    private const uint TtmSetTipBackColor = WmUser + 19;
    private const uint TtmSetTipTextColor = WmUser + 20;
    private const uint TtmSetMaxTipWidth = WmUser + 24;
    private const uint TtmSetMargin = WmUser + 26;
    private readonly IntPtr window;
    private readonly IntPtr fontHandle;
    private ToolInfo tool;
    private IntPtr textPointer;
    private bool disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ToolInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr Owner;
        public UIntPtr Id;
        public NativeRect Rect;
        public IntPtr Instance;
        public IntPtr Text;
        public IntPtr Param;
        public IntPtr Reserved;
    }

    [DllImport("comctl32.dll")]
    private static extern void InitCommonControls();

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string? title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr first, IntPtr second);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr first, ref ToolInfo second);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? appName, string? idList);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    internal NativeTrackingTooltip(IntPtr owner, Font font)
    {
        InitCommonControls();
        window = CreateWindowEx(0x080000a8, "tooltips_class32", null, 0x80000003,
            0, 0, 0, 0, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create description tooltip");

        SetWindowTheme(window, "", "");
        SetWindowLong(window, -16, GetWindowLong(window, -16) & ~0x00800000); // Remove WS_BORDER.
        SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        fontHandle = font.ToHfont();
        SendMessage(window, 0x0030, fontHandle, new IntPtr(1)); // WM_SETFONT
        SendMessage(window, TtmSetMaxTipWidth, IntPtr.Zero, new IntPtr(340));
        SendMessage(window, TtmSetTipBackColor, new IntPtr(0x363636), IntPtr.Zero);
        SendMessage(window, TtmSetTipTextColor, new IntPtr(0xFFFFFF), IntPtr.Zero);
        var margin = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
        try
        {
            Marshal.StructureToPtr(new NativeRect { Left = 12, Top = 10, Right = 12, Bottom = 10 }, margin, false);
            SendMessage(window, TtmSetMargin, IntPtr.Zero, margin);
        }
        finally { Marshal.FreeHGlobal(margin); }

        textPointer = Marshal.StringToHGlobalUni(" ");
        tool = new ToolInfo
        {
            // Version 2 is accepted by both the legacy and themed common controls.
            // The final lpReserved pointer belongs to version 3.
            Size = (uint)(Marshal.SizeOf<ToolInfo>() - IntPtr.Size),
            Flags = 0x20 | 0x80 | 0x100, // TTF_TRACK | TTF_ABSOLUTE | TTF_TRANSPARENT
            Owner = owner,
            Id = (UIntPtr)1,
            Text = textPointer
        };
        if (SendMessage(window, TtmAddToolW, IntPtr.Zero, ref tool) == IntPtr.Zero)
        {
            Dispose();
            throw new Win32Exception("Could not register description tooltip");
        }
    }

    internal bool Show(string text, Point screenPosition)
    {
        if (disposed) return false;
        Hide();
        textPointer = Marshal.StringToHGlobalUni(text);
        tool.Text = textPointer;
        SendMessage(window, TtmUpdateTipTextW, IntPtr.Zero, ref tool);
        var packed = (screenPosition.X & 0xffff) | ((screenPosition.Y & 0xffff) << 16);
        SendMessage(window, TtmTrackPosition, IntPtr.Zero, new IntPtr(packed));
        SendMessage(window, TtmTrackActivate, new IntPtr(1), ref tool);
        SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
        if (GetWindowRect(window, out var bounds))
        {
            var region = CreateRoundRectRgn(0, 0, bounds.Right - bounds.Left + 1,
                bounds.Bottom - bounds.Top + 1, 12, 12);
            if (region != IntPtr.Zero && SetWindowRgn(window, region, true) == 0) DeleteObject(region);
        }
        return IsWindowVisible(window);
    }

    internal void Hide()
    {
        if (disposed) return;
        SendMessage(window, TtmTrackActivate, IntPtr.Zero, ref tool);
        if (textPointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(textPointer);
            textPointer = IntPtr.Zero;
            tool.Text = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        Hide();
        disposed = true;
        DestroyWindow(window);
        if (fontHandle != IntPtr.Zero) DeleteObject(fontHandle);
    }
}
