using System.Text.Json;
using ContextBridge.Core;
using ContextBridge.Core.Legacy;
using ContextBridge.Core.Sharing;

namespace ContextBridge.Core.Tests;

static class TestEnv
{
    public static readonly Dictionary<string, string> Vars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
        ["APPDATA"] = @"C:\Users\me\AppData\Roaming",
        ["USERPROFILE"] = @"C:\Users\me",
        ["ProgramFiles"] = @"C:\Program Files",
        ["DATA"] = @"D:\data",
        ["SystemRoot"] = @"C:\Windows",
        ["PATH"] = @"C:\Tools;C:\Program Files\Git\cmd",
        ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
    };

    public static string? Lookup(string name) => Vars.TryGetValue(name, out var v) ? v : null;

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

public class SharedVectorTests
{
    // The same vectors run against the native handler (tests/native/core_tests.cpp).
    [Fact]
    public void TemplatesMatchNativeVectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnv.RepoRoot(), "tests/vectors/vectors.json")));
        foreach (var v in doc.RootElement.GetProperty("templates").EnumerateArray())
        {
            var paths = v.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToList();
            var ctx = new Template.Context(paths, v.GetProperty("directory").GetString()!);
            var got = Template.Expand(v.GetProperty("template").GetString()!, ctx, TestEnv.Lookup);
            Assert.Equal(v.GetProperty("expected").GetString(), got);
        }
    }

    [Fact]
    public void ConfigValidityMatchesNativeVectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnv.RepoRoot(), "tests/vectors/vectors.json")));
        foreach (var v in doc.RootElement.GetProperty("configs").EnumerateArray())
        {
            string name = v.GetProperty("name").GetString()!;
            bool expected = v.GetProperty("valid").GetBoolean();
            bool ok;
            try
            {
                var cfg = ConfigSerializer.Parse(v.GetProperty("json").GetString()!);
                // A command without "action" deserializes to an empty program → error, like native.
                ok = !ConfigValidator.HasErrors(ConfigValidator.Validate(cfg));
            }
            catch (JsonException)
            {
                ok = false;
            }
            Assert.True(expected == ok, $"vector '{name}': expected valid={expected}");
        }
    }
}

public class ConfigTests
{
    const string Sample = """
    {
      "version": 1,
      "groups": [ { "id": "editors", "title": "エディタで開く" } ],
      "commands": [
        {
          "id": "open-with-example",
          "title": "Example で開く",
          "group": "editors",
          "enabled": true,
          "target": { "itemTypes": ["file"], "extensions": [".txt", ".md"], "minSelection": 1, "maxSelection": 20 },
          "action": { "program": "C:\\Tools\\example.exe", "arguments": "\"%*\"", "workingDirectory": "%d",
                      "multiFileMode": "single", "runAsAdmin": false, "window": "normal" },
          "icon": { "source": "program", "path": null, "index": 0 },
          "futureField": { "x": 1 }
        }
      ],
      "futureTop": true
    }
    """;

    [Fact]
    public void RoundTripPreservesUnknownFieldsAndJapanese()
    {
        var cfg = ConfigSerializer.Parse(Sample);
        Assert.Single(cfg.Commands);
        Assert.Equal("エディタで開く", cfg.Groups[0].Title);
        string json = ConfigSerializer.Serialize(cfg);
        Assert.Contains("エディタで開く", json);       // not \u-escaped
        Assert.Contains("futureField", json);
        Assert.Contains("futureTop", json);
        var again = ConfigSerializer.Parse(json);
        Assert.Equal(cfg.Commands[0].Action.Arguments, again.Commands[0].Action.Arguments);
    }

