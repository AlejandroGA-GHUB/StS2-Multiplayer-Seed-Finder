using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Sts2.SeedFinder.Shell;

/// <summary>
/// The application window: a title bar, a taskbar entry, and the seed finder filling the rest.
/// No address bar, no tabs, nothing that says "browser".
/// </summary>
public sealed class ShellWindow : Form
{
    // The page's own tokens, from app.css. Duplicated rather than read at runtime: they are three
    // constants that have not moved since the theme was designed, and fetching them would mean
    // the window could not paint until the server answered - which is exactly the flash of
    // default colours this is here to avoid. If the theme is ever retuned, retune these too.
    /// <summary>--bg-0, the page background. See <see cref="OnLoad"/> for why this is set twice.</summary>
    private static readonly Color PageBackground = Color.FromArgb(0x14, 0x13, 0x1A);

    /// <summary>--bg-1, what the app's own header bar sits on.</summary>
    private static readonly Color HeaderBackground = Color.FromArgb(0x1C, 0x1B, 0x24);

    /// <summary>--border.</summary>
    private static readonly Color BorderTint = Color.FromArgb(0x35, 0x32, 0x3F);

    private readonly WebServer _server;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    private readonly bool _webViewAvailable;
    private readonly string? _webViewProblem;

    /// <summary>
    /// Watches for the server dying. See <see cref="WatchServer"/> for why polling is the right
    /// shape here.
    /// </summary>
    private readonly System.Windows.Forms.Timer _serverWatch = new() { Interval = 2000 };

    /// <summary>
    /// True once the page has been replaced by a message. See <see cref="OnFormClosing"/> - the
    /// notice resizes the window, and that size must not become the remembered one.
    /// </summary>
    private bool _showingNotice;

    /// <summary>
    /// What the last session left behind, kept because it is consumed in two places at two
    /// different times: the bounds in the constructor, the zoom only once WebView2 exists.
    /// </summary>
    private readonly WindowPlacement? _restored = WindowPlacement.Load();

    /// <summary>
    /// The browser process WebView2 started for us, or 0 if it never got that far.
    ///
    /// Kept so shutdown can be verified rather than assumed. See <see cref="OnFormClosed"/>.
    /// </summary>
    public int BrowserProcessId { get; private set; }

    public ShellWindow(WebServer server, bool webViewAvailable, string? webViewProblem)
    {
        _server = server;
        _webViewAvailable = webViewAvailable;
        _webViewProblem = webViewProblem;

        Text = "StS2 Co-op Seed Finder";

        // Assigned here, hidden from the caption in OnHandleCreated. Windows fixes the caption
        // icon about 13px from the window edge while the page's own header starts at 20px, and
        // that gap cannot be closed without drawing our own title bar or moving the page's
        // gutter - so the bar shows no icon rather than a misaligned one.
        //
        // Not ShowIcon = false, which looks like the obvious way and is not: it nulls the
        // window's icons outright, and the taskbar reads those. See TitleBar.HideCaptionIcon.
        Icon = LoadAppIcon() ?? Icon;
        MinimumSize = new Size(940, 640);
        ClientSize = new Size(1440, 900);
        StartPosition = FormStartPosition.CenterScreen;

        // After the defaults above, so a saved size replaces them rather than fighting them, and
        // so MinimumSize is already set for the restore to clamp against.
        _restored?.ApplyTo(this);

        // Both, not just the form. The form's colour covers the split second before the control
        // is created; the control's own default covers the gap between creation and first paint.
        // Miss either and a dark app opens with a white flash, which reads as a web page loading.
        BackColor = PageBackground;
        _view.DefaultBackgroundColor = PageBackground;

        Controls.Add(_view);
    }

