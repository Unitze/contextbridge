using ContextBridge.Core;
using ContextBridge.Core.Legacy;

namespace ContextBridge.Core.Tests;

/// <summary>In-memory HKCR used to test migration without a Windows registry.</summary>
sealed class FakeRegistry : IRegistryView
{
    // key path (case-insensitive) → values (name "" = default)
    readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistry Set(string key, string? name, string value)
    {
        EnsureKey(key)[name ?? ""] = value;
        return this;
    }

    public FakeRegistry Verb(string shellKey, string verb, string command, params (string Name, string Value)[] values)
    {
        string vk = shellKey + @"\" + verb;
        EnsureKey(vk);
        foreach (var (n, v) in values) Set(vk, n, v);
        Set(vk + @"\command", null, command);
        return this;
    }

    Dictionary<string, string> EnsureKey(string key)
    {
        // create parents so SubKeyNames works
        var parts = key.Split('\\');
        for (int i = 1; i <= parts.Length; i++)
        {
            var p = string.Join('\\', parts.Take(i));
            if (!_keys.ContainsKey(p)) _keys[p] = new(StringComparer.OrdinalIgnoreCase);
        }
        return _keys[key];
    }

    public IEnumerable<string> SubKeyNames(string path)
    {
        string prefix = path.Length == 0 ? "" : path + @"\";
        return _keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.Length > prefix.Length)
            .Select(k => k[prefix.Length..])
            .Where(rest => !rest.Contains('\\'))
            .ToList();
    }

    public bool KeyExists(string path) => _keys.ContainsKey(path);
    public bool HasValue(string path, string name) => _keys.TryGetValue(path, out var v) && v.ContainsKey(name);
    public string? GetString(string path, string? name) =>
        _keys.TryGetValue(path, out var v) && v.TryGetValue(name ?? "", out var s) ? s : null;
}

public class LegacyCommandLineTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Foo\\foo.exe\" \"%1\"", "C:\\Program Files\\Foo\\foo.exe", "\"%1\"")]
    [InlineData("C:\\Program Files\\Foo\\foo.exe \"%1\"", "C:\\Program Files\\Foo\\foo.exe", "\"%1\"")]
    [InlineData("notepad.exe %1", "notepad.exe", "%1")]
    [InlineData("C:\\tools\\run.cmd", "C:\\tools\\run.cmd", "")]
    [InlineData("\"%ProgramFiles%\\Git\\git-bash.exe\" \"--cd=%v.\"", "%ProgramFiles%\\Git\\git-bash.exe", "\"--cd=%1.\"")]
    [InlineData("wt.exe -d \"%V\"", "wt.exe", "-d \"%1\"")]
    [InlineData("app.exe /dir \"%W\" %*", "app.exe", "/dir \"%d\" %*")]
    [InlineData("app.exe %L %I", "app.exe", "%1")]
    [InlineData("app.exe 100%", "app.exe", "100%%")]
    [InlineData("notepad foo.exe", "notepad", "foo.exe")]
    [InlineData("app.exe \"%1.%2\"", "app.exe", "\"%1.\"")]
    [InlineData("app.exe \"%I\"", "app.exe", "")]
    public void ParsesAndConvertsPlaceholders(string raw, string program, string args)
    {
        var p = LegacyCommandLine.Parse(raw);
        Assert.Equal(program, p.Program);
        Assert.Equal(args, p.Arguments);
    }

    [Fact]
    public void ExpandsConvertedTemplateLikeExplorerWould()
    {
        var p = LegacyCommandLine.Parse("\"C:\\x\\x.exe\" \"%1\" --cwd \"%W\"");
        var got = Template.Expand(p.Arguments, new Template.Context(new[] { @"C:\w\a b.txt" }, @"C:\w"), _ => null);
        Assert.Equal("\"C:\\w\\a b.txt\" --cwd \"C:\\w\"", got);
    }
}

public class LegacyScannerTests
{
    static LegacyScanner Scanner(FakeRegistry reg) =>
        new(reg, s => s == "@shell32.dll,-8506" ? "Git Bash ここで開く(&B)" : null);

    static FakeRegistry Sample() => new FakeRegistry()
        .Verb(@"Directory\shell", "git_shell", "\"C:\\Program Files\\Git\\git-bash.exe\" \"--cd=%1\"",
              ("", "Git &Bash Here"), ("Icon", "C:\\Program Files\\Git\\git-bash.exe"))
        .Verb(@"Directory\Background\shell", "git_shell", "\"C:\\Program Files\\Git\\git-bash.exe\" \"--cd=%1\"",
              ("", "Git &Bash Here"))
        .Verb(@"*\shell", "Open with Foo", "\"C:\\Foo\\foo.exe\" \"%1\"", ("Icon", "\"C:\\Foo\\foo.ico\",0"))
        .Verb(@".txt\shell", "mytool", "C:\\T\\t.exe \"%1\"", ("MUIVerb", "@shell32.dll,-8506"))
        .Verb(@"SystemFileAssociations\.md\shell", "mytool", "C:\\T\\t.exe \"%1\"")
        .Verb(@"*\shell", "secret", "x.exe %1", ("Extended", ""))
        .Verb(@"*\shell", "hidden", "x.exe %1", ("ProgrammaticAccessOnly", ""))
        .Verb(@"*\shell", "cascade", "", ("SubCommands", ""))
        .Verb(@"*\shell", "player", "C:\\P\\p.exe /add \"%1\"", ("MultiSelectModel", "Player"))
        .Verb(@"*\shell", "player_eq", "C:\\P\\p.exe \"--open=%1\"", ("MultiSelectModel", "Player"))
        .Verb(@"*\shell", "aqs", "C:\\P\\p.exe \"%1\"", ("AppliesTo", "System.FileName:~<\".txt\""))
        .Verb(@"Drive\shell", "open", "explorer.exe %1")
        .Set(@".png", null, "pngfile");

