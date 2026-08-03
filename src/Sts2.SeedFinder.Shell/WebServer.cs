using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sts2.SeedFinder.Shell;

/// <summary>
/// The seed finder's own web app, run as a hidden child process for one window to talk to.
///
/// Nothing here is specific to being embedded: it is the same <c>sts2seedweb.exe</c> that
/// <c>seed-finder.bat</c> starts, given the same <c>--urls</c> switch. The window is a client of
/// it in exactly the way a browser tab is, which is what keeps the front end untouched.
/// </summary>
public sealed class WebServer : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    public Uri Address { get; }

    private WebServer(Process process, Uri address)
    {
        _process = process;
        Address = address;
    }

    /// <summary>Anything the child said before it died, for a message box that names the cause.</summary>
    public string Output
    {
        get { lock (_output) return _output.ToString().Trim(); }
    }

    public bool HasExited => _process.HasExited;

    /// <summary>
    /// Start the server on a port nobody else is using and wait for it to accept connections.
    /// </summary>
    /// <remarks>
    /// An ephemeral port rather than 5173, deliberately. The fixed port is right for the browser
    /// flow, where a predictable address is the whole point and a second copy should be refused.
    /// Here the window is the only client and the address is never seen, so binding the usual
    /// port would only create a way to fail: launch the shell while a browser instance is up and
    /// it would either refuse to start or, worse, quietly attach to the OTHER process and show
    /// its results. Taking a free port means the two can run side by side.
    /// </remarks>
    public static WebServer Start(TimeSpan timeout)
    {
        var exe = Locate()
            ?? throw new FileNotFoundException(
                "Could not find sts2seedweb.exe. Build the solution first: dotnet build -c Release");

        int port = FreePort();
        var address = new Uri($"http://localhost:{port}/");

        var info = new ProcessStartInfo(exe)
        {
            // The exe is a console app. Without CreateNoWindow a console flashes up and then sits
            // behind the window for the whole session, which is the single most un-native thing
            // this design could do.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        info.ArgumentList.Add("--urls");
        info.ArgumentList.Add(address.ToString().TrimEnd('/'));

        var process = Process.Start(info)
            ?? throw new InvalidOperationException("The seed finder server did not start.");

        var server = new WebServer(process, address);

        // Drained rather than ignored: a child with redirected output and nobody reading it
        // blocks once the pipe buffer fills, which would hang the server mid-search rather than
        // at startup where it would be obvious.
        process.OutputDataReceived += server.Capture;
        process.ErrorDataReceived += server.Capture;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        server.WaitUntilListening(port, timeout);
        return server;
    }

    private void Capture(object _, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        lock (_output)
        {
            // Bounded, because this is only ever read to explain a failure and a long-running
            // server would otherwise grow it without limit.
            if (_output.Length < 8192) _output.AppendLine(e.Data);
        }
    }

    /// <summary>
    /// Poll the socket rather than the process, and give up if the process dies first.
    ///
    /// Waiting on a fixed sleep is the version of this that works on the developer's machine:
    /// first start also decodes the icon cache, and a cold disk makes that slower than any
    /// constant anyone would pick.
    /// </summary>
    private void WaitUntilListening(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                throw new InvalidOperationException(
                    $"The seed finder server stopped during startup.\r\n\r\n{Output}");

            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
            }
        }

        throw new TimeoutException(
            $"The seed finder server did not start within {timeout.TotalSeconds:0} seconds.\r\n\r\n{Output}");
    }

    /// <summary>
    /// Find the built web app: beside this exe first, which is how a release is laid out, then
    /// through the source tree, which is how it looks when run from a build directory.
    /// </summary>
    private static string? Locate()
    {
        const string ExeName = "sts2seedweb.exe";

        var beside = Path.Combine(AppContext.BaseDirectory, ExeName);
        if (IsRunnable(beside)) return beside;

        // Walk up looking for the repo root, then back down into the web app's output. Both
        // configurations are tried because a Debug build of the shell should still find a web
        // app, whichever way round it was built.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 6 && dir is not null; up++, dir = dir.Parent)
        {
            foreach (var configuration in (string[])["Release", "Debug"])
            {
                var candidate = Path.Combine(
                    dir.FullName, "src", "Sts2.SeedFinder.Web", "bin", configuration, "net10.0", ExeName);
                if (IsRunnable(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// An exe is only a candidate if the managed assembly it launches is beside it.
    /// </summary>
    /// <remarks>
    /// A framework-dependent .NET exe is just an apphost: a small native launcher that loads the
    /// dll of the same name. Separate them and it still looks like a perfectly good program to
    /// File.Exists, starts, fails on its own with "the application to execute does not exist",
    /// and is gone before anything here can ask it why. Build systems separate them more readily
    /// than one would hope - this exact pair is what a ProjectReference leaves behind. Checking
    /// for the dll costs nothing and turns a puzzling startup crash into picking the next
    /// candidate.
    /// </remarks>
    private static bool IsRunnable(string exePath) =>
        File.Exists(exePath) && File.Exists(Path.ChangeExtension(exePath, ".dll"));

    /// <summary>Ask the OS for a port it is not using, then let go of it immediately.</summary>
    /// <remarks>
    /// There is a race here in principle: something else could take the port between the listener
    /// closing and the server binding. It is not worth defending against, because the failure is
    /// caught by the readiness wait and reported, and the alternative (holding the socket and
    /// passing the handle) is a large amount of machinery for a window that opens once.
    /// </remarks>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Stop the server. Called when the window closes, and again on process exit.
    ///
    /// Kill rather than a graceful shutdown request: the child holds no state worth flushing,
    /// every result already reached the page, and there is no console attached to send Ctrl+C to.
    /// A server left running would hold both a port and the built DLLs open, which is the exact
    /// failure <c>seed-finder.bat</c> has a taskkill for.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
            // Already gone, or gone between the check and the kill. Either way there is nothing
            // left to do and nothing useful to tell anyone during shutdown.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
