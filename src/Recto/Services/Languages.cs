using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace Recto.Services;

/// <summary>
/// The languages Recto is translated into (one folder each in Strings), and the one chosen in Settings.
/// Windows picks the closest of them to the user's languages unless Settings names one.
/// </summary>
internal static class Languages
{
    /// <summary>In the order Settings lists them, after "Use system setting". Each is named in its own language.</summary>
    public static readonly (string Tag, string Name)[] Available = [("en-US", "English"), ("fr-FR", "Français"), ("zh-CN", "简体中文")];

    /// <summary>
    /// The language chosen when Recto started ("" to follow Windows). Text that's already showing can't
    /// switch language, so a different choice takes effect the next time Recto starts.
    /// </summary>
    public static string StartupChoice { get; private set; } = "";

    /// <summary>Applies the language chosen in Settings. Runs before any XAML loads, which reads its text once.</summary>
    public static void Initialize()
    {
        StartupChoice = Normalize(AppState.Preferences.Language);
        try
        {
            // Always set, since "" is what clears an override an earlier run left.
            ApplicationLanguages.PrimaryLanguageOverride = StartupChoice;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
        }
        // Opened on first use, not at startup.
        var loader = new Lazy<ResourceLoader>(() => new ResourceLoader());
        Strings.SetSource(key =>
        {
            try
            {
                return loader.Value.GetString(key);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        });
    }

    /// <summary>A tag from <see cref="Available"/>, or "" for anything else, such as a language no longer offered.</summary>
    public static string Normalize(string? tag) => Available.FirstOrDefault(l => l.Tag == tag).Tag ?? "";
}
