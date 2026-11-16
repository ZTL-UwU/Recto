using System.Globalization;
using Recto.Services;

namespace Recto;

public static class Program
{
    /// <summary>Absolute paths of the files passed on the command line.</summary>
    internal static IReadOnlyList<string> StartupFiles { get; private set; } = [];

    /// <summary>Whether this process owns the session's single-instance lock.</summary>
    internal static bool IsPrimary { get; private set; }

    /// <summary>Whether Settings restarted Recto, which passes on the documents that were open.</summary>
    internal static bool IsRestart { get; private set; }

    /// <summary>Followed by the id of the process that restarted Recto (see <see cref="App.RestartAsync"/>).</summary>
    internal const string RestartAfter = "--restart-after=";

    /// <summary>Longest to wait for the old process, which stays to finish sending a print job.</summary>
    private static readonly TimeSpan MaxRestartWait = TimeSpan.FromMinutes(2);

    [STAThread]
    private static void Main(string[] args)
    {
        // A restart waits for the old process to let go of the single-instance lock, or it would hand it the files.
        bool oldStillRunning = false;
        if (args.FirstOrDefault(a => a.StartsWith(RestartAfter, StringComparison.Ordinal)) is { } restart
            && int.TryParse(restart.AsSpan(RestartAfter.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
        {
            IsRestart = true;
            oldStillRunning = !WaitForExit(pid);
        }
        StartupFiles = args.Where(a => !a.StartsWith('-')).Select(ToFullPath).OfType<string>().Where(File.Exists).ToList();
        IsPrimary = SingleInstance.TryBecomePrimary();
        // Hand the files to the running instance. If it doesn't answer (it may be closing), carry on alone. And if
        // it's the old process, still sending a print job, carry on alone too: it would show them in the old language.
        if (!IsPrimary && !oldStillRunning && SingleInstance.TryForward(StartupFiles)) return;
        Languages.Initialize();
        XamlGeneratedProgram.XamlGeneratedMain();
    }

    /// <summary>Waits for process <paramref name="pid"/> to exit. False if it's still running after <see cref="MaxRestartWait"/>.</summary>
    private static bool WaitForExit(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.WaitForExit(MaxRestartWait);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
            return true;
        }
    }

    private static string? ToFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
