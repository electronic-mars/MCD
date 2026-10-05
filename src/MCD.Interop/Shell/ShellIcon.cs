using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mcd.Interop.Shell;

/// <param name="Bgra">Blue, green, red, alpha per pixel, top row first, alpha premultiplied.</param>
public sealed record IconPixels(int Width, int Height, byte[] Bgra);

/// <summary>
/// The picture Explorer shows a file with.
/// </summary>
/// <remarks>
/// Asked of the shell rather than pulled out of the executable, so that a
/// shortcut gives the icon it was told to use, a folder gives a folder, and a
/// document gives whatever its program registered.
/// </remarks>
public static class ShellIcon
{
    private static readonly BlockingCollection<Action> Work = [];
    private static readonly Thread Worker = Start();

    /// <summary>The icon for a path, or null if the shell has nothing to give.</summary>
    /// <remarks>
    /// <para>
    /// Blocks the calling thread, and must not be called from the one drawing
    /// the window: for a target on a drive that is no longer connected the shell
    /// takes seconds to say so.
    /// </para>
    /// <para>
    /// The work itself happens on one thread of this class's own, because asking
    /// the shell for an icon means asking COM, and a thread with no apartment
    /// gets an answer of "no icon" rather than an error. On the thread pool that
    /// depends on whether something else happened to have initialised that
    /// particular thread, so the same program would show its icon on one run and
    /// a bare letter on the next.
    /// </para>
    /// <para>
    /// Nothing here throws for a path that has gone away. A launcher entry
    /// pointing at an uninstalled program should draw a placeholder, not stop
    /// the dock from being built.
    /// </para>
    /// </remarks>
    public static IconPixels? For(string path)
    {
        if (Thread.CurrentThread == Worker)
        {
            return Extract(path);
        }

        IconPixels? found = null;
        using var done = new ManualResetEventSlim();

        Work.Add(() =>
        {
            try
            {
                found = Extract(path);
            }
            catch (Exception)
            {
                // Whatever a shell extension for some file type decides to
                // throw. One unhappy icon must not take the thread that fetches
                // all of them, and with it every icon after this one.
                found = null;
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        return found;
    }

    private static Thread Start()
    {
        var thread = new Thread(Run)
        {
            Name = "mcd-shell-icons",
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static void Run()
    {
        // The apartment is set up here rather than left to the runtime: nothing
        // on this thread goes through the runtime's own COM interop, so it never
        // gets around to it.
        PInvoke.CoInitializeEx(COINIT.COINIT_APARTMENTTHREADED);

        try
        {
            foreach (Action job in Work.GetConsumingEnumerable())
            {
                job();
            }
        }
        finally
        {
            PInvoke.CoUninitialize();
        }
    }

    /// <summary>The Settings app, under the name the shell lists it by.</summary>
    private const string SettingsApp =
        @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    /// <summary>
    /// The icon of something that is a place in the shell rather than a file:
    /// "shell:Downloads", the Recycle Bin, a store program, the Settings app.
    /// </summary>
    /// <remarks>
    /// These have no path for the file system to look at, so asking for the
    /// icon of the text came back empty and the bar drew a letter. The shell
    /// can parse the name into its own address of the thing, and the icon is
    /// asked for by that.
    /// </remarks>
    private static unsafe IconPixels? ExtractNamed(string name)
    {
        if (name.Equals("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            name = SettingsApp;
        }

        if (PInvoke.SHParseDisplayName(name, null, out ITEMIDLIST* item, 0, out _).Failed)
        {
            return null;
        }

        try
        {
            var info = default(SHFILEINFOW);

            nuint ok = PInvoke.SHGetFileInfo(
                (char*)item,
                0,
                &info,
                (uint)sizeof(SHFILEINFOW),
                SHGFI_FLAGS.SHGFI_PIDL | SHGFI_FLAGS.SHGFI_ICON | SHGFI_FLAGS.SHGFI_LARGEICON);

            if (ok == 0 || info.hIcon.IsNull)
            {
                return null;
            }

            try
            {
                return Read(info.hIcon);
            }
            finally
            {
                PInvoke.DestroyIcon(info.hIcon);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)item);
        }
    }

    private static unsafe IconPixels? Extract(string path)
    {
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            || path.Equals("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractNamed(path);
        }

        var info = default(SHFILEINFOW);
        nuint ok;

        // USEFILEATTRIBUTES is deliberately not passed: the point is the icon
        // this particular file has, including a shortcut's own.
        fixed (char* text = path)
        {
            ok = PInvoke.SHGetFileInfo(
                text,
                0,
                &info,
                (uint)sizeof(SHFILEINFOW),
                SHGFI_FLAGS.SHGFI_ICON | SHGFI_FLAGS.SHGFI_LARGEICON);
        }

        if (ok == 0 || info.hIcon.IsNull)
        {
            return null;
        }

        try
        {
            return Read(info.hIcon);
        }
        finally
        {
            PInvoke.DestroyIcon(info.hIcon);
        }
    }

    /// <summary>Copies an icon's colour bitmap out into ordinary bytes.</summary>
    private static unsafe IconPixels? Read(HICON icon)
    {
        ICONINFO parts = default;

        if (!PInvoke.GetIconInfo(icon, &parts) || parts.hbmColor.IsNull)
        {
            // A one-bit icon, with its shape in the mask rather than in colour.
            // Nothing has produced one of those for twenty years, and drawing
            // the mask as if it were colour would be worse than nothing.
            Release(parts);
            return null;
        }

        try
        {
            return Copy(parts.hbmColor, parts.hbmMask);
        }
        finally
        {
            Release(parts);
        }
    }

    private static unsafe IconPixels? Copy(HBITMAP colour, HBITMAP mask)
    {
        HDC screen = PInvoke.GetDC(HWND.Null);

        if (screen.IsNull)
        {
            return null;
        }

        try
        {
            if (Measure(screen, colour) is not { } size)
            {
                return null;
            }

            (int width, int height) = size;
            byte[] pixels = new byte[width * height * 4];

            if (!Pixels(screen, colour, width, height, pixels))
            {
                return null;
            }

            // A 32-bit icon whose alpha channel is empty is an older icon whose
            // shape lives in the mask. Without this, such an icon comes out
            // entirely transparent - which looks exactly like a missing icon.
            if (NoAlpha(pixels))
            {
                if (mask.IsNull)
                {
                    return null;
                }

                ApplyMask(screen, mask, width, height, pixels);
            }
            else
            {
                Premultiply(pixels);
            }

            return new IconPixels(width, height, pixels);
        }
        finally
        {
            PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }

    /// <summary>The bitmap's size, from a GetDIBits call that asks for nothing else.</summary>
    /// <remarks>
    /// The bit count has to be zero here. That is what tells GetDIBits to
    /// describe the bitmap rather than convert it; left at 32 it quietly returns
    /// success having filled in nothing, and the icon comes out zero by zero.
    /// </remarks>
    private static unsafe (int Width, int Height)? Measure(HDC dc, HBITMAP bitmap)
    {
        BITMAPINFO header = Header(0, 0, bits: 0);

        if (PInvoke.GetDIBits(dc, bitmap, 0, 0, null, &header, DIB_USAGE.DIB_RGB_COLORS) == 0)
        {
            return null;
        }

        int width = header.bmiHeader.biWidth;
        int height = Math.Abs(header.bmiHeader.biHeight);

        return width > 0 && height > 0 && (long)width * height <= 1024 * 1024
            ? (width, height)
            : null;
    }

    private static unsafe bool Pixels(HDC dc, HBITMAP bitmap, int width, int height, byte[] into)
    {
        BITMAPINFO header = Header(width, -height, bits: 32);

        fixed (byte* target = into)
        {
            return PInvoke.GetDIBits(dc, bitmap, 0, (uint)height, target, &header, DIB_USAGE.DIB_RGB_COLORS) != 0;
        }
    }

    /// <summary>Takes the shape from the mask, where a set bit means transparent.</summary>
    private static unsafe void ApplyMask(HDC dc, HBITMAP mask, int width, int height, byte[] pixels)
    {
        byte[] shape = new byte[width * height * 4];

        if (!Pixels(dc, mask, width, height, shape))
        {
            return;
        }

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 3] = shape[i] == 0 ? (byte)255 : (byte)0;

            if (pixels[i + 3] == 0)
            {
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
            }
        }
    }

    /// <summary>
    /// Scales each colour by its own alpha.
    /// </summary>
    /// <remarks>
    /// An icon's alpha is straight; every drawing surface in WinUI wants it
    /// premultiplied. Skipping this leaves a pale halo around every icon that
    /// has a soft edge.
    /// </remarks>
    private static void Premultiply(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte alpha = pixels[i + 3];

            if (alpha == 255)
            {
                continue;
            }

            pixels[i] = (byte)(pixels[i] * alpha / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
        }
    }

    private static bool NoAlpha(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A negative height asks for the rows in top-down order.</summary>
    private static BITMAPINFO Header(int width, int height, ushort bits) => new()
    {
        bmiHeader = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = height,
            biPlanes = 1,
            biBitCount = bits,
            biCompression = 0,
        },
    };

    private static void Release(ICONINFO parts)
    {
        if (!parts.hbmColor.IsNull)
        {
            PInvoke.DeleteObject(parts.hbmColor);
        }

        if (!parts.hbmMask.IsNull)
        {
            PInvoke.DeleteObject(parts.hbmMask);
        }
    }
}
