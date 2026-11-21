using System.Text.RegularExpressions;

using ContextBridge.Core.Resources;

namespace ContextBridge.Core.Legacy;

/// <summary>Read-only view of HKEY_CLASSES_ROOT (merged HKCU/HKLM). Paths are relative to HKCR.</summary>
public interface IRegistryView
{
    IEnumerable<string> SubKeyNames(string path);
    bool KeyExists(string path);
    bool HasValue(string path, string name);
    /// <summary>String value without environment expansion; null when absent or not a string. name null = default value.</summary>
    string? GetString(string path, string? name);
}

public enum CandidateStatus
{
    /// <summary>Can be imported and has not been yet.</summary>
    New,
    /// <summary>Already imported and unchanged.</summary>
    Imported,
    /// <summary>Imported earlier but the registry command changed since.</summary>
    Changed,
    /// <summary>Cannot be represented (COM handler, cascading menu, Shift-only, ...).</summary>
    Unsupported,
}

public sealed class LegacyCandidate
{
    public required string Verb { get; init; }
    public List<string> Keys { get; } = new();
    public required string Title { get; init; }
    public required string RawCommand { get; init; }
    public CommandDef? Proposed { get; set; }
    public CandidateStatus Status { get; set; }
    public string? UnsupportedReason { get; init; }
    /// <summary>Non-null when the default exclusion list matches; the UI leaves it unchecked.</summary>
    public string? ExcludedReason { get; set; }
    /// <summary>Id of the existing command this candidate corresponds to (Imported/Changed).</summary>
    public string? ExistingId { get; set; }
}

public sealed record ScanResult(List<LegacyCandidate> Candidates, List<CommandDef> RemovedFromRegistry);

public sealed class LegacyScanner
{
    readonly IRegistryView _reg;
    readonly Func<string, string?> _resolveIndirect;

    /// <param name="resolveIndirect">Resolves "@dll,-id" strings (SHLoadIndirectString). Return null on failure.</param>
    public LegacyScanner(IRegistryView reg, Func<string, string?> resolveIndirect)
    {
        _reg = reg;
        _resolveIndirect = resolveIndirect;
    }

    sealed record Source(string ShellKey, string ItemType, string? Extension);

    IEnumerable<Source> Sources()
    {
        yield return new(@"*\shell", ItemTypes.File, null);
        yield return new(@"AllFilesystemObjects\shell", ItemTypes.File, null);
        yield return new(@"AllFilesystemObjects\shell", ItemTypes.Folder, null);
        yield return new(@"Directory\shell", ItemTypes.Folder, null);
        yield return new(@"Directory\Background\shell", ItemTypes.FolderBackground, null);
        yield return new(@"Drive\shell", ItemTypes.Drive, null);
        foreach (var name in _reg.SubKeyNames(""))
        {
            if (name.Length > 1 && name[0] == '.' && _reg.KeyExists(name + @"\shell"))
                yield return new(name + @"\shell", ItemTypes.File, name.ToLowerInvariant());
        }
        foreach (var name in _reg.SubKeyNames("SystemFileAssociations"))
        {
            string key = @"SystemFileAssociations\" + name + @"\shell";
            if (name.Length > 1 && name[0] == '.' && _reg.KeyExists(key))
                yield return new(key, ItemTypes.File, name.ToLowerInvariant());
        }
    }

