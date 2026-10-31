using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NetBinder.UI.Services;

/// <summary>
/// Starts the background NetBinder Service when the user launches the UI, so the
/// application behaves like a single program: open the UI and the service comes up
/// with it.
///
/// Both executables are marked <c>requireAdministrator</c>, so the UI process is
/// already elevated and can start the service directly without another consent
/// prompt. The service is left running when the UI closes, matching the existing
/// behaviour where bindings survive a UI restart.
/// </summary>
public class ServiceProcessLauncher
{
    /// <summary>Process name of the service executable, without extension.</summary>
    public const string ServiceProcessName = "NetBinder.Service";

    /// <summary>File name of the service executable.</summary>
    public const string ServiceExecutableName = ServiceProcessName + ".exe";

    private Process? _startedProcess;

    /// <summary>
    /// Human-readable reason the last <see cref="TryStart"/> attempt failed, or
    /// <c>null</c> when it succeeded. The UI surfaces this instead of failing silently.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Path of the service executable resolved by the last <see cref="TryStart"/> call.
    /// </summary>
    public string? ResolvedExecutablePath { get; private set; }

    /// <summary>
    /// True when a service process is already running, whoever started it.
    /// </summary>
    public static bool IsServiceRunning()
    {
        try
        {
            return Process.GetProcessesByName(ServiceProcessName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Locates the service executable that belongs with this UI build.
    /// Supports both a published layout (both EXEs side by side) and the
    /// source layout used by <c>dotnet build</c>.
    /// </summary>
    public static string? FindServiceExecutable()
    {
        // 1. Published / installed layout: next to the UI executable.
        string sideBySide = Path.Combine(AppContext.BaseDirectory, ServiceExecutableName);
        if (File.Exists(sideBySide))
        {
            return sideBySide;
        }

        // 2. Source layout: the UI runs from src\NetBinder.UI\bin\<Configuration>\<tfm>,
        //    and the service lives in a sibling project's output directory.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 6 && directory != null; depth++)
        {
            string serviceProjectDir = Path.Combine(directory.FullName, "src", "NetBinder.Service");
            if (Directory.Exists(serviceProjectDir))
            {
                var candidates = Directory
                    .EnumerateFiles(serviceProjectDir, ServiceExecutableName, SearchOption.AllDirectories)
                    .Where(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();

                if (candidates.Count > 0)
                {
                    return candidates[0];
                }
            }

            directory = directory.Parent;
        }

        // 3. Last resort: an installed copy beside the user's applications.
        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireShift", ServiceExecutableName);
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>
    /// Starts the service if it is not already running.
    /// Returns true when a service process is running after the call.
    /// </summary>
    public bool TryStart()
    {
        LastError = null;
        ResolvedExecutablePath = null;

        if (IsServiceRunning())
        {
            return true;
        }

        string? executable = FindServiceExecutable();
        ResolvedExecutablePath = executable;

        if (executable == null)
        {
            LastError = $"NetBinder.Service.exe was not found. It should be in the same folder as the UI " +
                        $"({AppContext.BaseDirectory}) or in a built source tree.";
            Console.WriteLine($"[ServiceProcessLauncher] {LastError}");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                // Keep the console window out of the way; the service is a background worker.
                WindowStyle = ProcessWindowStyle.Hidden
            };

            _startedProcess = Process.Start(startInfo);
            Console.WriteLine($"[ServiceProcessLauncher] Started service: {executable}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Could not start '{executable}': {ex.Message}";
            Console.WriteLine($"[ServiceProcessLauncher] {LastError}");
            return false;
        }
    }

    /// <summary>
    /// Starts the service and waits until its named pipe accepts a connection,
    /// which means the worker finished initialising.
    /// </summary>
    public async Task<bool> EnsureStartedAsync(PipeClient pipeClient, TimeSpan timeout, CancellationToken ct = default)
    {
        if (pipeClient.IsConnected)
        {
            return true;
        }

        if (!IsServiceRunning())
        {
            TryStart();
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await pipeClient.ConnectAsync(ct);
                return true;
            }
            catch
            {
                // Service is still starting up; try again shortly.
            }

            await Task.Delay(250, ct);
        }

        // The process may have started and then exited, which leaves LastError empty.
        if (LastError == null && !IsServiceRunning())
        {
            LastError = "The service process exited before it opened its pipe. " +
                        "Run NetBinder.Service.exe manually to see its console output.";
        }

        return false;
    }
}
