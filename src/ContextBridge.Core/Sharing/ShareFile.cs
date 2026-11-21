using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContextBridge.Core.Sharing;

/// <summary>
/// Share file: a subset of commands.json plus a "kind" marker. The recommended
/// file name is "*.contextbridge.json" so it stays a plain .json file while being recognizable.
/// A full commands.json is also accepted on import.
/// </summary>
public sealed class ShareDocument
{
    public const string KindValue = "contextbridge-share";
    public const string FileSuffix = ".contextbridge.json";

    public string Kind { get; set; } = KindValue;
    public int Version { get; set; } = ConfigFile.CurrentVersion;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    public List<GroupDef> Groups { get; set; } = new();
    public List<CommandDef> Commands { get; set; } = new();
}

public static class ShareExporter
{
    /// <summary>
    /// Exports the given commands (and the groups they use). Origin metadata is dropped, and
    /// with <paramref name="generalizePaths"/> user-specific folders are rewritten to environment
    /// variables (C:\Users\me\AppData\Local\... → %LOCALAPPDATA%\...).
    /// </summary>
    public static ShareDocument Export(ConfigFile cfg, IEnumerable<string> commandIds, bool generalizePaths = true,
                                       IReadOnlyList<(string Variable, string Path)>? userFolders = null)
    {
        var wanted = new HashSet<string>(commandIds, StringComparer.Ordinal);
        var doc = new ShareDocument();
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        userFolders ??= DefaultUserFolders();
        foreach (var c in cfg.Commands.Where(c => wanted.Contains(c.Id)))
        {
            var copy = c.Clone();
            copy.Origin = null;
            copy.Extra = null;
            if (generalizePaths)
            {
                copy.Action.Program = Generalize(copy.Action.Program, userFolders);
                copy.Action.Arguments = Generalize(copy.Action.Arguments, userFolders);
                if (copy.Action.WorkingDirectory is not null)
                    copy.Action.WorkingDirectory = Generalize(copy.Action.WorkingDirectory, userFolders);
                if (copy.Icon.Path is not null) copy.Icon.Path = Generalize(copy.Icon.Path, userFolders);
            }
            doc.Commands.Add(copy);
            if (!string.IsNullOrEmpty(copy.Group)) groupIds.Add(copy.Group);
        }
        doc.Groups = cfg.Groups.Where(g => groupIds.Contains(g.Id)).Select(g => g.Clone()).ToList();
        foreach (var g in doc.Groups) g.Extra = null;
        return doc;
    }

    public static string Serialize(ShareDocument doc) => JsonSerializer.Serialize(doc, ConfigSerializer.Options) + "\n";

    /// <summary>Most specific folders first so %LOCALAPPDATA% wins over %USERPROFILE%.</summary>
    public static IReadOnlyList<(string Variable, string Path)> DefaultUserFolders()
    {
        var list = new List<(string, string)>();
        void Add(string v)
        {
            var p = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrEmpty(p)) list.Add((v, p.TrimEnd('\\')));
        }
        Add("LOCALAPPDATA");
        Add("APPDATA");
        Add("USERPROFILE");
        Add("ProgramFiles");
        Add("ProgramFiles(x86)");
        return list.OrderByDescending(x => x.Item2.Length).ToList();
    }

    public static string Generalize(string s, IReadOnlyList<(string Variable, string Path)> folders)
    {
        foreach (var (variable, path) in folders.OrderByDescending(f => f.Path.Length))
        {
            if (string.IsNullOrEmpty(path)) continue;
            int idx = s.IndexOf(path, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                int end = idx + path.Length;
                bool boundary = end == s.Length || s[end] is '\\' or '/' or '"' or ' ' or ',';
                if (boundary)
                {
                    s = s[..idx] + "%" + variable + "%" + s[end..];
                    idx = s.IndexOf(path, idx + variable.Length + 2, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    idx = s.IndexOf(path, end, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        return s;
    }
}
