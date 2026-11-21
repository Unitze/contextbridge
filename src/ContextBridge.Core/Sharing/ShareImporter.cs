using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

using ContextBridge.Core.Resources;

namespace ContextBridge.Core.Sharing;

public enum FindingLevel { Info, Warning, Blocking }

public sealed record Finding(FindingLevel Level, string Message);

public sealed class ImportItem : INotifyPropertyChanged
{
    public required CommandDef Command { get; init; }
    public required string OriginalId { get; init; }
    /// <summary>Program after environment expansion, for display.</summary>
    public required string ResolvedProgram { get; init; }
    public bool ProgramFound { get; init; }
    public List<Finding> Findings { get; } = new();
    bool _include = true;

    /// <summary>Selected for import in the confirmation dialog.</summary>
    public bool Include
    {
        get => _include;
        set
        {
            if (_include == value) return;
            _include = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Include)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayTitle => Template.DisplayTitle(Command);
    public string WorkingDirectoryDisplay => string.IsNullOrWhiteSpace(Command.Action.WorkingDirectory) ? Strings.Imp_WorkingDirDefault : Command.Action.WorkingDirectory!;
    public string IconDisplay => Command.Icon.Source switch
    {
        IconSources.None => Strings.Imp_IconNone,
        IconSources.File => string.IsNullOrWhiteSpace(Command.Icon.Path) ? Strings.Imp_IconUnset : $"{Command.Icon.Path} ({Command.Icon.Index})",
        _ => string.IsNullOrWhiteSpace(Command.Icon.Path) ? Strings.Imp_IconFromProgram : $"{Command.Icon.Path} ({Command.Icon.Index})",
    };

    /// <summary>Human-readable description of where the command appears.</summary>
    public string TargetSummary
    {
        get
        {
            var t = Command.Target;
            var parts = new List<string>();
            bool all = t.ItemTypes.Contains(ItemTypes.All);
            if (all || t.ItemTypes.Contains(ItemTypes.File))
                parts.Add(t.Extensions is { Count: > 0 } e ? string.Format(Strings.Imp_TargetFiles, string.Join(" ", e)) : Strings.Imp_TargetAllFiles);
            if (all || t.ItemTypes.Contains(ItemTypes.Folder)) parts.Add(Strings.Imp_TargetFolder);
            if (all || t.ItemTypes.Contains(ItemTypes.FolderBackground)) parts.Add(Strings.Imp_TargetBackground);
            if (all || t.ItemTypes.Contains(ItemTypes.Drive)) parts.Add(Strings.Imp_TargetDrive);
            string sel = t.MaxSelection == 0 ? string.Format(Strings.Imp_SelAtLeast, t.MinSelection) : string.Format(Strings.Imp_SelRange, t.MinSelection, t.MaxSelection);
            string mode = Command.Action.MultiFileMode == MultiFileModes.Each ? Strings.Imp_ModeEach : Strings.Imp_ModeSingle;
            return string.Format(Strings.Imp_TargetSummary, string.Join(" / ", parts), sel, mode);
        }
    }
}

public sealed class ImportPlan
{
    public List<ImportItem> Items { get; } = new();
    public List<GroupDef> NewGroups { get; } = new();
    public string? Description { get; init; }
    public bool HasUnresolvedPrograms => Items.Any(i => i.Include && !i.ProgramFound);

    /// <summary>Applies the selected items to <paramref name="target"/>. Call only after user approval.</summary>
    public void ApplyTo(ConfigFile target, string sourceName)
    {
        var used = Items.Where(i => i.Include).Select(i => i.Command.Group).Where(g => g is not null).ToHashSet();
        foreach (var g in NewGroups.Where(g => used.Contains(g.Id))) target.Groups.Add(g.Clone());
        foreach (var item in Items.Where(i => i.Include))
        {
            var c = item.Command.Clone();
            c.Origin = new OriginDef { Type = "import", Source = sourceName };
            target.Commands.Add(c);
        }
    }
}

/// <summary>
/// Builds the import confirmation: nothing is written until the user has seen each
/// command's program and arguments. Presets go through the same path.
/// </summary>
public static class ShareImporter
{
    // Base names (without extension) so extensionless references like "mshta" or "wscript" cannot bypass checks.
    static readonly HashSet<string> ScriptHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32",
        "bash", "wsl", "python", "pythonw", "node", "msiexec", "certutil", "bitsadmin",
        "curl", "wget", "reg", "schtasks", "sc", "net", "net1", "forfiles", "pcalua",
    };

