using ContextBridge.Core.Resources;

namespace ContextBridge.Core;

public enum SelectedKind { File, Folder, Drive }

public sealed record SelectedEntry(string Path, SelectedKind Kind);

/// <summary>A right-click target: either selected items or a folder background.</summary>
public sealed record SelectionInfo(IReadOnlyList<SelectedEntry> Items, string? BackgroundFolder = null)
{
    public bool IsBackground => BackgroundFolder is not null;
    public static SelectionInfo Background(string folder) => new(Array.Empty<SelectedEntry>(), folder);
}

public sealed record PlannedLaunch(string Program, string Arguments, string WorkingDirectory, bool RunAsAdmin, string Window);

/// <summary>
/// C# port of Matches / ContextDirectory / PlanLaunches in src/Shell/core/config.cpp, used by the
/// settings app for the preview and for "Try it". Keep both in sync (see tests).
/// </summary>
public static class LaunchPlanner
{
    static bool IsSep(char c) => c is '\\' or '/';

    public static bool IsRootPath(string p)
    {
        if ((p.Length == 2 || p.Length == 3) && p[1] == ':' && char.IsAsciiLetter(p[0]))
            return p.Length == 2 || IsSep(p[2]);
        if (p.Length > 2 && IsSep(p[0]) && IsSep(p[1]))
        {
            var rest = p[2..].TrimEnd('\\', '/');
            if (rest.Length == 0 || IsSep(rest[0]) || rest[0] is '?' or '.') return false;
            int sepPos = -1;
            for (int i = 0; i < rest.Length; i++)
            {
                if (IsSep(rest[i]))
                {
                    if (sepPos >= 0) return false;
                    sepPos = i;
                }
            }
            return sepPos > 0 && sepPos + 1 < rest.Length;
        }
        return false;
    }

    public static string ParentOf(string path)
    {
        if (path.Length == 0 || IsRootPath(path)) return "";
        int end = path.Length;
        while (end > 0 && IsSep(path[end - 1])) end--;
        int pos = end;
        while (pos > 0 && !IsSep(path[pos - 1])) pos--;
        if (pos == 0) return end >= 2 && path[1] == ':' ? path[..2] + "\\" : "";
        string parent = path[..pos];
        if (IsRootPath(parent)) return parent.Length >= 2 && parent[1] == ':' ? parent : parent.TrimEnd('\\', '/');
        while (parent.Length > 1 && IsSep(parent[^1])) parent = parent[..^1];
        return parent;
    }

    static string FileName(string path)
    {
        int end = path.Length;
        while (end > 0 && IsSep(path[end - 1])) end--;
        int start = end;
        while (start > 0 && !IsSep(path[start - 1]) && path[start - 1] != ':') start--;
        return path[start..end];
    }

    static string DirectoryForItem(SelectedEntry item)
    {
        if (item.Kind == SelectedKind.Drive) return item.Path;
        var p = ParentOf(item.Path);
        return p.Length == 0 ? item.Path : p;
    }

    public static string ContextDirectory(SelectionInfo s)
    {
        if (s.IsBackground) return s.BackgroundFolder!;
        if (s.Items.Count == 0) return "";
        return DirectoryForItem(s.Items[0]);
    }

    static bool MatchesExtensions(IReadOnlyList<string>? exts, string fileName)
    {
        if (exts is null || exts.Count == 0) return true;
        var normalized = new List<string>(exts.Count);
        foreach (var raw in exts)
        {
            var w = (raw ?? "").Trim();
            if (w.Length == 0) continue;
            if (w is "*" or ".*") return true;
            normalized.Add(w.StartsWith('.') ? w : "." + w);
        }
        if (normalized.Count == 0) return true;
        return normalized.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Null when the command would be shown for this selection, otherwise why not (ignores "enabled").</summary>
    public static string? WhyHidden(CommandDef c, SelectionInfo s)
    {
        var t = c.Target;
        if (s.IsBackground)
        {
            if (!t.Includes(ItemTypes.FolderBackground)) return Strings.Why_BackgroundNotTarget;
            if (string.IsNullOrEmpty(s.BackgroundFolder)) return Strings.Why_NoBackgroundPath;
            if (t.MinSelection > 1) return Strings.Why_BackgroundMin;
            return null;
        }
        int n = s.Items.Count;
        if (n == 0) return Strings.Why_NothingSelected;
        if (n < t.MinSelection) return string.Format(Strings.Why_TooFew, n, t.MinSelection);
        if (t.MaxSelection != 0 && n > t.MaxSelection) return string.Format(Strings.Why_TooMany, n, t.MaxSelection);
        foreach (var item in s.Items)
        {
            if (string.IsNullOrEmpty(item.Path)) return Strings.Why_NoPath;
            switch (item.Kind)
            {
                case SelectedKind.File:
                    if (!t.Includes(ItemTypes.File)) return Strings.Why_FileNotTarget;
                    if (t.Extensions is { Count: > 0 } exts)
                    {
                        string name = FileName(item.Path);
                        if (!MatchesExtensions(exts, name))
                            return string.Format(Strings.Why_Extension, name, string.Join(" ", exts));
                    }
                    break;
                case SelectedKind.Folder:
                    if (!t.Includes(ItemTypes.Folder)) return Strings.Why_FolderNotTarget;
                    break;
                case SelectedKind.Drive:
                    if (!t.Includes(ItemTypes.Drive)) return Strings.Why_DriveNotTarget;
                    break;
            }
        }
        return null;
    }

    static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    public static List<PlannedLaunch> Plan(CommandDef c, SelectionInfo s, Func<string, string?> env)
    {
        var batches = new List<(IReadOnlyList<string> Paths, string Dir)>();
        if (s.IsBackground) batches.Add((new[] { s.BackgroundFolder! }, s.BackgroundFolder!));
        else if (c.Action.MultiFileMode == MultiFileModes.Each)
            batches.AddRange(s.Items.Select(i => ((IReadOnlyList<string>)new[] { i.Path }, DirectoryForItem(i))));
        else
            batches.Add((s.Items.Select(i => i.Path).ToList(), ContextDirectory(s)));

        var result = new List<PlannedLaunch>();
        foreach (var (paths, dir) in batches)
        {
            var ctx = new Template.Context(paths, dir);
            string program = Template.ExpandProgram(c.Action.Program, env);
            string args = Template.Expand(c.Action.Arguments, ctx, env);
            string wdTemplate = (c.Action.WorkingDirectory ?? "").Trim();
            string wd = wdTemplate.Length == 0 ? dir : Unquote(Template.Expand(wdTemplate, ctx, env).Trim());
            while (wd.Length > 3 && IsSep(wd[^1])) wd = wd[..^1];
            result.Add(new PlannedLaunch(program, args, wd, c.Action.RunAsAdmin, c.Action.Window));
        }
        return result;
    }
}