    [Fact]
    public void StoreSavesAtomicallyWithBackup()
    {
        var dir = Directory.CreateTempSubdirectory("cbtest").FullName;
        try
        {
            var store = new ConfigStore(dir);
            Assert.Empty(store.Load().Commands);  // missing file → empty config
            var cfg = ConfigSerializer.Parse(Sample);
            store.Save(cfg);
            cfg.Commands[0].Title = "changed";
            store.Save(cfg);
            Assert.Equal("changed", store.Load().Commands[0].Title);
            Assert.True(File.Exists(store.BackupPath));
            Assert.False(File.Exists(store.ConfigPath + ".tmp"));
            // No BOM: the native reader accepts one, but other tools may not.
            Assert.NotEqual(0xEF, File.ReadAllBytes(store.ConfigPath)[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StoreRefusesInvalidConfig()
    {
        var dir = Directory.CreateTempSubdirectory("cbtest").FullName;
        try
        {
            var cfg = new ConfigFile();
            cfg.Commands.Add(new CommandDef { Id = "a" });  // empty program
            Assert.Throws<InvalidOperationException>(() => new ConfigStore(dir).Save(cfg));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ValidatorWarnsAboutCountAndMissingPrograms()
    {
        var cfg = new ConfigFile();
        for (int i = 0; i < ConfigValidator.CommandCountWarningThreshold + 1; i++)
            cfg.Commands.Add(new CommandDef { Id = $"c{i}", Action = new ActionDef { Program = "x.exe", Arguments = "%*" } });
        var issues = ConfigValidator.Validate(cfg, _ => false);
        Assert.False(ConfigValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.CommandId is null && i.Message.Contains("有効なコマンド"));
        Assert.Contains(issues, i => i.Message.Contains("見つかりません"));
    }

    [Fact]
    public void ValidatorFlagsFirstFileOnlyArguments()
    {
        var cfg = new ConfigFile();
        cfg.Commands.Add(new CommandDef { Id = "a", Action = new ActionDef { Program = "x", Arguments = "\"%1\"" } });
        Assert.Contains(ConfigValidator.Validate(cfg), i => i.Message.Contains("%1 のみ"));

        // Background-only command is always a single target, so %1 must not warn.
        var bg = new ConfigFile();
        bg.Commands.Add(new CommandDef
        {
            Id = "bg",
            Target = new TargetDef { ItemTypes = new() { ItemTypes.FolderBackground } },
            Action = new ActionDef { Program = "x", Arguments = "\"%1\"" },
        });
        Assert.DoesNotContain(ConfigValidator.Validate(bg), i => i.Message.Contains("%1 のみ"));
    }

    [Fact]
    public void SerializerRejectsMissingVersionAndNullEntries()
    {
        Assert.Throws<JsonException>(() => ConfigSerializer.Parse("{\"commands\":[]}"));
        Assert.Throws<JsonException>(() => ConfigSerializer.Parse("{\"version\":1,\"commands\":[null]}"));
        Assert.Throws<JsonException>(() => ConfigSerializer.Parse("{\"version\":1,\"groups\":[null]}"));
        var cfg = ConfigSerializer.Parse("{\"version\":1,\"commands\":[{\"id\":\"a\",\"action\":{\"program\":\"x\",\"arguments\":null}}]}");
        Assert.Equal("", cfg.Commands[0].Action.Arguments);
        Assert.False(ConfigValidator.HasErrors(ConfigValidator.Validate(cfg)));
    }
}

public class PackageIdentityTests
{
    [Fact]
    public void PublisherIdMatchesKnownMicrosoftValue()
    {
        Assert.Equal("8wekyb3d8bbwe",
            PackageIdentity.PublisherId("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
    }

    [Fact]
    public void FamilyNameShape()
    {
        Assert.Matches("^ContextBridge_[0-9a-z]{13}$", PackageIdentity.FamilyName);
        Assert.Equal("1.2.3.0", PackageIdentity.ToPackageVersion("1.2.3"));
        Assert.Equal("0.1.0.0", PackageIdentity.VersionFromFullName("ContextBridge_0.1.0.0_x64__abc"));
    }

    [Fact]
    public void ManifestPublisherMatchesCode()
    {
        var manifest = File.ReadAllText(Path.Combine(TestEnv.RepoRoot(), "package/AppxManifest.xml.in"));
        Assert.Contains($"Publisher=\"{PackageIdentity.Publisher}\"", manifest);
        Assert.Contains($"Name=\"{PackageIdentity.Name}\"", manifest);
    }
}

public class ProgramLocatorTests
{
    static ProgramLocator Make(params string[] files)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return new ProgramLocator(TestEnv.Lookup, name => name.Equals("code.exe", StringComparison.OrdinalIgnoreCase)
            ? @"C:\Users\me\AppData\Local\Programs\VS Code\Code.exe" : null, set.Contains);
    }

    [Fact]
    public void ResolvesAbsoluteEnvPathAndAppPaths()
    {
        var loc = Make(@"C:\Users\me\AppData\Local\x\y.exe", @"C:\Windows\System32\notepad.exe",
                       @"C:\Users\me\AppData\Local\Programs\VS Code\Code.exe", @"C:\Program Files\Git\cmd\git.exe");
        Assert.Equal(@"C:\Users\me\AppData\Local\x\y.exe", loc.Locate(@"%LOCALAPPDATA%\x\y.exe"));
        Assert.Equal(@"C:\Users\me\AppData\Local\x\y.exe", loc.Locate("\"%LOCALAPPDATA%\\x\\y.exe\""));
        Assert.Equal(@"C:\Windows\System32\notepad.exe", loc.Locate("notepad"), ignoreCase: true);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", loc.Locate("notepad.exe"));
        Assert.Equal(@"C:\Program Files\Git\cmd\git.exe", loc.Locate("git"), ignoreCase: true);
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\VS Code\Code.exe", loc.Locate("code"));
        Assert.Null(loc.Locate(@"C:\nope.exe"));
        Assert.Null(loc.Locate("nope"));
    }
}

public class IcoWriterTests
{
    [Fact]
    public void WrapsPng()
    {
        var png = File.ReadAllBytes(Path.Combine(TestEnv.RepoRoot(), "package/Assets/Square44x44Logo.png"));
        var ico = IcoWriter.FromPngs(new[] { png });
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1, 0, 44, 44 }, ico[..8]);
        Assert.Equal(6 + 16 + png.Length, ico.Length);
        Assert.Throws<FormatException>(() => IcoWriter.PngSize(new byte[30]));
        // Valid PNG signature without IHDR chunk must also be rejected.
        var fake = new byte[30];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(fake, 0);
        Assert.Throws<FormatException>(() => IcoWriter.PngSize(fake));
    }
}