    static readonly HashSet<string> ScriptProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".py", ".pyw",
        ".lnk", ".scr", ".pif", ".com", ".msi", ".msp",
    };

    static readonly Regex UrlPattern = new(@"\b(?:https?|ftp)://", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ScriptFilePattern = new(@"\.(?:ps1|bat|cmd|vbs|vbe|js|jse|wsf|wsh|hta|py|pyw)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ProgramTemplateTokenPattern = new(@"%(?:1|\*|d)(?!\w)", RegexOptions.Compiled);

    public static ShareDocument Parse(string json)
    {
        var docOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };
        using var doc = JsonDocument.Parse(json, docOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException(Strings.Imp_RootNotObject);
        bool isShare = root.TryGetProperty("kind", out var kind) &&
                       kind.ValueKind == JsonValueKind.String &&
                       kind.GetString() == ShareDocument.KindValue;
        bool hasVersion = root.TryGetProperty("version", out var verProp) && verProp.ValueKind == JsonValueKind.Number;
        bool isConfig = root.TryGetProperty("commands", out _) && hasVersion;
        if (!isShare && !isConfig) throw new FormatException(Strings.Imp_NotContextBridge);
        if (!hasVersion) throw new FormatException(Strings.Imp_NoVersion);
        var share = JsonSerializer.Deserialize<ShareDocument>(json, ConfigSerializer.Options)
                    ?? throw new FormatException(Strings.Imp_EmptyFile);
        if (share.Version != ConfigFile.CurrentVersion) throw new FormatException(string.Format(Strings.Val_UnsupportedVersion, share.Version));
        share.Groups ??= new();
        share.Commands ??= new();
        try
        {
            ConfigSerializer.Normalize(share.Groups, share.Commands);
        }
        catch (JsonException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
        return share;
    }

    public static ImportPlan Plan(ShareDocument doc, ConfigFile existing, Func<string, bool> programExists,
                                  Func<string, string?>? env = null)
    {
        env ??= Template.ProcessEnv;
        // Validate the incoming definitions with the same rules the handler applies.
        var asConfig = new ConfigFile { Groups = doc.Groups, Commands = doc.Commands };
        var errors = ConfigValidator.Validate(asConfig).Where(i => i.Severity == Severity.Error).ToList();
        if (errors.Count > 0) throw new FormatException(string.Join("\n", errors.Select(e => e.Message)));

        var plan = new ImportPlan { Description = doc.Description };

        // Groups: reuse an existing group with the same id+title, otherwise add (renaming on id clash).
        var groupMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedGroupIds = new HashSet<string>(existing.Groups.Select(g => g.Id), StringComparer.Ordinal);
        foreach (var g in doc.Groups)
        {
            var same = existing.Groups.FirstOrDefault(e => e.Id == g.Id && e.Title == g.Title);
            if (same is not null)
            {
                groupMap[g.Id] = same.Id;
                continue;
            }
            var ng = g.Clone();
            ng.Id = Legacy.LegacyScanner.UniqueId(g.Id, usedGroupIds);
            groupMap[g.Id] = ng.Id;
            plan.NewGroups.Add(ng);
        }

        var usedIds = new HashSet<string>(existing.Commands.Select(c => c.Id), StringComparer.Ordinal);
        foreach (var src in doc.Commands)
        {
            var c = src.Clone();
            c.Origin = null;
            c.Id = Legacy.LegacyScanner.UniqueId(src.Id, usedIds);
            if (c.Group is not null) c.Group = groupMap.TryGetValue(c.Group, out var mapped) ? mapped : null;

            string resolved = Template.ExpandProgram(c.Action.Program, env);
            bool found = programExists(c.Action.Program);
            var item = new ImportItem { Command = c, OriginalId = src.Id, ResolvedProgram = resolved, ProgramFound = found };
            Analyze(item, env);
            plan.Items.Add(item);
        }
        return plan;
    }

    static bool IsUncPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim().Trim('"');
        return p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal);
    }

    static void Analyze(ImportItem item, Func<string, string?> env)
    {
        var c = item.Command;
        if (!item.ProgramFound)
        {
            bool userSpecific = Regex.IsMatch(c.Action.Program, @"^[A-Za-z]:\\Users\\[^\\%]+\\", RegexOptions.IgnoreCase);
            item.Findings.Add(new(FindingLevel.Blocking, userSpecific
                ? Strings.Find_UserSpecificPath
                : Strings.Find_ProgramNotFound));
        }
        string exe = Path.GetFileName(item.ResolvedProgram.Replace('/', '\\').Split('\\').Last());
        string exeBase = Path.GetFileNameWithoutExtension(exe);
        string exeExt = Path.GetExtension(exe);
        if (ScriptHostNames.Contains(exeBase))
            item.Findings.Add(new(FindingLevel.Warning, string.Format(Strings.Find_ScriptHost, exe)));
        if (ScriptProgramExtensions.Contains(exeExt))
            item.Findings.Add(new(FindingLevel.Warning, string.Format(Strings.Find_ScriptProgram, exeExt)));
        if (ProgramTemplateTokenPattern.IsMatch(c.Action.Program))
            item.Findings.Add(new(FindingLevel.Warning, Strings.Find_TemplateInProgram));
        if (IsUncPath(c.Action.Program) || IsUncPath(item.ResolvedProgram))
            item.Findings.Add(new(FindingLevel.Warning, Strings.Find_UncProgram));
        if (IsUncPath(c.Action.WorkingDirectory) ||
            (!string.IsNullOrWhiteSpace(c.Action.WorkingDirectory) && IsUncPath(Template.ExpandEnvOnly(c.Action.WorkingDirectory!, env))))
            item.Findings.Add(new(FindingLevel.Warning, Strings.Find_UncWorkingDir));
        if (c.Icon.Source != IconSources.None &&
            (IsUncPath(c.Icon.Path) || (!string.IsNullOrWhiteSpace(c.Icon.Path) && IsUncPath(Template.ExpandEnvOnly(c.Icon.Path!, env)))))
            item.Findings.Add(new(FindingLevel.Warning, Strings.Find_UncIcon));
        if (UrlPattern.IsMatch(c.Action.Arguments) || UrlPattern.IsMatch(c.Action.Program))
            item.Findings.Add(new(FindingLevel.Warning, Strings.Find_Url));
        if (ScriptFilePattern.IsMatch(c.Action.Arguments))
            item.Findings.Add(new(FindingLevel.Warning, Strings.Find_ScriptFile));
        if (c.Action.RunAsAdmin)
            item.Findings.Add(new(FindingLevel.Warning, Strings.Find_RunAsAdmin));
        if (c.Action.Window == WindowModes.Hidden)
            item.Findings.Add(new(FindingLevel.Warning, Strings.Find_Hidden));
    }
}
