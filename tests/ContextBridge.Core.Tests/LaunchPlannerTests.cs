using ContextBridge.Core;

namespace ContextBridge.Core.Tests;

// Mirrors TestMatching / TestLaunch in tests/native/core_tests.cpp so the UI's preview and
// "Try it" behave exactly like the Explorer handler.
public class LaunchPlannerTests
{
    static SelectionInfo Files(params string[] paths) =>
        new(paths.Select(p => new SelectedEntry(p, SelectedKind.File)).ToList());

    static CommandDef Example() => new()
    {
        Id = "ex",
        Target = new TargetDef { ItemTypes = new() { ItemTypes.File }, Extensions = new() { ".txt", ".md" }, MinSelection = 1, MaxSelection = 20 },
        Action = new ActionDef { Program = @"C:\Tools\example.exe", Arguments = "\"%*\"", WorkingDirectory = "%d" },
    };

    [Fact]
    public void MatchesLikeNative()
    {
        var ex = Example();
        Assert.Null(LaunchPlanner.WhyHidden(ex, Files(@"C:\a\b.TXT")));
        Assert.Null(LaunchPlanner.WhyHidden(ex, Files(@"C:\a\b.txt", @"C:\a\c.md")));
        Assert.Contains("拡張子", LaunchPlanner.WhyHidden(ex, Files(@"C:\a\b.txt", @"C:\a\c.png")));
        Assert.Contains("背景", LaunchPlanner.WhyHidden(ex, SelectionInfo.Background(@"C:\work")));
        Assert.Contains("最大", LaunchPlanner.WhyHidden(ex, Files(Enumerable.Range(0, 21).Select(i => $@"C:\f{i}.txt").ToArray())));
        var drive = new SelectionInfo(new[] { new SelectedEntry(@"D:\", SelectedKind.Drive) });
        Assert.Contains("ドライブ", LaunchPlanner.WhyHidden(ex, drive));
        Assert.False(LaunchPlanner.IsRootPath(@"\\\a"));
        Assert.False(LaunchPlanner.IsRootPath(@"\\srv\"));
        Assert.NotNull(LaunchPlanner.WhyHidden(new CommandDef { Id = "b", Target = new TargetDef { ItemTypes = new() { ItemTypes.FolderBackground } } }, SelectionInfo.Background("")));

        // "*" / ".*" in extensions matches any file (like native).
        var anyExt = new CommandDef { Id = "any", Target = new TargetDef { ItemTypes = new() { ItemTypes.File }, Extensions = new() { "*" } } };
        Assert.Null(LaunchPlanner.WhyHidden(anyExt, Files(@"C:\a\b.png")));
    }

    [Fact]
    public void PlansLikeNative()
    {
        var plans = LaunchPlanner.Plan(Example(), Files(@"C:\w\a.txt", @"C:\w\b.md"), TestEnv.Lookup);
        Assert.Single(plans);
        Assert.Equal(@"C:\Tools\example.exe", plans[0].Program);
        Assert.Equal("\"C:\\w\\a.txt\" \"C:\\w\\b.md\"", plans[0].Arguments);
        Assert.Equal(@"C:\w", plans[0].WorkingDirectory);

        var each = new CommandDef
        {
            Id = "each",
            Action = new ActionDef { Program = "y.exe", Arguments = "--open \"%1\"", MultiFileMode = MultiFileModes.Each },
        };
        plans = LaunchPlanner.Plan(each, Files(@"C:\w1\a.txt", @"C:\w2\b.txt"), TestEnv.Lookup);
        Assert.Equal(2, plans.Count);
        Assert.Equal(@"C:\w1", plans[0].WorkingDirectory);
        Assert.Equal("--open \"C:\\w2\\b.txt\"", plans[1].Arguments);
        Assert.Equal(@"C:\w2", plans[1].WorkingDirectory);

        // action.program never expands %1 / %d; quoted workingDirectory is unquoted.
        var sec = new CommandDef { Id = "s", Action = new ActionDef { Program = "%1", WorkingDirectory = "\"%d\"" } };
        var sp = LaunchPlanner.Plan(sec, Files(@"C:\w\evil.exe"), TestEnv.Lookup);
        Assert.Equal("%1", sp[0].Program);
        Assert.Equal(@"C:\w", sp[0].WorkingDirectory);

        var term = new CommandDef
        {
            Id = "t",
            Target = new TargetDef { ItemTypes = new() { ItemTypes.FolderBackground } },
            Action = new ActionDef { Program = @"%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe", Arguments = "-d \"%d\"" },
        };
        plans = LaunchPlanner.Plan(term, SelectionInfo.Background(@"C:\work"), TestEnv.Lookup);
        Assert.Equal(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe", plans[0].Program);
        Assert.Equal("-d \"C:\\work\"", plans[0].Arguments);
    }

    [Theory]
    [InlineData(@"C:\a\b.txt", @"C:\a")]
    [InlineData(@"C:\b.txt", @"C:\")]
    [InlineData(@"C:\", "")]
    [InlineData(@"\\srv\share\x.txt", @"\\srv\share")]
    public void ParentOfLikeNative(string path, string parent) => Assert.Equal(parent, LaunchPlanner.ParentOf(path));

    [Fact]
    public void ContextDirectoryForFolderIsParent()
    {
        var folder = new SelectionInfo(new[] { new SelectedEntry(@"C:\work\sub", SelectedKind.Folder) });
        Assert.Equal(@"C:\work", LaunchPlanner.ContextDirectory(folder));
    }
}
