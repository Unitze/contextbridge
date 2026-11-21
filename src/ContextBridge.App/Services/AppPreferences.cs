using System.IO;
using System.Text.Json;
using ContextBridge.Core;

namespace ContextBridge.App.Services;

/// <summary>
/// Settings that only concern the settings app (kept out of commands.json, which the shell
/// handler reads and which is meant to be shared). Stored next to it as app-settings.json.
/// </summary>
sealed class AppPreferences
{
    /// <summary>"" = follow Windows, otherwise a language tag such as "ja" or "en".</summary>
    public string Language { get; set; } = "";
    public bool RestartExplorerOnSave { get; set; }

    public WindowPlacement? Window { get; set; }
    public double CommandListWidth { get; set; }
    public int SelectedTab { get; set; }

    public sealed class WindowPlacement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(ConfigStore.DefaultDirectory(), "app-settings.json");

    static AppPreferences? _current;
    public static AppPreferences Current => _current ??= Load(FilePath);

    public static AppPreferences Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path), Options) ?? new();
        }
        catch (Exception) { /* broken file: fall back to defaults */ }
        return new();
    }

    public void Save() => Save(FilePath);

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception) { /* preference only: ignore */ }
    }
}
