using ContextBridge.Core.Resources;

namespace ContextBridge.Core;

public enum Severity { Warning, Error }

public sealed record ValidationIssue(Severity Severity, string? CommandId, string Message);

/// <summary>
/// Mirrors the native loader's rules (an Error here means the handler would reject the whole
/// file and hide every item) and adds UI-only warnings.
/// </summary>
public static class ConfigValidator
{
    /// <summary>No hard limit, but warn when the flyout gets long.</summary>
    public const int CommandCountWarningThreshold = 30;
    public const int MaxSelectionLimit = 1_000_000;

    public static List<ValidationIssue> Validate(ConfigFile cfg, Func<string, bool>? programExists = null)
    {
        var issues = new List<ValidationIssue>();
        void Err(string? id, string m) => issues.Add(new(Severity.Error, id, m));
        void Warn(string? id, string m) => issues.Add(new(Severity.Warning, id, m));

        if (cfg.Version != ConfigFile.CurrentVersion) Err(null, string.Format(Strings.Val_UnsupportedVersion, cfg.Version));

        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in cfg.Groups)
        {
            if (g is null) { Err(null, Strings.Val_NullGroup); continue; }
            if (string.IsNullOrWhiteSpace(g.Id)) Err(null, Strings.Val_GroupNoId);
            else if (!groupIds.Add(g.Id)) Err(null, string.Format(Strings.Val_DuplicateGroupId, g.Id));
            ValidateIcon(g.Icon, null, Err);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        int enabled = 0;
        foreach (var c in cfg.Commands)
        {
            if (c is null) { Err(null, Strings.Val_NullCommand); continue; }
            string label = string.IsNullOrEmpty(c.Title) ? c.Id : c.Title;
            if (string.IsNullOrWhiteSpace(c.Id)) Err(null, Strings.Val_CommandNoId);
            else if (!ids.Add(c.Id)) Err(c.Id, string.Format(Strings.Val_DuplicateCommandId, c.Id));

            if (string.IsNullOrWhiteSpace(c.Action.Program)) Err(c.Id, string.Format(Strings.Val_NoProgram, label));
            if (c.Target.ItemTypes.Count == 0) Err(c.Id, string.Format(Strings.Val_NoItemTypes, label));
            foreach (var t in c.Target.ItemTypes)
            {
                if (Array.IndexOf(ItemTypes.Known, t) < 0) Err(c.Id, string.Format(Strings.Val_UnknownItemType, label, t));
            }
            if (c.Target.MinSelection < 0 || c.Target.MaxSelection < 0)
                Err(c.Id, string.Format(Strings.Val_NegativeSelection, label));
            if (c.Target.MinSelection > MaxSelectionLimit || c.Target.MaxSelection > MaxSelectionLimit)
                Err(c.Id, string.Format(Strings.Val_SelectionTooLarge, label, MaxSelectionLimit));
            if (c.Target.MaxSelection != 0 && c.Target.MaxSelection < c.Target.MinSelection)
                Err(c.Id, string.Format(Strings.Val_MaxBelowMin, label));
            if (c.Action.MultiFileMode is not (MultiFileModes.Single or MultiFileModes.Each))
                Err(c.Id, string.Format(Strings.Val_UnknownMultiFileMode, label, c.Action.MultiFileMode));
            if (Array.IndexOf(WindowModes.Known, c.Action.Window) < 0)
                Err(c.Id, string.Format(Strings.Val_UnknownWindow, label, c.Action.Window));
            ValidateIcon(c.Icon, c.Id, Err);

            if (!c.Enabled) continue;
            enabled++;
            if (!string.IsNullOrEmpty(c.Group) && !groupIds.Contains(c.Group))
                Warn(c.Id, string.Format(Strings.Val_MissingGroup, label, c.Group));
            bool backgroundOnly = c.Target.ItemTypes.Count == 1 && c.Target.ItemTypes[0] == ItemTypes.FolderBackground;
            if (backgroundOnly && c.Target.MinSelection > 1)
                Warn(c.Id, string.Format(Strings.Val_BackgroundMin, label));
            if (programExists is not null && !string.IsNullOrWhiteSpace(c.Action.Program) &&
                !programExists(c.Action.Program))
                Warn(c.Id, string.Format(Strings.Val_ProgramNotFound, label, c.Action.Program));
            string args = c.Action.Arguments ?? "";
            if (!backgroundOnly && c.Action.MultiFileMode == MultiFileModes.Single && (c.Target.MaxSelection is 0 or > 1) &&
                !args.Contains("%*") && args.Contains("%1"))
                Warn(c.Id, string.Format(Strings.Val_OnlyFirstFile, label));
        }

        if (enabled > CommandCountWarningThreshold)
            Warn(null, string.Format(Strings.Val_TooManyCommands, enabled, CommandCountWarningThreshold));
        return issues;
    }

    static void ValidateIcon(IconDef? icon, string? id, Action<string?, string> err)
    {
        if (icon is null) return;
        if (icon.Source is not (IconSources.Program or IconSources.File or IconSources.None))
            err(id, string.Format(Strings.Val_UnknownIconSource, icon.Source));
    }

    public static bool HasErrors(IEnumerable<ValidationIssue> issues) => issues.Any(i => i.Severity == Severity.Error);
}
