using Recto.Services;

namespace Recto;

public static class Program
{
    /// <summary>Absolute paths of the files passed on the command line.</summary>
    internal static IReadOnlyList<string> StartupFiles { get; private set; } = [];

    /// <summary>Whether this process owns the session's single-instance lock.</summary>
    internal static bool IsPrimary { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        StartupFiles = args.Where(a => !a.StartsWith('-')).Select(ToFullPath).OfType<string>().Where(File.Exists).ToList();
        IsPrimary = SingleInstance.TryBecomePrimary();
        // Hand the files to the running instance. If it doesn't answer (it may be closing), carry on alone.
        if (!IsPrimary && SingleInstance.TryForward(StartupFiles)) return;
        Languages.Initialize();
        XamlGeneratedProgram.XamlGeneratedMain();
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
