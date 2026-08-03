using System.Drawing;
using System.Runtime.InteropServices;

namespace Sts2.SeedFinder.Shell;

/// <summary>
/// Recolours the window's title bar to match the page below it.
///
/// Windows 11 lets an app choose its own caption colours, so the title bar can be the app's own
/// header rather than a system-grey strip above it. The alternative approach - a borderless
/// window with a title bar drawn in HTML - looks the same on a good day and then has to
/// reimplement dragging, double-click to maximise, the snap layouts flyout on the maximise
/// button, and the resize borders. This keeps all of that, because the title bar is still the
/// real one.
///
/// Everything here is best-effort. The attributes arrived in Windows 11 (build 22000), so on
/// Windows 10 the calls fail, are ignored, and the window keeps the default title bar - which is
/// the correct outcome, not a degraded one.
/// </summary>
internal static class TitleBar
{
    // Documented DWMWINDOWATTRIBUTE values. Named rather than inlined because a wrong number here
    // silently sets a different attribute instead of failing.
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// A 16x16 of nothing, kept alive for as long as the window uses it.
    /// </summary>
    /// <remarks>
    /// Static, and never disposed, on purpose: the window holds this handle for its whole life,
    /// and letting the Icon be collected would free the handle out from under the caption.
    /// </remarks>
    private static readonly Icon Blank = CreateBlank();

    private static Icon CreateBlank()
    {
        using var bitmap = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>
    /// Take the icon out of the title bar while leaving it on the taskbar.
    /// </summary>
    /// <remarks>
    /// A window has two icons, and they are read by different things: ICON_SMALL draws the
    /// caption, ICON_BIG draws the taskbar button. So the caption can be emptied on its own by
    /// giving it a transparent image and leaving ICON_BIG alone.
    ///
    /// Two approaches that look right and are not, recorded so they are not retried:
    ///
    /// - Form.ShowIcon = false clears BOTH icons plus ICON_SMALL2, so the taskbar loses its icon
    ///   too. The jump list keeps showing the correct one, because that comes from the exe's
    ///   Win32 resource, which makes the symptom read as a caching bug rather than a deliberate
    ///   clear.
    /// - WS_EX_DLGMODALFRAME with SWP_FRAMECHANGED is the widely cited Win32 answer and had no
    ///   effect at all on the Windows 11 caption.
    ///
    /// Windows still reserves the caption slot, so nothing to the right of it shifts. That is
    /// wanted here: the goal is an empty title bar, not a reflowed one.
    /// </remarks>
    public static void ClearCaptionIcon(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        try
        {
            SendMessage(handle, WmSetIcon, IconSmall, Blank.Handle);
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Apply the app's colours to <paramref name="handle"/>'s title bar.
    /// </summary>
    public static void Apply(IntPtr handle, Color caption, Color text, Color border)
    {
        if (handle == IntPtr.Zero) return;

        // Dark mode first. Caption colour covers the bar itself, but the system still draws the
        // window buttons, and in light mode it draws them dark - black glyphs on a near-black
        // bar, so close and minimise become invisible.
        Set(handle, UseImmersiveDarkMode, 1);

        Set(handle, CaptionColor, ToColorRef(caption));
        Set(handle, TextColor, ToColorRef(text));
        Set(handle, BorderColor, ToColorRef(border));
    }

    private static void Set(IntPtr handle, int attribute, int value)
    {
        try
        {
            // The HRESULT is deliberately ignored. Every failure here means "this build of
            // Windows does not support this attribute", and the response to that is to keep the
            // system title bar, which is what already happens.
            _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // No dwmapi.dll at all. Nothing to do and nothing worth saying.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Pack a colour the way GDI wants it: 0x00BBGGRR, with red and blue the other way round
    /// from every hex colour in the stylesheet this is matching.
    /// </summary>
    /// <remarks>
    /// Getting this wrong does not throw or look broken, it just produces a different colour -
    /// #d4a24c would arrive as #4ca2d4, gold rendered as blue. Worth stating once here rather
    /// than rediscovering at the title bar.
    /// </remarks>
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
