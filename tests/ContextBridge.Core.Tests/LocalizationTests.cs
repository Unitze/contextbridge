using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ContextBridge.Core;
using ContextBridge.Core.Resources;

namespace ContextBridge.Core.Tests;

static class TestCulture
{
    /// <summary>The message assertions in this project are written against the Japanese resources.</summary>
    [ModuleInitializer]
    internal static void UseJapanese()
    {
        var ja = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.DefaultThreadCurrentUICulture = ja;
        CultureInfo.CurrentUICulture = ja;
    }
}

public class LocalizationTests
{
    static readonly Regex Placeholder = new(@"\{(\d+)[^}]*\}", RegexOptions.Compiled);

    static Dictionary<string, string> Load(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    public static IEnumerable<object[]> ResourceSets() => new[]
    {
        new object[] { "src/ContextBridge.Core/Resources" },
        new object[] { "src/ContextBridge.App/Resources" },
    };

    [Theory]
    [MemberData(nameof(ResourceSets))]
    public void TranslationsMatchNeutralKeysAndPlaceholders(string dir)
    {
        string root = Path.Combine(TestEnv.RepoRoot(), dir);
        var en = Load(Path.Combine(root, "Strings.resx"));
        Assert.NotEmpty(en);
        var translations = Directory.GetFiles(root, "Strings.*.resx");
        Assert.Contains(translations, f => Path.GetFileName(f) == "Strings.ja.resx");
        foreach (var file in translations)
        {
            string name = Path.GetFileName(file);
            var tr = Load(file);
            Assert.Empty(en.Keys.Except(tr.Keys));
            Assert.Empty(tr.Keys.Except(en.Keys));
            foreach (var (key, value) in en)
            {
                var a = Placeholder.Matches(value).Select(m => m.Groups[1].Value).Distinct().Order();
                var b = Placeholder.Matches(tr[key]).Select(m => m.Groups[1].Value).Distinct().Order();
                Assert.True(a.SequenceEqual(b), $"{dir}/{name}: placeholders differ for {key}");
                Assert.False(string.IsNullOrWhiteSpace(tr[key]), $"{dir}/{name}: empty text for {key}");
            }
        }
    }

    [Fact]
    public void MessagesFollowUiCulture()
    {
        var cmd = new CommandDef { Id = "x", Action = new ActionDef { Program = "" } };
        var cfg = new ConfigFile();
        cfg.Commands.Add(cmd);
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Contains("no program is specified", ConfigValidator.Validate(cfg)[0].Message);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");  // falls back to English
            Assert.Contains("no program is specified", ConfigValidator.Validate(cfg)[0].Message);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert.Contains("プログラムが指定されていません", ConfigValidator.Validate(cfg)[0].Message);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void AutoTitleFollowsMenuLanguage()
    {
        var cmd = new CommandDef { Id = "x", Action = new ActionDef { Program = @"C:\Tools\wt.exe" } };
        Assert.Equal("wt で開く", Template.DisplayTitle(cmd, CultureInfo.GetCultureInfo("ja-JP")));
        Assert.Equal("Open in wt", Template.DisplayTitle(cmd, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("Open in wt", Template.DisplayTitle(cmd, CultureInfo.GetCultureInfo("de-DE")));
        cmd.Title = "Mine";
        Assert.Equal("Mine", Template.DisplayTitle(cmd, CultureInfo.GetCultureInfo("ja-JP")));
    }

    [Fact]
    public void ExclusionReasonsAreLocalized()
    {
        Assert.Equal(Strings.Excl_Builtin, Legacy.DefaultExclusions.Match("open", "x.exe"));
        Assert.Equal(Strings.Excl_System, Legacy.DefaultExclusions.Match("foo", @"C:\Windows\explorer.exe"));
        Assert.Null(Legacy.DefaultExclusions.Match("foo", "x.exe"));
    }
}