    [Fact]
    public void ScansMergesAndConverts()
    {
        var result = Scanner(Sample()).Scan(new ConfigFile());
        var git = result.Candidates.Single(c => c.Verb == "git_shell");
        Assert.Equal(CandidateStatus.New, git.Status);
        Assert.Equal("Git Bash Here", git.Title);
        Assert.Equal(2, git.Keys.Count);
        Assert.Equal(new[] { ItemTypes.Folder, ItemTypes.FolderBackground }, git.Proposed!.Target.ItemTypes);
        Assert.Equal(@"C:\Program Files\Git\git-bash.exe", git.Proposed.Action.Program);
        Assert.Equal("\"--cd=%1\"", git.Proposed.Action.Arguments);
        Assert.Equal(MultiFileModes.Each, git.Proposed.Action.MultiFileMode);
        Assert.Equal(IconSources.File, git.Proposed.Icon.Source);
        Assert.Equal("legacy-git-shell", git.Proposed.Id);
        Assert.Equal("legacy", git.Proposed.Origin!.Type);

        var foo = result.Candidates.Single(c => c.Verb == "Open with Foo");
        Assert.Null(foo.Proposed!.Target.Extensions);  // *\shell → all files
        Assert.Equal(@"C:\Foo\foo.ico", foo.Proposed.Icon.Path);

        var tool = result.Candidates.Single(c => c.Verb == "mytool");
        Assert.Equal("Git Bash ここで開く", tool.Title);  // indirect string resolved, mnemonic stripped
        Assert.Equal(new[] { ".md", ".txt" }, tool.Proposed!.Target.Extensions);

        Assert.DoesNotContain(result.Candidates, c => c.Verb == "hidden");
        Assert.Equal(CandidateStatus.Unsupported, result.Candidates.Single(c => c.Verb == "secret").Status);
        Assert.Equal(CandidateStatus.Unsupported, result.Candidates.Single(c => c.Verb == "cascade").Status);

        var player = result.Candidates.Single(c => c.Verb == "player");
        Assert.Equal(MultiFileModes.Single, player.Proposed!.Action.MultiFileMode);
        Assert.Equal("/add %*", player.Proposed.Action.Arguments);

        var playerEq = result.Candidates.Single(c => c.Verb == "player_eq");
        Assert.Equal("\"--open=%1\"", playerEq.Proposed!.Action.Arguments);  // must not corrupt embedded %1

        Assert.Equal(CandidateStatus.Unsupported, result.Candidates.Single(c => c.Verb == "aqs").Status);

        Assert.NotNull(result.Candidates.Single(c => c.Verb == "open").ExcludedReason);
        Assert.Null(git.ExcludedReason);
    }

    [Fact]
    public void RescanReportsImportedChangedAndRemoved()
    {
        var first = Scanner(Sample()).Scan(new ConfigFile());
        var cfg = new ConfigFile();
        cfg.Commands.Add(first.Candidates.Single(c => c.Verb == "git_shell").Proposed!);
        cfg.Commands.Add(first.Candidates.Single(c => c.Verb == "mytool").Proposed!);
        var gone = first.Candidates.Single(c => c.Verb == "Open with Foo").Proposed!;
        gone.Origin!.Keys = new() { @"*\shell\uninstalled" };
        cfg.Commands.Add(gone);

        var reg = Sample().Verb(@".txt\shell", "mytool", "C:\\T\\t2.exe \"%1\"")
                          .Verb(@"SystemFileAssociations\.md\shell", "mytool", "C:\\T\\t2.exe \"%1\"");
        var again = Scanner(reg).Scan(cfg);
        Assert.Equal(CandidateStatus.Imported, again.Candidates.Single(c => c.Verb == "git_shell").Status);
        var changed = again.Candidates.Single(c => c.Verb == "mytool");
        Assert.Equal(CandidateStatus.Changed, changed.Status);
        Assert.Equal(changed.ExistingId, changed.Proposed!.Id);
        Assert.Single(again.RemovedFromRegistry);
        Assert.Equal(CandidateStatus.New, again.Candidates.Single(c => c.Verb == "Open with Foo").Status);
    }

    [Theory]
    [InlineData("&Open", "Open")]
    [InlineData("Save && Exit", "Save & Exit")]
    [InlineData("開く(&O)", "開く")]
    public void StripsAccelerators(string input, string expected) =>
        Assert.Equal(expected, LegacyScanner.StripAccelerator(input));

    [Theory]
    [InlineData("C:\\a\\b.exe,3", "C:\\a\\b.exe", 3)]
    [InlineData("\"C:\\a b\\c.dll\",-101", "C:\\a b\\c.dll", -101)]
    [InlineData("\"C:\\a b\\c.exe,2\"", "C:\\a b\\c.exe", 2)]
    [InlineData("C:\\a\\b.ico", "C:\\a\\b.ico", 0)]
    public void ParsesIconLocations(string s, string path, int index) =>
        Assert.Equal((path, index), LegacyScanner.ParseIconLocation(s));
}
