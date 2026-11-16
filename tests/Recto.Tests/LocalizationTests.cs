using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Recto.Tests;

/// <summary>The app's text, read from its resource files in the source tree.</summary>
internal static class AppStrings
{
    public static readonly string SourceFolder = FindSourceFolder();
    public static readonly string Folder = Path.Combine(SourceFolder, "Strings");
    public const string Fallback = "en-US";

    public static IEnumerable<string> Languages => Directory.GetDirectories(Folder).Select(Path.GetFileName).OfType<string>();

    public static Dictionary<string, string> Load(string language) =>
        XDocument.Load(Path.Combine(Folder, language, "Resources.resw")).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    /// <summary>The code under test reads its messages through Strings, as the app does, in English.</summary>
    [ModuleInitializer]
#pragma warning disable CA2255 // A test assembly is the place for it: nothing else loads it.
    internal static void UseEnglish()
#pragma warning restore CA2255
    {
        var english = Load(Fallback);
        Strings.SetSource(english.GetValueOrDefault);
    }

    private static string FindSourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Recto.sln"))) return Path.Combine(dir.FullName, "src", "Recto");
        }
        throw new DirectoryNotFoundException("The checkout holding the tests wasn't found.");
    }
}

public sealed partial class LocalizationTests
{
    private static readonly Dictionary<string, string> English = AppStrings.Load(AppStrings.Fallback);

    public static TheoryData<string> Translations => [.. AppStrings.Languages.Where(l => l != AppStrings.Fallback)];

    [Fact]
    public void ThereAreTranslations() => Assert.NotEmpty(Translations);

    [Theory]
    [MemberData(nameof(Translations))]
    public void TranslationsHaveTheSameKeys(string language)
    {
        var translated = AppStrings.Load(language);
        Assert.Empty(English.Keys.Except(translated.Keys));
        Assert.Empty(translated.Keys.Except(English.Keys));
        Assert.All(translated, t => Assert.False(string.IsNullOrWhiteSpace(t.Value), t.Key));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void TranslationsFillInTheSameValues(string language)
    {
        foreach (var (key, text) in AppStrings.Load(language))
        {
            // English can leave the number out of a singular ("One page couldn't…"); the other forms can't.
            var expected = Placeholders(English[key]);
            var actual = Placeholders(text);
            if (key.EndsWith("_One", StringComparison.Ordinal)) Assert.Subset(Placeholders(English[key[..^4] + "_Other"]), actual);
            else Assert.True(expected.SetEquals(actual), $"{language} {key}: {text}");
        }
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void FormatsAreValid(string language)
    {
        object[] values = [1, 2, 3, 4];
        foreach (var (key, text) in AppStrings.Load(language))
        {
            var ex = Record.Exception(() => string.Format(text, values));
            Assert.True(ex is null, $"{language} {key}: {text}");
        }
    }

    public static TheoryData<string> AllLanguages => [.. AppStrings.Languages];

    [Fact]
    public void KeysTheCodeAsksForExist()
    {
        var missing = new List<string>();
        foreach (var (file, code) in SourceFiles("*.cs"))
        {
            foreach (Match call in StringsCall().Matches(code))
            {
                string argument = FirstArgument(code, call.Index + call.Length);
                // A key kept in a variable or a table can't be checked here, so the code names each one in the call.
                if (!argument.Contains('"')) missing.Add($"{Path.GetFileName(file)}: Strings.{call.Groups[1].Value}({argument}) names no key");
                // Literals that are joined to something else ("Color" + name) are prefixes, not keys.
                foreach (Match literal in KeyLiteral().Matches(argument))
                {
                    string key = literal.Groups[1].Value;
                    string[] keys = call.Groups[1].Value == "Count" ? [key + "_One", key + "_Other"] : [key];
                    missing.AddRange(keys.Where(k => !English.ContainsKey(k)).Select(k => $"{Path.GetFileName(file)}: {k}"));
                }
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryKeyIsUsed()
    {
        string code = string.Concat(SourceFiles("*.cs").Select(f => f.Text));
        var uids = Uids();
        // Keys made up in the code: Color + "Yellow", Key + "Shift"…
        string[] prefixes = ["Color", "Highlight", "Key"];
        var unused = English.Keys.Where(key =>
        {
            if (key == "PluralRule") return false;
            int dot = key.IndexOf('.');
            if (dot >= 0) return !uids.Contains(key[..dot]);
            string name = Regex.Replace(key, "_(One|Other)$", "");
            return !code.Contains($"\"{name}\"")
                && !prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal) && code.Contains($"\"{name[p.Length..]}\""));
        });
        Assert.Empty(unused);
    }

    [Fact]
    public void EveryUidHasText()
    {
        var withText = English.Keys.Where(k => k.Contains('.')).Select(k => k[..k.IndexOf('.')]).ToHashSet();
        Assert.Empty(Uids().Except(withText));
    }

    private static HashSet<string> Uids() =>
        SourceFiles("*.xaml").SelectMany(f => Uid().Matches(f.Text).Select(m => m.Groups[1].Value)).ToHashSet();

    private static IEnumerable<(string File, string Text)> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(AppStrings.SourceFolder, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (f, File.ReadAllText(f)));

    /// <summary>The text of a call's first argument, from just after its opening parenthesis.</summary>
    private static string FirstArgument(string code, int start)
    {
        int depth = 0;
        for (int i = start; i < code.Length; i++)
        {
            char c = code[i];
            if (c == '"')
            {
                for (i++; i < code.Length && code[i] != '"'; i++)
                {
                    if (code[i] == '\\') i++;
                }
            }
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth-- == 0) return code[start..i];
            }
            else if (c == ',' && depth == 0) return code[start..i];
        }
        return code[start..];
    }

    private static HashSet<int> Placeholders(string text) => Placeholder().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();

    [GeneratedRegex(@"\bStrings\.(Get|Format|Count)\(")]
    private static partial Regex StringsCall();

    [GeneratedRegex(@"""(\w+)""(?!\s*\+)")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"\{(\d+)")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"x:Uid=""(\w+)""")]
    private static partial Regex Uid();
}
