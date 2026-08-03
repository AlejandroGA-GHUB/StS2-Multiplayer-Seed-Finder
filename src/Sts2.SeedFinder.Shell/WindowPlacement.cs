using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace Sts2.SeedFinder.Shell;

/// <summary>
/// Where the window was last time, remembered between runs.
///
/// Stored next to the WebView2 profile in LocalApplicationData rather than in the registry or
/// beside the exe: it is per-user, disposable, and losing it costs nothing more than a
/// default-sized window. Nothing here is on the search path, so a failure to read or write it is
/// never worth interrupting anyone over.
/// </summary>
internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized, double Zoom)
{
    /// <summary>
    /// The stored zoom, or 1.0 when there isn't a sensible one.
    /// </summary>
    /// <remarks>
    /// Guards two cases. A file written before zoom was remembered has no Zoom member at all, and
    /// deserialises to 0 - which as a zoom factor means an invisible page. And WebView2 rejects
    /// anything outside roughly a quarter to five times, so a corrupted value would throw at the
    /// point it was applied rather than where it was read.
    /// </remarks>
    [JsonIgnore]
    public double UsableZoom => Zoom is >= 0.25 and <= 5.0 ? Zoom : 1.0;

    /// <summary>
    /// How much of the window has to land on a monitor for the position to be worth restoring.
    /// Enough to grab and drag: some caption and a corner.
    /// </summary>
    private static readonly Size MinimumVisible = new(220, 90);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sts2SeedFinder", "window.json");

    public static WindowPlacement? Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path));
        }
        catch
        {
            // Missing, unreadable, or written by a version that shaped it differently. A default
            // window is a perfectly good answer to all three.
            return null;
        }
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Read-only profile, a full disk, a roaming policy. None of these are worth a dialog
            // during shutdown, and the only consequence is a default-sized window next time.
        }
    }

    /// <summary>
    /// Read the placement to restore later, taking the restore bounds when maximised.
    /// </summary>
    /// <remarks>
    /// <c>Bounds</c> on a maximised window is the maximised rectangle, so saving that and
    /// restoring it produces a window that fills the screen but is not actually maximised - it
    /// cannot be un-maximised, because there is nothing to return to. <c>RestoreBounds</c> is the
    /// size it would return to, which is the pair of facts worth keeping: how big, and whether it
    /// was maximised.
    ///
    /// Minimised is deliberately not remembered. An app that starts minimised looks like an app
    /// that failed to start.
    /// </remarks>
    public static WindowPlacement From(Form form, double zoom)
    {
        bool maximized = form.WindowState == FormWindowState.Maximized;
        var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;

        return new WindowPlacement(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized, zoom);
    }

    /// <summary>
    /// Put the window back, unless where it was no longer exists.
    /// </summary>
    /// <remarks>
    /// Monitors get unplugged, laptops get undocked, and resolutions change. A saved position
    /// that was perfectly sensible on a second screen becomes coordinates in the void, and a
    /// window restored there is invisible with no way to reach it - the app looks like it did not
    /// launch at all. So the saved rectangle is only honoured if enough of it still falls on a
    /// monitor; otherwise the window centres itself and only the SIZE is kept, which is the part
    /// that is still meaningful.
    /// </remarks>
    public void ApplyTo(Form form)
    {
        // Never smaller than the window can usefully be, whatever the file says.
        var size = new Size(
            Math.Max(Width, form.MinimumSize.Width),
            Math.Max(Height, form.MinimumSize.Height));

        var bounds = new Rectangle(new Point(X, Y), size);

        if (IsReachable(bounds))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = bounds;
        }
        else
        {
            form.StartPosition = FormStartPosition.CenterScreen;
            form.Size = size;
        }

        // Applied last: setting Bounds on a maximised form is ignored, so the restore size has to
        // be established first and the state put back on top of it.
        if (Maximized) form.WindowState = FormWindowState.Maximized;
    }

    private static bool IsReachable(Rectangle bounds) =>
        Screen.AllScreens.Any(screen =>
        {
            var visible = Rectangle.Intersect(screen.WorkingArea, bounds);
            return visible.Width >= MinimumVisible.Width && visible.Height >= MinimumVisible.Height;
        });
}
