using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace ContextBridge.Core;

public static class ConfigSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,  // the native reader does not accept comments
        AllowTrailingCommas = false,
        // Keep Japanese and other text readable in the file; still escapes HTML-sensitive chars safely.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static ConfigFile Parse(string json)
    {
        var docOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };
        using (var doc = JsonDocument.Parse(json, docOptions))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("root must be an object");
            if (!doc.RootElement.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number)
                throw new JsonException("'version' is missing");
        }
        var cfg = JsonSerializer.Deserialize<ConfigFile>(json, Options)
                  ?? throw new JsonException("empty document");
        cfg.Groups ??= new();
        cfg.Commands ??= new();
        Normalize(cfg.Groups, cfg.Commands);
        return cfg;
    }

    internal static void Normalize(List<GroupDef>? groups, List<CommandDef>? commands)
    {
        if (groups is not null)
        {
            foreach (var g in groups)
            {
                if (g is null) throw new JsonException("group entry must not be null");
                g.Id ??= "";
                g.Title ??= "";
                if (g.Icon is not null) g.Icon.Source ??= IconSources.None;
            }
        }
        if (commands is not null)
        {
            foreach (var c in commands)
            {
                if (c is null) throw new JsonException("command entry must not be null");
                c.Id ??= "";
                c.Title ??= "";
                c.Target ??= new TargetDef();
                c.Target.ItemTypes ??= new() { ItemTypes.File };
                if (c.Target.ItemTypes.Any(t => t is null))
                    throw new JsonException("'itemTypes' entries must be strings");
                if (c.Target.Extensions is not null && c.Target.Extensions.Any(e => e is null))
                    throw new JsonException("'extensions' entries must be strings");
                c.Action ??= new ActionDef();
                c.Action.Program ??= "";
                c.Action.Arguments ??= "";
                c.Action.MultiFileMode ??= MultiFileModes.Single;
                c.Action.Window ??= WindowModes.Normal;
                c.Icon ??= new IconDef();
                c.Icon.Source ??= IconSources.Program;
            }
        }
    }

    public static string Serialize(ConfigFile cfg) => JsonSerializer.Serialize(cfg, Options) + "\n";

    public static byte[] ToUtf8(ConfigFile cfg) => new UTF8Encoding(false).GetBytes(Serialize(cfg));
}