    public ScanResult Scan(ConfigFile existing)
    {
        var byKey = new Dictionary<string, LegacyCandidate>(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<LegacyCandidate, (HashSet<string> types, HashSet<string>? exts)>();
        var order = new List<LegacyCandidate>();

        foreach (var src in Sources())
        {
            foreach (var verb in SafeSubKeys(src.ShellKey))
            {
                string verbKey = src.ShellKey + @"\" + verb;
                var cand = ReadVerb(verb, verbKey);
                if (cand is null) continue;

                // Same verb + same command under several keys → one command with merged targets.
                string mergeKey = verb + "\n" + cand.RawCommand + "\n" + (cand.UnsupportedReason ?? "");
                if (!byKey.TryGetValue(mergeKey, out var merged))
                {
                    merged = cand;
                    byKey[mergeKey] = cand;
                    order.Add(cand);
                    targets[cand] = (new HashSet<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }
                if (!merged.Keys.Contains(verbKey, StringComparer.OrdinalIgnoreCase)) merged.Keys.Add(verbKey);
                var t = targets[merged];
                t.types.Add(src.ItemType);
                if (src.ItemType == ItemTypes.File)
                {
                    if (src.Extension is null) t = (t.types, null);  // all files
                    else t.exts?.Add(src.Extension);
                }
                targets[merged] = t;
            }
        }

        var usedIds = new HashSet<string>(existing.Commands.Select(c => c.Id), StringComparer.Ordinal);
        foreach (var cand in order)
        {
            if (cand.Proposed is null) continue;
            var (types, exts) = targets[cand];
            cand.Proposed.Target.ItemTypes = ItemTypes.Known.Where(k => k != ItemTypes.All && types.Contains(k)).ToList();
            cand.Proposed.Target.Extensions = exts is { Count: > 0 } && types.Contains(ItemTypes.File)
                ? exts.OrderBy(e => e, StringComparer.Ordinal).ToList()
                : null;
            cand.Proposed.Origin = new OriginDef { Type = "legacy", Keys = new List<string>(cand.Keys), Command = cand.RawCommand };
            cand.Proposed.Id = UniqueId("legacy-" + Slug(cand.Verb), usedIds);
            cand.ExcludedReason = DefaultExclusions.Match(cand.Verb, cand.Proposed.Action.Program);
        }

        // Diff against what was imported before, so a rescan shows what changed.
        var legacyCommands = existing.Commands.Where(c => c.Origin?.Type == "legacy" && c.Origin.Keys is not null).ToList();
        var matched = new HashSet<CommandDef>();
        foreach (var cand in order)
        {
            if (cand.UnsupportedReason is not null)
            {
                cand.Status = CandidateStatus.Unsupported;
                continue;
            }
            var prior = legacyCommands.FirstOrDefault(c =>
                !matched.Contains(c) &&
                c.Origin!.Keys!.Intersect(cand.Keys, StringComparer.OrdinalIgnoreCase).Any());
            if (prior is null)
            {
                cand.Status = CandidateStatus.New;
                continue;
            }
            matched.Add(prior);
            cand.ExistingId = prior.Id;
            cand.Status = string.Equals(prior.Origin!.Command, cand.RawCommand, StringComparison.Ordinal)
                ? CandidateStatus.Imported
                : CandidateStatus.Changed;
            if (cand.Proposed is not null) cand.Proposed.Id = prior.Id;
        }
        var removed = legacyCommands.Where(c => !matched.Contains(c)).ToList();
        return new ScanResult(order, removed);
    }

    IEnumerable<string> SafeSubKeys(string path)
    {
        try
        {
            return _reg.SubKeyNames(path).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    LegacyCandidate? ReadVerb(string verb, string verbKey)
    {
        // Verbs Explorer never shows in a menu: skip silently.
        if (_reg.HasValue(verbKey, "ProgrammaticAccessOnly") || _reg.HasValue(verbKey, "LegacyDisable")) return null;

        string title = ResolveTitle(verb, verbKey);
        string cmdKey = verbKey + @"\command";
        string raw = _reg.GetString(cmdKey, null) ?? "";

        string? unsupported = null;
        if (_reg.HasValue(verbKey, "Extended")) unsupported = Strings.Legacy_Extended;
        else if (_reg.HasValue(verbKey, "AppliesTo")) unsupported = Strings.Legacy_AppliesTo;
        else if (_reg.HasValue(verbKey, "ExplorerCommandHandler")) unsupported = Strings.Legacy_ExplorerCommandHandler;
        else if (_reg.HasValue(verbKey, "SubCommands") || _reg.HasValue(verbKey, "ExtendedSubCommandsKey"))
            unsupported = Strings.Legacy_Cascade;
        else if (_reg.HasValue(cmdKey, "DelegateExecute")) unsupported = Strings.Legacy_DelegateExecute;
        else if (!_reg.KeyExists(cmdKey) || string.IsNullOrWhiteSpace(raw)) unsupported = Strings.Legacy_NoCommand;

        var cand = new LegacyCandidate { Verb = verb, Title = title, RawCommand = raw, UnsupportedReason = unsupported };
        if (unsupported is not null) return cand;

        var parsed = LegacyCommandLine.Parse(raw);
        if (string.IsNullOrWhiteSpace(parsed.Program))
        {
            return new LegacyCandidate { Verb = verb, Title = title, RawCommand = raw, UnsupportedReason = Strings.Legacy_Unparsable };
        }

        var cmd = new CommandDef
        {
            Title = title,
            Action = new ActionDef { Program = parsed.Program, Arguments = parsed.Arguments },
            Icon = ReadIcon(verbKey),
        };
        // Legacy verbs run once per selected item unless they take all items.
        string? model = _reg.GetString(verbKey, "MultiSelectModel");
        if (string.Equals(model, "Single", StringComparison.OrdinalIgnoreCase))
        {
            cmd.Target.MaxSelection = 1;
        }
        else if (string.Equals(model, "Player", StringComparison.OrdinalIgnoreCase) || parsed.UsesAllItems)
        {
            cmd.Action.MultiFileMode = MultiFileModes.Single;
            if (!parsed.UsesAllItems && parsed.UsesItemPath)
            {
                // Replace standalone "%1" or %1 tokens with %*, without mangling embedded forms like "--open=%1".
                cmd.Action.Arguments = Regex.Replace(
                    cmd.Action.Arguments,
                    @"(?<=^|\s)(?:""%1""|%1)(?=$|\s)",
                    "%*");
            }
        }
        else
        {
            cmd.Action.MultiFileMode = MultiFileModes.Each;
            cmd.Target.MaxSelection = 15;  // Explorer's default limit for per-item verbs
        }
        cand.Proposed = cmd;
        return cand;
    }

    string ResolveTitle(string verb, string verbKey)
    {
        foreach (var candidate in new[] { _reg.GetString(verbKey, "MUIVerb"), _reg.GetString(verbKey, null) })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string text = candidate!;
            if (text.StartsWith('@'))
            {
                string? resolved = null;
                try { resolved = _resolveIndirect(text); } catch { }
                if (string.IsNullOrWhiteSpace(resolved)) continue;
                text = resolved!;
            }
            return StripAccelerator(text);
        }
        return verb;
    }

    IconDef ReadIcon(string verbKey)
    {
        string? icon = _reg.GetString(verbKey, "Icon");
        if (string.IsNullOrWhiteSpace(icon)) return new IconDef { Source = IconSources.Program };
        var (path, index) = ParseIconLocation(icon!);
        if (string.IsNullOrEmpty(path)) return new IconDef { Source = IconSources.Program };
        return new IconDef { Source = IconSources.File, Path = path, Index = index };
    }

    public static (string Path, int Index) ParseIconLocation(string s)
    {
        s = s.Trim();
        // Handle whole-string quoting such as "\"C:\\path\\foo.exe,0\"" as well as "\"C:\\path\\foo.exe\",0".
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"' && s.IndexOf('"', 1) == s.Length - 1)
            s = s[1..^1].Trim();
        int index = 0;
        string path = s;
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out var idx) && !s[(comma + 1)..].Contains('\\'))
        {
            index = idx;
            path = s[..comma].Trim();
        }
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        return (path, index);
    }

    public static string StripAccelerator(string s)
    {
        // "&Open" → "Open", "Save && Exit" → "Save & Exit", "(&O)" Japanese style mnemonic removed.
        s = Regex.Replace(s, @"\(&[A-Za-z0-9]\)", "");
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '&')
            {
                if (i + 1 < s.Length && s[i + 1] == '&') { sb.Append('&'); i++; }
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString().Trim();
    }

    public static string Slug(string s, string fallback = "verb")
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in s.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var r = sb.ToString().Trim('-');
        return r.Length == 0 ? fallback : r;
    }

    public static string UniqueId(string baseId, HashSet<string> used)
    {
        string id = baseId;
        for (int i = 2; !used.Add(id); i++) id = $"{baseId}-{i}";
        return id;
    }
}
