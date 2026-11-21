namespace ContextBridge.Core;

/// <summary>
/// Finds the executable a command would launch: absolute path, PATH search with PATHEXT, and
/// (on Windows, via the injected lookup) the App Paths registry key used by ShellExecute.
/// </summary>
public sealed class ProgramLocator
{
    readonly Func<string, string?> _env;
    readonly Func<string, string?>? _appPaths;
    readonly Func<string, bool> _fileExists;

    public ProgramLocator(Func<string, string?>? env = null, Func<string, string?>? appPaths = null,
                          Func<string, bool>? fileExists = null)
    {
        _env = env ?? Template.ProcessEnv;
        _appPaths = appPaths;
        _fileExists = fileExists ?? File.Exists;
    }

    public string? Locate(string program)
    {
        string p = Template.ExpandProgram(program, _env);
        if (p.Length == 0) return null;
        bool hasDir = p.IndexOfAny(new[] { '\\', '/', ':' }) >= 0;
        if (hasDir) return _fileExists(p) ? p : null;
        bool hasExt = p.LastIndexOf('.') > 0;

        if (_appPaths is not null)
        {
            var viaAppPaths = _appPaths(p) ?? (hasExt ? null : _appPaths(p + ".exe"));
            if (viaAppPaths is not null && _fileExists(viaAppPaths)) return viaAppPaths;
        }

        var exts = hasExt
            ? new[] { "" }
            : (_env("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Windows semantics regardless of the host OS (keeps the logic unit-testable anywhere).
        var dirs = (_env("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var winDir = _env("SystemRoot") ?? _env("windir");
        var searchDirs = winDir is null ? dirs : dirs.Prepend(winDir).Prepend(Join(winDir, "System32"));
        foreach (var dir in searchDirs)
        {
            foreach (var ext in exts)
            {
                string candidate = Join(dir.Trim('"'), p + ext);
                if (_fileExists(candidate)) return candidate;
            }
        }
        return null;
    }

    public bool Exists(string program) => Locate(program) is not null;

    static string Join(string dir, string name) => dir.TrimEnd('\\', '/') + "\\" + name;
}
