using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Sts2.SeedFinder.Shell;

/// <summary>
/// A window around the seed finder, for people who would rather run an app than a browser tab.
///
/// It is a shell in the literal sense: it starts the same <c>sts2seedweb.exe</c> that
/// <c>seed-finder.bat</c> starts, on a private port, and shows it in a WebView2 control. Not one
/// line of the UI changes, because from the page's point of view nothing has - it is still being
/// served over HTTP and still talking to the same API. What changes is that there is no address
/// bar, no tab strip and no console window behind it.
///
/// The browser flow is untouched and remains the cross-platform one. This is Windows-only, which
/// is the price of WebView2 and the reason it lives in its own project.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Checked before anything is on screen, because the failure it prevents is ugly: an
        // unhandled exception dialog from deep inside the control, in place of an app. The
        // runtime ships in-box on Windows 11 and arrives with Edge on Windows 10, so this is
        // rare - but "rare" and "the first thing a new user sees" do not combine well, and the
        // browser they would otherwise have used is right there as a fallback.
        bool webViewAvailable = IsWebViewInstalled(out string? webViewProblem);

        WebServer server;
        try
        {
            server = WebServer.Start(StartupTimeout);
        }
        catch (Exception ex)
        {
            // Nothing to fall back to: without the server there is no seed finder in any window.
            MessageBox.Show(
                ex.Message, "StS2 Co-op Seed Finder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // A second net for the paths Dispose cannot reach - a crash, or Task Manager ending this
        // process. A child that outlives the window holds its port and the built DLLs open, and
        // presents no window to close.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => server.Dispose();

        int browserProcessId = 0;
        try
        {
            using var window = new ShellWindow(server, webViewAvailable, webViewProblem);
            Application.Run(window);
            browserProcessId = window.BrowserProcessId;
        }
        finally
        {
            server.Dispose();
            EnsureBrowserStopped(browserProcessId);
        }
    }

    /// <summary>
    /// Make sure the browser process WebView2 started for us is actually gone.
    /// </summary>
    /// <remarks>
    /// Asking the control to shut down is the correct thing to do and usually enough, but it is
    /// asynchronous and this process is about to exit, so "usually" leaves a tail: an orphaned
    /// browser process tree with no window, which the user finds in Task Manager and reasonably
    /// reads as a leak. Six processes per launch, since Chromium is multi-process.
    ///
    /// By process ID, never by name. A machine can be running several WebView2 apps at once, and
    /// killing msedgewebview2.exe by name would take down other applications' windows - a far
    /// worse bug than the one being fixed. This id came from our own control, and the private
    /// user data folder means the process behind it is not shared with anyone else.
    /// </remarks>
    private static void EnsureBrowserStopped(int processId)
    {
        if (processId <= 0) return;

        try
        {
            using var browser = Process.GetProcessById(processId);

            // A grace period first: shutdown was already requested and letting it finish is
            // tidier than killing a process mid-write to its own profile directory.
            if (browser.WaitForExit(3000)) return;

            browser.Kill(entireProcessTree: true);
            browser.WaitForExit(3000);
        }
        catch (ArgumentException)
        {
            // Already exited, which is the expected path and not worth reporting.
        }
        catch
        {
            // Anything else here is a best-effort cleanup failing during shutdown. There is no
            // window left to show a message in and nothing the user could do about it.
        }
    }

    /// <summary>
    /// Ask whether a WebView2 runtime is installed, without creating anything.
    /// </summary>
    /// <remarks>
    /// The documented way to detect this: the call throws
    /// <c>WebView2RuntimeNotFoundException</c> when there is no runtime, and returns null rather
    /// than throwing in at least one reported case, so both are treated as absent.
    /// </remarks>
    private static bool IsWebViewInstalled(out string? problem)
    {
        problem = null;
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (!string.IsNullOrEmpty(version)) return true;

            problem = "The WebView2 runtime is not installed.";
            return false;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return false;
        }
    }
}
