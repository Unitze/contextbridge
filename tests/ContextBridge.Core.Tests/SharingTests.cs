using ContextBridge.Core;
using ContextBridge.Core.Sharing;

namespace ContextBridge.Core.Tests;

public class SharingTests
{
    static ConfigFile Existing()
    {
        var cfg = new ConfigFile();
        cfg.Groups.Add(new GroupDef { Id = "editors", Title = "エディタ" });
        cfg.Commands.Add(new CommandDef
        {
            Id = "code",
            Title = "VS Code で開く",
            Group = "editors",
            Action = new ActionDef { Program = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", Arguments = "%*" },
            Origin = new OriginDef { Type = "legacy", Keys = new() { @"*\shell\VSCode" }, Command = "x" },
        });
        cfg.Commands.Add(new CommandDef { Id = "other", Action = new ActionDef { Program = "x.exe" } });
        return cfg;
    }

    static readonly (string, string)[] Folders =
    {
        ("LOCALAPPDATA", @"C:\Users\me\AppData\Local"),
        ("USERPROFILE", @"C:\Users\me"),
    };

    [Fact]
    public void ExportsSubsetWithGeneralizedPaths()
    {
        var doc = ShareExporter.Export(Existing(), new[] { "code" }, true, Folders);
        Assert.Single(doc.Commands);
        Assert.Single(doc.Groups);
        Assert.Null(doc.Commands[0].Origin);
        Assert.Equal(@"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe", doc.Commands[0].Action.Program);
        string json = ShareExporter.Serialize(doc);
        Assert.Contains("\"kind\": \"contextbridge-share\"", json);
    }

    [Fact]
    public void GeneralizeRespectsPathBoundaries()
    {
        Assert.Equal(@"C:\Users\meow\x", ShareExporter.Generalize(@"C:\Users\meow\x", Folders));
        Assert.Equal("\"%USERPROFILE%\" a", ShareExporter.Generalize("\"C:\\Users\\me\" a", Folders));
    }

    [Fact]
    public void ImportPlanRenamesConflictsAndFlagsRisks()
    {
        var doc = ShareExporter.Export(Existing(), new[] { "code" }, true, Folders);
        doc.Commands.Add(new CommandDef
        {
            Id = "dl",
            Title = "download",
            Action = new ActionDef { Program = "powershell.exe", Arguments = "-c iwr https://example.invalid/x.ps1", RunAsAdmin = true },
        });
        doc.Commands.Add(new CommandDef
        {
            Id = "foreign",
            Action = new ActionDef { Program = @"C:\Users\someone\tool.exe" },
        });
        var json = ShareExporter.Serialize(doc);

        var parsed = ShareImporter.Parse(json);
        var existing = Existing();
        var plan = ShareImporter.Plan(parsed, existing,
            p => !p.Contains("someone"), TestEnv.Lookup);

        Assert.Equal(3, plan.Items.Count);
        Assert.Equal("code-2", plan.Items[0].Command.Id);           // id conflict renamed
        Assert.Equal("editors", plan.Items[0].Command.Group);        // same group reused
        Assert.Empty(plan.NewGroups);
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", plan.Items[0].ResolvedProgram);

        var dl = plan.Items[1];
        Assert.Contains(dl.Findings, f => f.Message.Contains("URL"));
        Assert.Contains(dl.Findings, f => f.Message.Contains("管理者"));
        Assert.Contains(dl.Findings, f => f.Message.Contains("powershell.exe"));
        Assert.Contains(dl.Findings, f => f.Message.Contains("スクリプト"));

        var foreign = plan.Items[2];
        Assert.False(foreign.ProgramFound);
        Assert.Contains(foreign.Findings, f => f.Level == FindingLevel.Blocking && f.Message.Contains("他のユーザー名"));
        Assert.True(plan.HasUnresolvedPrograms);

        foreign.Include = false;
        Assert.False(plan.HasUnresolvedPrograms);
        plan.ApplyTo(existing, "test.contextbridge.json");
        Assert.Equal(4, existing.Commands.Count);
        Assert.All(existing.Commands.Skip(2), c => Assert.Equal("import", c.Origin!.Type));
        Assert.False(ConfigValidator.HasErrors(ConfigValidator.Validate(existing)));
    }

    [Fact]
    public void GroupWithSameIdButDifferentTitleIsAddedUnderNewId()
    {
        var doc = new ShareDocument();
        doc.Groups.Add(new GroupDef { Id = "editors", Title = "Editors" });
        doc.Commands.Add(new CommandDef { Id = "n", Group = "editors", Action = new ActionDef { Program = "n.exe" } });
        var plan = ShareImporter.Plan(doc, Existing(), _ => true, TestEnv.Lookup);
        Assert.Single(plan.NewGroups);
        Assert.Equal("editors-2", plan.NewGroups[0].Id);
        Assert.Equal("editors-2", plan.Items[0].Command.Group);
    }

    [Fact]
    public void IncludeRaisesChangeNotification()
    {
        var doc = new ShareDocument();
        doc.Commands.Add(new CommandDef { Id = "n", Action = new ActionDef { Program = "n.exe" } });
        var item = ShareImporter.Plan(doc, new ConfigFile(), _ => true, TestEnv.Lookup).Items[0];
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        item.Include = false;
        item.Include = false;  // no-op: no second notification
        item.Include = true;
        Assert.Equal(new[] { "Include", "Include" }, changed);
    }

    [Fact]
    public void RejectsForeignOrInvalidFiles()
    {
        Assert.Throws<FormatException>(() => ShareImporter.Parse("{\"hello\": 1}"));
        Assert.Throws<FormatException>(() => ShareImporter.Parse("{\"kind\": 123, \"version\": 1}"));
        Assert.Throws<FormatException>(() => ShareImporter.Parse("{\"kind\":\"contextbridge-share\",\"commands\":[]}"));
        Assert.Throws<FormatException>(() => ShareImporter.Parse("[]"));
        Assert.Throws<FormatException>(() => ShareImporter.Parse("{\"kind\":\"contextbridge-share\",\"version\":9,\"commands\":[]}"));
        Assert.Throws<FormatException>(() => ShareImporter.Parse("{\"kind\":\"contextbridge-share\",\"version\":1,\"commands\":[null]}"));
        var nullAct = ShareImporter.Parse("{\"kind\":\"contextbridge-share\",\"version\":1,\"commands\":[{\"id\":\"a\",\"action\":null}]}");
        Assert.Throws<FormatException>(() => ShareImporter.Plan(nullAct, new ConfigFile(), _ => true));
        var bad = ShareImporter.Parse("{\"kind\":\"contextbridge-share\",\"version\":1,\"commands\":[{\"id\":\"a\",\"action\":{\"program\":\"\"}}]}");
        Assert.Throws<FormatException>(() => ShareImporter.Plan(bad, new ConfigFile(), _ => true));
    }

    [Fact]
    public void FlagsExtensionlessScriptHostsScriptProgramsUncAndTemplatePrograms()
    {
        var doc = new ShareDocument();
        doc.Commands.Add(new CommandDef { Id = "m", Action = new ActionDef { Program = "mshta", Arguments = "x" } });
        doc.Commands.Add(new CommandDef { Id = "s", Action = new ActionDef { Program = @"C:\tools\run.ps1" } });
        doc.Commands.Add(new CommandDef
        {
            Id = "u",
            Action = new ActionDef { Program = @"\\evil\share\a.exe", WorkingDirectory = @"\\evil\share" },
            Icon = new IconDef { Source = IconSources.File, Path = @"\\evil\share\x.ico" },
        });
        doc.Commands.Add(new CommandDef { Id = "t", Action = new ActionDef { Program = "%d\\tool.exe" } });

        var plan = ShareImporter.Plan(doc, new ConfigFile(), _ => true, TestEnv.Lookup);
        Assert.Contains(plan.Items[0].Findings, f => f.Message.Contains("スクリプトを実行できる"));
        Assert.Contains(plan.Items[1].Findings, f => f.Message.Contains(".ps1"));
        Assert.Equal(3, plan.Items[2].Findings.Count(f => f.Message.Contains("UNC")));
        Assert.Contains(plan.Items[3].Findings, f => f.Message.Contains("%1 / %* / %d"));
    }

    [Fact]
    public void AcceptsFullConfigFile()
    {
        var json = ConfigSerializer.Serialize(Existing());
        var doc = ShareImporter.Parse(json);
        Assert.Equal(2, doc.Commands.Count);
    }

    [Fact]
    public void PresetsInRepositoryAreValid()
    {
        var dir = Path.Combine(TestEnv.RepoRoot(), "presets");
        var files = Directory.GetFiles(dir, "*" + ShareDocument.FileSuffix, SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var doc = ShareImporter.Parse(File.ReadAllText(f));
            Assert.False(string.IsNullOrWhiteSpace(doc.Description), $"{f}: description required");
            var plan = ShareImporter.Plan(doc, new ConfigFile(), _ => true, TestEnv.Lookup);
            Assert.NotEmpty(plan.Items);
            // Review rules (presets/README.md): no script hosts, scripts, URLs, UNC paths, or hardcoded user paths.
            foreach (var item in plan.Items)
            {
                Assert.DoesNotMatch(@"(?i)^[A-Za-z]:\\Users\\[^\\%]+\\", item.Command.Action.Program);
                Assert.DoesNotContain(item.Findings, x =>
                    x.Message.Contains("URL") ||
                    x.Message.Contains("スクリプト") ||
                    x.Message.Contains("UNC") ||
                    x.Message.Contains("%1 / %* / %d"));
            }
        }
    }
}
