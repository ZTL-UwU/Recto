using System.Globalization;
using Recto.Services;

namespace Recto;

public static class Program
{
    /// <summary>Absolute paths of the files passed on the command line.</summary>
    internal static IReadOnlyList<string> StartupFiles { get; private set; } = [];

    /// <summary>Whether this process owns the session's single-instance lock.</summary>
    internal static bool IsPrimary { get; private set; }

    /// <summary>Followed by the id of the process that restarted Recto (see <see cref="App.RestartAsync"/>).</summary>
    internal const string RestartAfter = "--restart-after=";

    /// <summary>Longest to wait for the old process, which stays to finish sending a print job.</summary>
    private static readonly TimeSpan MaxRestartWait = TimeSpan.FromMinutes(2);

    [STAThread]
    private static void Main(string[] args)
    {
        // A restart waits for the old process to let go of the single-instance lock, or it would hand it the files.
        if (args.FirstOrDefault(a => a.StartsWith(RestartAfter, StringComparison.Ordinal)) is { } restart
            && int.TryParse(restart.AsSpan(RestartAfter.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            WaitForExit(pid);
        StartupFiles = args.Where(a => !a.StartsWith('-')).Select(ToFullPath).OfType<string>().Where(File.Exists).ToList();
        IsPrimary = SingleInstance.TryBecomePrimary();
        // Hand the files to the running instance. If it doesn't answer (it may be closing), carry on alone.
        if (!IsPrimary && SingleInstance.TryForward(StartupFiles)) return;
        Languages.Initialize();
        XamlGeneratedProgram.XamlGeneratedMain();
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.WaitForExit(MaxRestartWait);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
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
