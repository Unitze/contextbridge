using System.Text;

namespace ContextBridge.Core;

/// <summary>Reads and atomically writes %LOCALAPPDATA%\ContextBridge\commands.json.</summary>
public sealed class ConfigStore
{
    public string Directory { get; }
    public string ConfigPath => Path.Combine(Directory, "commands.json");
    public string BackupPath => Path.Combine(Directory, "commands.json.bak");
    public string IconDirectory => Path.Combine(Directory, "icons");
    public string LogDirectory => Path.Combine(Directory, "logs");

    public ConfigStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory();
    }

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContextBridge");

    public bool Exists => File.Exists(ConfigPath);

    /// <summary>Loads the config; a missing file yields an empty config. Throws on corrupt JSON.</summary>
    public ConfigFile Load()
    {
        if (!File.Exists(ConfigPath)) return new ConfigFile();
        return ConfigSerializer.Parse(File.ReadAllText(ConfigPath, Encoding.UTF8));
    }

    /// <summary>
    /// Writes via temp file + replace so Explorer never observes a half-written file.
    /// The previous version is kept as commands.json.bak.
    /// </summary>
    public void Save(ConfigFile cfg)
    {
        var issues = ConfigValidator.Validate(cfg);
        if (ConfigValidator.HasErrors(issues))
            throw new InvalidOperationException(string.Join("\n", issues.Where(i => i.Severity == Severity.Error).Select(i => i.Message)));

        System.IO.Directory.CreateDirectory(Directory);
        string tmp = $"{ConfigPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = ConfigSerializer.ToUtf8(cfg);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(ConfigPath))
            {
                try
                {
                    File.Replace(tmp, ConfigPath, BackupPath, ignoreMetadataErrors: true);
                    return;
                }
                catch (PlatformNotSupportedException) { }
                catch (IOException) { }
                File.Copy(ConfigPath, BackupPath, overwrite: true);
            }
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
