using System.Collections.Concurrent;
using System.Globalization;

namespace Recto;

/// <summary>
/// Text shown to the user, from Strings\&lt;language&gt;\Resources.resw. XAML takes its text from the same
/// files with x:Uid; code asks here by key. UI-free, so the PDF and printing code can use it under the tests,
/// which read the English file directly.
/// </summary>
internal static class Strings
{
    private static Func<string, string?>? _lookup;
    private static string? _pluralRule;
    // Each lookup goes through Windows App SDK's resources, and some text is asked for on every pointer move or zoom step.
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>Where the text comes from: the app's resources, or the tests' copy of the English ones.</summary>
    public static void SetSource(Func<string, string?> lookup)
    {
        _lookup = lookup;
        _pluralRule = null;
        Cache.Clear();
    }

    /// <summary>The text for <paramref name="key"/>, or the key itself if there's none, which a test catches.</summary>
    public static string Get(string key) => Cache.GetOrAdd(key, static k => _lookup?.Invoke(k) is { Length: > 0 } text ? text : k);

    /// <summary>The text for <paramref name="key"/> with {0}, {1}… filled in.</summary>
    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// The text for a count: <paramref name="key"/>_One or _Other, chosen by the language's rule, with {0} as the
    /// number. English says "1 page" but "0 pages"; French says "0 page" and "1 page". Chinese has no plurals,
    /// so its two forms read the same.
    /// </summary>
    public static string Count(string key, long count, params object?[] args)
    {
        // Each language file names its rule, so this follows whichever one Windows picked.
        _pluralRule ??= Get("PluralRule");
        bool one = _pluralRule == "fr" ? count is 0 or 1 : count == 1;
        return string.Format(CultureInfo.CurrentCulture, Get(key + (one ? "_One" : "_Other")), [count, .. args]);
    }
}
