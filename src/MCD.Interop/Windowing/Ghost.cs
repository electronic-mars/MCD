using System.Runtime.InteropServices;

namespace Mcd.Interop.Windowing;

/// <summary>
/// A picture that follows the pointer: what is being carried, drawn over
/// everything, taking no clicks and no focus.
/// </summary>
/// <remarks>
/// A layered window of the plainest kind rather than a window of XAML. A XAML
/// window has to be activated to be shown, and activation would take the
/// pointer away from the element carrying the widget - the carrying would end
/// the moment the picture appeared.
/// </remarks>
public sealed class Ghost : IDisposable
{
    private const string ClassName = "MasterControlDock.Ghost";

    private const int ExLayered = 0x00080000;
    private const int ExTransparent = 0x00000020;
    private const int ExToolWindow = 0x00000080;
    private const int ExNoActivate = 0x08000000;
    private const int ExTopmost = 0x00000008;
    private const uint Popup = 0x80000000;

    private static bool _registered;

    private readonly nint _hwnd;
    private readonly int _width;
    private readonly int _height;
    private readonly int _hotX;
    private readonly int _hotY;
    private bool _shown;

    /// <param name="pixels">Premultiplied BGRA, top row first, as XAML renders it.</param>
    /// <param name="hotX">Where in the picture the pointer holds it, across.</param>
    /// <param name="hotY">Where in the picture the pointer holds it, down.</param>
    public Ghost(byte[] pixels, int width, int height, int hotX, int hotY)
    {
        _width = width;
        _height = height;
        _hotX = hotX;
        _hotY = hotY;

        nint module = GetModuleHandleW(null);

        if (!_registered)
        {
            var wc = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"),
                Instance = module,
                ClassName = ClassName,
            };

            RegisterClassExW(ref wc);
            _registered = true;
        }

        _hwnd = CreateWindowExW(
            ExLayered | ExTransparent | ExToolWindow | ExNoActivate | ExTopmost,
            ClassName,
            string.Empty,
            Popup,
            0,
            0,
            width,
            height,
            0,
            0,
            module,
            0);

        if (_hwnd == 0)
        {
            throw new InvalidOperationException("the picture's window was not made");
        }

        try
        {
            Paint(pixels);
        }
        catch
        {
            DestroyWindow(_hwnd);
            throw;
        }
    }

    /// <summary>Puts the picture under the pointer at this point of the desktop, and shows it.</summary>
    public void MoveTo(int x, int y)
    {
        SetWindowPos(_hwnd, -1, x - _hotX, y - _hotY, 0, 0, 0x0001 | 0x0010 | 0x0040);
        _shown = true;
    }

    /// <summary>Takes the picture off the screen until it is moved again.</summary>
    public void Hide()
    {
        if (_shown)
        {
            ShowWindow(_hwnd, 0);
            _shown = false;
        }
    }

    public void Dispose() => DestroyWindow(_hwnd);

    private void Paint(byte[] pixels)
    {
        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);

        var info = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = _width,

            // Negative: the rows run top to bottom, as the pixels do.
            Height = -_height,
            Planes = 1,
            BitCount = 32,
        };

        nint bitmap = CreateDIBSection(memory, ref info, 0, out nint bits, 0, 0);

        if (bitmap == 0)
        {
            DeleteDC(memory);
            ReleaseDC(0, screen);
            throw new InvalidOperationException("no bitmap for the picture");
        }

        Marshal.Copy(pixels, 0, bits, Math.Min(pixels.Length, _width * _height * 4));
        nint old = SelectObject(memory, bitmap);

        var size = new Size { Cx = _width, Cy = _height };
        var origin = new PointI();

        // A little see-through, so what it is carried over still shows.
        var blend = new Blend { Op = 0, Flags = 0, Alpha = 215, Format = 1 };

        UpdateLayeredWindow(_hwnd, screen, 0, ref size, memory, ref origin, 0, ref blend, 2);

        SelectObject(memory, old);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(0, screen);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointI
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blend
    {
        public byte Op;
        public byte Flags;
        public byte Alpha;
        public byte Format;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        int exStyle, string className, string title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(
        nint hwnd, nint screen, nint at, ref Size size, nint source, ref PointI from, uint key, ref Blend blend, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint thing);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint thing);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string name);
}
