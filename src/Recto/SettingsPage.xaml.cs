using System.Reflection;
using Recto.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Recto;

/// <summary>The settings page a window shows in place of its content, like the one in Windows apps.</summary>
public sealed partial class SettingsPage : UserControl
{
    // In the order the options are listed.
    private static readonly (AppTheme Theme, string Name)[] Themes =
    [
        (AppTheme.Light, Strings.Get("ThemeLight")), (AppTheme.Dark, Strings.Get("ThemeDark")), (AppTheme.System, Strings.Get("UseSystemSetting")),
    ];

    /// <summary>Following Windows ("" in the preferences), then the languages Recto has, each named in itself.</summary>
    private static readonly (string Tag, string Name)[] LanguageChoices = [("", Strings.Get("UseSystemSetting")), .. Languages.Available];

    /// <summary>Set while showing the current preferences, so that doesn't count as the user changing them.</summary>
    private bool _refreshing;

    public SettingsPage()
    {
        InitializeComponent();
        foreach (var (_, name) in Themes) ThemeOptions.Items.Add(name);
        foreach (var (_, name) in LanguageChoices) LanguageBox.Items.Add(name);
        var assembly = typeof(SettingsPage).Assembly;
        VersionText.Text = Strings.Format("Version", assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        AboutCard.Description = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright.Replace("(c)", "©") ?? "";
    }

    /// <summary>Shows the current preferences, which another window may have changed, from the top.</summary>
    public void Refresh()
    {
        var prefs = AppState.Preferences;
        _refreshing = true;
        ThemeOptions.SelectedIndex = Array.FindIndex(Themes, t => t.Theme == prefs.Theme);
        LanguageBox.SelectedIndex = Array.FindIndex(LanguageChoices, l => l.Tag == Languages.Normalize(prefs.Language));
        CornersToggle.IsOn = prefs.RoundedPageCorners;
        HyphenatedToggle.IsOn = prefs.SearchJoinHyphenated;
        QuotesToggle.IsOn = prefs.SearchAnyQuote;
        DashesToggle.IsOn = prefs.SearchAnyDash;
        RecentToggle.IsOn = prefs.RememberRecent;
        _refreshing = false;
        UpdateThemeValue();
        UpdateLanguageRestart();
        Scroller.ChangeView(null, 0, null, true);
    }

    private void UpdateThemeValue() => ThemeValue.Text = ThemeOptions.SelectedIndex >= 0 ? Themes[ThemeOptions.SelectedIndex].Name : "";

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateThemeValue();
        if (_refreshing || ThemeOptions.SelectedIndex < 0) return;
        AppState.Preferences.Theme = Themes[ThemeOptions.SelectedIndex].Theme;
        AppState.Save();
        App.ApplyTheme();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLanguageRestart();
        if (_refreshing || LanguageBox.SelectedIndex < 0) return;
        string tag = LanguageChoices[LanguageBox.SelectedIndex].Tag;
        AppState.Preferences.Language = tag.Length > 0 ? tag : null;
        AppState.Save();
    }

    /// <summary>Says when the language chosen isn't the one Recto started in, which it switches to only when it restarts.</summary>
    private void UpdateLanguageRestart() =>
        LanguageRestartBar.IsOpen = LanguageBox.SelectedIndex >= 0 && LanguageChoices[LanguageBox.SelectedIndex].Tag != Languages.StartupChoice;

    private void OnCornersToggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        AppState.Preferences.RoundedPageCorners = CornersToggle.IsOn;
        AppState.Save();
        App.ApplyPageCorners();
    }

    private void OnHyphenatedToggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        AppState.Preferences.SearchJoinHyphenated = HyphenatedToggle.IsOn;
        AppState.Save();
    }

    private void OnQuotesToggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        AppState.Preferences.SearchAnyQuote = QuotesToggle.IsOn;
        AppState.Save();
    }

    private void OnDashesToggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        AppState.Preferences.SearchAnyDash = DashesToggle.IsOn;
        AppState.Save();
    }

    private void OnRecentToggled(object sender, RoutedEventArgs e)
    {
        if (!_refreshing) AppState.SetRememberRecent(RecentToggle.IsOn);
    }
}