    /// <summary>
    /// The app icon, for the title bar and the taskbar.
    /// </summary>
    /// <remarks>
    /// Loaded from an embedded copy rather than relying on the exe's Win32 icon, because
    /// &lt;ApplicationIcon&gt; only reaches Explorer: WinForms ignores it and falls back to its own
    /// built-in default, and the taskbar takes whatever the window has. Setting it here is what
    /// actually puts the icon on screen.
    ///
    /// The whole multi-size file is handed over, not one image, so WinForms can pick 16px for the
    /// title bar and a larger one for the taskbar instead of scaling a single size to both.
    /// </remarks>
    private static Icon? LoadAppIcon()
    {
        try
        {
            using var stream = typeof(ShellWindow).Assembly
                .GetManifestResourceStream("Sts2.SeedFinder.Shell.appicon.ico");

            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            // A missing or malformed resource is a cosmetic problem. The caller keeps the
            // WinForms default, which is worse-looking and entirely functional.
            return null;
        }
    }

    /// <summary>
    /// Colour the title bar as soon as there is a window to colour, and again whenever Windows
    /// hands us a new one.
    /// </summary>
    /// <remarks>
    /// OnHandleCreated rather than the constructor or OnLoad: the attributes are set against an
    /// HWND, and in the constructor there is not one yet. It fires again on handle recreation,
    /// which WinForms does for several ordinary property changes, and a title bar that silently
    /// reverts to grey partway through a session would be a puzzling thing to debug later.
    /// </remarks>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Caption text in the caption's own colour, which hides it. The page already carries the
        // app's name in its header, immediately below, so the title bar was saying it twice.
        //
        // Hidden rather than removed: clearing Text would empty the title bar too, but Text is
        // also the label Windows puts on the taskbar button and in the alt-tab switcher, and
        // those are drawn by the system in its own colours where this one does not reach. So the
        // window keeps its name for everything that identifies it, and only stops repeating it
        // where the page already does.
        // To show the title again, pass the accent (#d4a24c) or --text-0 (#e8e6ef) as `text`.
        TitleBar.Apply(Handle, caption: HeaderBackground, text: HeaderBackground, border: BorderTint);
    }

    /// <summary>
    /// Empty the caption's icon slot, once WinForms has finished filling it.
    /// </summary>
    /// <remarks>
    /// OnShown rather than OnHandleCreated: WinForms pushes Form.Icon into both icon slots as
    /// part of bringing the window up, deriving its own 16px copy for the caption, and it does
    /// that AFTER the handle exists. Overriding earlier is silently undone - and undone in a way
    /// that reads back as a valid non-null handle, so it looks like it worked.
    /// </remarks>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TitleBar.ClearCaptionIcon(Handle);
    }

    /// <summary>
    /// Re-assert the empty caption icon, cheaply, whenever the window comes forward.
    /// </summary>
    /// <remarks>
    /// Defensive, and earned: the first attempt at this ran in OnHandleCreated, WinForms
    /// overwrote it, and the overwrite read back as a perfectly valid icon handle - so the only
    /// way to notice was to photograph the title bar. WinForms re-applies Form.Icon whenever it
    /// rebuilds the window handle, which several ordinary property changes trigger. One
    /// SendMessage per activation is not worth measuring, and it makes the icon staying hidden
    /// independent of knowing every case that would bring it back.
    /// </remarks>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        TitleBar.ClearCaptionIcon(Handle);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        if (!_webViewAvailable)
        {
            ShowBrowserFallback(_webViewProblem);
            return;
        }

        try
        {
            await StartWebViewAsync();
        }
        catch (Exception ex)
        {
            // The pre-flight check said a runtime exists, so reaching here means creation itself
            // failed - a locked profile directory, a half-updated runtime, a policy. The user
            // still wants to find seeds, and the server is already up.
            ShowBrowserFallback(ex.Message);
        }
    }

    private async Task StartWebViewAsync()
    {
        // The profile directory has to be somewhere writable. The default is beside the exe,
        // which works from a build directory and fails the moment the app is unzipped somewhere
        // like Program Files - and it fails at control creation, before anything is on screen.
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sts2SeedFinder", "WebView2");
        Directory.CreateDirectory(userData);

        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await _view.EnsureCoreWebView2Async(environment);

        var core = _view.CoreWebView2;

        // Kept on: this is the only route to DevTools, and iterating on 1,900 lines of app.js
        // without them would be a real regression against the browser flow.
        core.Settings.AreDevToolsEnabled = true;

        // Off, because they are browser affordances with nothing behind them here. Status text
        // shows link targets nobody can act on, and there is no password or address to autofill
        // in this app at all.
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        core.NewWindowRequested += OpenExternally;
        core.NavigationStarting += KeepNavigationLocal;

        BrowserProcessId = (int)core.BrowserProcessId;

        // Zoom is a property of the CONTROLLER, not of the profile on disk, and every launch
        // builds a new controller - so WebView2 has nothing to restore from and Ctrl+/- would
        // reset to 100% every time. Hence remembering it ourselves.
        //
        // After EnsureCoreWebView2Async, which is what brings the controller into existence.
        if (_restored is not null) _view.ZoomFactor = _restored.UsableZoom;
        _view.ZoomFactorChanged += SaveZoom;

        core.Navigate(_server.Address.ToString());

        _serverWatch.Tick += WatchServer;
        _serverWatch.Start();
    }

    /// <summary>
    /// Notice when the server behind the page has gone, and say so.
    /// </summary>
    /// <remarks>
    /// Without this the failure is silent and deeply confusing: the page carries on rendering
    /// perfectly, because it is already loaded, and only the next search fails. The user sees a
    /// working app that has stopped working, with no address bar to check and no console to read.
    ///
    /// Polling rather than the process Exited event, because Exited arrives on a thread pool
    /// thread and everything the handler wants to touch belongs to the UI thread. A Forms timer
    /// already ticks there. Two seconds is chosen against a human noticing, not against a
    /// machine: nothing depends on reacting quickly, and the cost of a check is one boolean.
    /// </remarks>
    private void WatchServer(object? sender, EventArgs e)
    {
        if (!_server.HasExited) return;

        _serverWatch.Stop();

        var detail = _server.Output;
        ShowNotice(
            "The seed finder's server has stopped, so searching will not work.\r\n\r\n"
            + "Close this window and start it again.\r\n\r\n"
            + (string.IsNullOrEmpty(detail) ? "It gave no reason." : detail));
    }

    /// <summary>
    /// Tear the control down explicitly, while there is still a message pump to do it on.
    /// </summary>
    /// <remarks>
    /// Leaving this to the form's own disposal is not enough, and the symptom is six orphaned
    /// msedgewebview2.exe per run. WebView2 shutdown is asynchronous and cooperative: the control
    /// asks the browser process to close and that takes a beat, so a process which reaches the
    /// end of Main first simply dies and leaves the whole browser process tree parentless, with
    /// no window and nothing to close it. Disposing here happens while the form is closing rather
    /// than after it is gone, which gives the request somewhere to land.
    ///
    /// It is still a request, not a guarantee, which is why Program checks afterwards.
    /// </remarks>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Before the disposal below, so a tick cannot land on a half-torn-down window and put a
        // "the server has stopped" notice on screen during a shutdown that stopped it on purpose.
        _serverWatch.Stop();
        _serverWatch.Dispose();

        if (!_view.IsDisposed) _view.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Remember where the window was, before it stops being anywhere.</summary>
    /// <remarks>
    /// OnFormClosing, not OnFormClosed: by the time the form has closed its bounds are no longer
    /// meaningful, so the position has to be read while the window still exists.
    ///
    /// Skipped entirely once a notice is showing. A notice shrinks the window to fit a paragraph,
    /// so saving then would mean one failed launch quietly resizes every successful launch after
    /// it - a small permanent consequence of a temporary problem, and one nobody would connect
    /// back to its cause.
    /// </remarks>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Remember();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Write the zoom out the moment it changes, rather than waiting for the window to close.
    /// </summary>
    /// <remarks>
    /// Closing is not a reliable moment to save: a force-kill skips it, and seed-finder.bat
    /// force-kills a running instance before it rebuilds. Losing a window position that way is a
    /// shrug; losing a zoom level is not, because zoom is set once by someone who needs it and
    /// then expected to stay. Each change costs one small write, and Ctrl+/- is not a key
    /// combination anyone holds down.
    /// </remarks>
    private void SaveZoom(object? sender, EventArgs e) => Remember();

    /// <summary>Store where the window is and how far it is zoomed, as one record.</summary>
    private void Remember()
    {
        // Nothing about a notice is worth remembering: it resizes the window to fit a paragraph,
        // and by then the WebView2 control has been disposed so there is no zoom left to read.
        if (_showingNotice) return;

        double zoom = _view.IsDisposed ? (_restored?.UsableZoom ?? 1.0) : _view.ZoomFactor;
        WindowPlacement.From(this, zoom).Save();
    }

    /// <summary>
    /// No window to render into, so hand the job back to the browser and become a stop button.
    ///
    /// The window stays open on purpose, doing nothing but existing. It owns the server's
    /// lifetime, so closing it has to remain the way to stop the seed finder - exactly the role
    /// the console window plays in the <c>seed-finder.bat</c> flow. Exiting here instead would
    /// kill the server the browser was just sent to.
    /// </summary>
    private void ShowBrowserFallback(string? reason)
    {
        ShowNotice(
            "The seed finder is running in your browser.\r\n\r\n"
            + _server.Address + "\r\n\r\n"
            + "Keep this window open while you use it. Closing it stops the seed finder.\r\n\r\n"
            + "A window of its own needs the Microsoft Edge WebView2 runtime, which could not "
            + "be started here:\r\n" + (reason ?? "reason unknown"));

        Browse(_server.Address.ToString());
    }

    /// <summary>
    /// Replace the page with a message, in the app's own colours.
    ///
    /// Used for both ways this can end up with nothing to show: no WebView2 to render in, and a
    /// server that has gone. Replacing rather than overlaying is deliberate in the second case,
    /// where the page underneath still looks completely healthy and would otherwise invite the
    /// user to keep clicking a Search button that can no longer do anything.
    /// </summary>
    private void ShowNotice(string text)
    {
        _showingNotice = true;

        if (Controls.Contains(_view))
        {
            Controls.Remove(_view);
            _view.Dispose();
        }

        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(0xC9, 0xC5, 0xD6),
            BackColor = PageBackground,
            Padding = new Padding(40),
            Font = new Font(Font.FontFamily, 10f),
            Text = text,
        });

        // A notice needs far less room than the app, and a full-size window mostly full of empty
        // background reads as something having gone wrong beyond the message itself.
        MinimumSize = new Size(560, 320);
        if (WindowState == FormWindowState.Normal) ClientSize = new Size(720, 400);
    }

    /// <summary>
    /// Send target="_blank" links to the real browser instead of opening a second, chromeless
    /// WebView2 window. The footer's SearchTheSpire link is one of these.
    /// </summary>
    private void OpenExternally(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        Browse(e.Uri);
    }

    /// <summary>
    /// An external link opened in the same tab would be a one-way trip: the window has no back
    /// button, so the app would simply be gone until restarted. Anything not served by our own
    /// child process is handed to the browser and the navigation is cancelled.
    /// </summary>
    private void KeepNavigationLocal(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)
            && target.IsLoopback
            && target.Port == _server.Address.Port)
            return;

        e.Cancel = true;
        Browse(e.Uri);
    }

    private static void Browse(string uri)
    {
        // Only http(s). Without the check this is a launcher for whatever protocol handler a
        // crafted link names, and the page has to be treated as untrusted input on principle
        // even though we serve it ourselves.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open that link in your browser.\r\n\r\n{ex.Message}",
                "StS2 Co-op Seed Finder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
