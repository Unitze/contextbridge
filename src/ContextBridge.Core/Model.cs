using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContextBridge.Core;

// JSON model of commands.json (schema version 1). Enum-like fields stay strings so the
// file round-trips exactly; unknown members are preserved through JsonExtensionData so
// newer files edited by an older UI do not lose data.

public sealed class ConfigFile
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("$schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Schema { get; set; }

    public int Version { get; set; } = CurrentVersion;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SettingsDef? Settings { get; set; }

    public List<GroupDef> Groups { get; set; } = new();
    public List<CommandDef> Commands { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SettingsDef
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MenuTitle { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Logging { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GroupDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IconDef? Icon { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public GroupDef Clone() => new()
    {
        Id = Id,
        Title = Title,
        Icon = Icon?.Clone(),
        Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra),
    };
}

public sealed class CommandDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Group { get; set; }

    public bool Enabled { get; set; } = true;
    public TargetDef Target { get; set; } = new();
    public ActionDef Action { get; set; } = new();
    public IconDef Icon { get; set; } = new();

    /// <summary>Where the definition came from (legacy migration, share file). Ignored by the handler.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OriginDef? Origin { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public CommandDef Clone() => new()
    {
        Id = Id,
        Title = Title,
        Group = Group,
        Enabled = Enabled,
        Target = Target.Clone(),
        Action = Action.Clone(),
        Icon = Icon.Clone(),
        Origin = Origin?.Clone(),
        Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra),
    };
}

public static class ItemTypes
{
    public const string File = "file";
    public const string Folder = "folder";
    public const string FolderBackground = "folderBackground";
    public const string Drive = "drive";
    public const string All = "*";
    public static readonly string[] Known = { File, Folder, FolderBackground, Drive, All };
}

public sealed class TargetDef
{
    public List<string> ItemTypes { get; set; } = new() { Core.ItemTypes.File };

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Extensions { get; set; }

    public int MinSelection { get; set; } = 1;

    /// <summary>0 = unlimited.</summary>
    public int MaxSelection { get; set; }

    public bool Includes(string type) => ItemTypes.Contains(type) || ItemTypes.Contains(Core.ItemTypes.All);

    public TargetDef Clone() => new()
    {
        ItemTypes = new List<string>(ItemTypes),
        Extensions = Extensions is null ? null : new List<string>(Extensions),
        MinSelection = MinSelection,
        MaxSelection = MaxSelection,
    };
}

public static class MultiFileModes
{
    public const string Single = "single";
    public const string Each = "each";
}

public static class WindowModes
{
    public const string Normal = "normal";
    public const string Minimized = "minimized";
    public const string Maximized = "maximized";
    public const string Hidden = "hidden";
    public static readonly string[] Known = { Normal, Minimized, Maximized, Hidden };
}

public sealed class ActionDef
{
    public string Program { get; set; } = "";
    public string Arguments { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkingDirectory { get; set; }

    public string MultiFileMode { get; set; } = MultiFileModes.Single;
    public bool RunAsAdmin { get; set; }
    public string Window { get; set; } = WindowModes.Normal;

    public ActionDef Clone() => (ActionDef)MemberwiseClone();
}

public static class IconSources
{
    public const string Program = "program";
    public const string File = "file";
    public const string None = "none";
}

public sealed class IconDef
{
    public string Source { get; set; } = IconSources.Program;
    public string? Path { get; set; }
    public int Index { get; set; }

    public IconDef Clone() => (IconDef)MemberwiseClone();
}

public sealed class OriginDef
{
    /// <summary>"legacy" (migrated registry verb) or "import" (share file).</summary>
    public string Type { get; set; } = "";

    /// <summary>Registry keys the verb was found under, e.g. "Directory\shell\git_shell".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Keys { get; set; }

    /// <summary>Raw legacy command line at import time, used to detect changes on rescan.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Command { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    public OriginDef Clone() => new() { Type = Type, Keys = Keys is null ? null : new(Keys), Command = Command, Source = Source };
}
