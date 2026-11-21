using ContextBridge.Core.Resources;

namespace ContextBridge.Core.Legacy;

/// <summary>
/// Verbs that are better left out by default — Windows' own verbs (already in the new
/// menu or meaningless to duplicate) and apps known to ship their own Windows 11 menu entry.
/// Matches leave the candidate unchecked with a reason; the user can still import it.
/// </summary>
public static class DefaultExclusions
{
    static readonly Dictionary<string, string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows built-in verbs
        ["open"] = nameof(Strings.Excl_Builtin),
        ["opennew"] = nameof(Strings.Excl_Builtin),
        ["edit"] = nameof(Strings.Excl_Builtin),
        ["print"] = nameof(Strings.Excl_Builtin),
        ["printto"] = nameof(Strings.Excl_Builtin),
        ["runas"] = nameof(Strings.Excl_Builtin),
        ["runasuser"] = nameof(Strings.Excl_Builtin),
        ["find"] = nameof(Strings.Excl_Builtin),
        ["explore"] = nameof(Strings.Excl_Builtin),
        ["openas"] = nameof(Strings.Excl_Builtin),
        ["opennewwindow"] = nameof(Strings.Excl_Builtin),
        ["opennewprocess"] = nameof(Strings.Excl_Builtin),
        ["opennewtab"] = nameof(Strings.Excl_Builtin),
        ["pintohome"] = nameof(Strings.Excl_Builtin),
        ["pintostartscreen"] = nameof(Strings.Excl_Builtin),
        ["properties"] = nameof(Strings.Excl_Builtin),
        ["cmd"] = nameof(Strings.Excl_Builtin),
        ["powershell"] = nameof(Strings.Excl_Builtin),
        ["setdesktopwallpaper"] = nameof(Strings.Excl_Builtin),
        ["rotateleft"] = nameof(Strings.Excl_Builtin),
        ["rotateright"] = nameof(Strings.Excl_Builtin),
        ["play"] = nameof(Strings.Excl_Builtin),
        ["enqueue"] = nameof(Strings.Excl_Builtin),
        ["install"] = nameof(Strings.Excl_Builtin),
        ["uninstall"] = nameof(Strings.Excl_Builtin),
        ["repair"] = nameof(Strings.Excl_Builtin),
        ["mount"] = nameof(Strings.Excl_Builtin),
        ["burn"] = nameof(Strings.Excl_Builtin),
        ["preview"] = nameof(Strings.Excl_Builtin),
        ["closetab"] = nameof(Strings.Excl_Builtin),
        ["WSL"] = nameof(Strings.Excl_Builtin),
        ["Windows.*"] = nameof(Strings.Excl_Builtin),
        // Apps that register their own IExplorerCommand for the Windows 11 menu
        ["WindowsTerminal"] = nameof(Strings.Excl_AppSupports),
        ["openinwt"] = nameof(Strings.Excl_AppSupports),
        ["VSCode"] = nameof(Strings.Excl_AppMaySupport),
        ["AnyCode"] = nameof(Strings.Excl_VisualStudio),
    };

    static readonly Dictionary<string, string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rundll32.exe"] = nameof(Strings.Excl_System),
        ["explorer.exe"] = nameof(Strings.Excl_System),
        ["msiexec.exe"] = nameof(Strings.Excl_System),
        ["control.exe"] = nameof(Strings.Excl_System),
        ["wscript.exe"] = nameof(Strings.Excl_ScriptHost),
        ["cscript.exe"] = nameof(Strings.Excl_ScriptHost),
        ["mshta.exe"] = nameof(Strings.Excl_ScriptHost),
    };

    /// <summary>The localized reason, or null when the verb is not excluded by default.</summary>
    public static string? Match(string verb, string program)
    {
        string? key = null;
        if (Verbs.TryGetValue(verb, out var r)) key = r;
        else if (verb.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase)) key = Verbs["Windows.*"];
        else if (Programs.TryGetValue(Path.GetFileName(program.Replace('/', '\\').Split('\\').Last()), out r)) key = r;
        return key is null ? null : Strings.ResourceManager.GetString(key, Strings.Culture) ?? key;
    }
}
