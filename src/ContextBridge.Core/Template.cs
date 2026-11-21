using System.Globalization;
using System.Text;

namespace ContextBridge.Core;

/// <summary>
/// Argument template expansion. This is a line-for-line port of ExpandTemplate in
/// src/Shell/core/config.cpp (the code that actually runs in Explorer); the UI uses it for the
/// live command-line preview. tests/vectors/vectors.json keeps both implementations in sync.
/// </summary>
public static class Template
{
    public sealed record Context(IReadOnlyList<string> Paths, string Directory);

    public static string QuoteArg(string arg)
    {
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                sb.Append(c);
            }
            else if (c == '"')
            {
                // 'backslashes' were already emitted above; add 'backslashes' more plus 1 to escape '"'.
                sb.Append('\\', backslashes + 1).Append('"');
                backslashes = 0;
            }
            else
            {
                backslashes = 0;
                sb.Append(c);
            }
        }
        sb.Append('\\', backslashes).Append('"');
        return sb.ToString();
    }

    static bool IsEnvNameChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '(' or ')' or '-' or '.' or '{' or '}';

    static void AppendRaw(StringBuilder sb, string value, bool followedByQuote)
    {
        sb.Append(value);
        if (!followedByQuote) return;
        int n = 0;
        for (int i = value.Length; i > 0 && value[i - 1] == '\\'; i--) n++;
        sb.Append('\\', n);
    }

    static void AppendAll(StringBuilder sb, IReadOnlyList<string> paths)
    {
        for (int k = 0; k < paths.Count; k++)
        {
            if (k > 0) sb.Append(' ');
            sb.Append(QuoteArg(paths[k]));
        }
    }

    /// <summary>Expands only %% and %ENV% references (used for program and icon paths so %1/%d/%* are never substituted).</summary>
    public static string ExpandEnvOnly(string t, Func<string, string?> env)
    {
        var sb = new StringBuilder(t.Length + 32);
        int i = 0, n = t.Length;
        while (i < n)
        {
            char c = t[i];
            if (c != '%' || i + 1 >= n)
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (t[i + 1] == '%')
            {
                sb.Append('%');
                i += 2;
                continue;
            }
            int j = i + 1;
            while (j < n && IsEnvNameChar(t[j])) j++;
            if (j < n && t[j] == '%' && j > i + 1)
            {
                var v = env(t.Substring(i + 1, j - i - 1));
                if (v is not null)
                {
                    sb.Append(v);
                    i = j + 1;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    public static string Expand(string t, Context ctx, Func<string, string?> env)
    {
        var sb = new StringBuilder(t.Length + 64);
        int i = 0, n = t.Length;
        while (i < n)
        {
            char c = t[i];
            if (c == '"' && i + 3 < n && t[i + 1] == '%' && t[i + 2] == '*' && t[i + 3] == '"')
            {
                AppendAll(sb, ctx.Paths);
                i += 4;
                continue;
            }
            if (c != '%' || i + 1 >= n)
            {
                sb.Append(c);
                i++;
                continue;
            }
            char d = t[i + 1];
            bool nextIsQuote = i + 2 < n && t[i + 2] == '"';
            if (d == '%')
            {
                sb.Append('%');
                i += 2;
            }
            else if (d == '1')
            {
                AppendRaw(sb, ctx.Paths.Count == 0 ? ctx.Directory : ctx.Paths[0], nextIsQuote);
                i += 2;
            }
            else if (d == '*')
            {
                AppendAll(sb, ctx.Paths);
                i += 2;
            }
            else if (d == 'd')
            {
                int j = i + 1;
                while (j < n && IsEnvNameChar(t[j])) j++;
                bool isEnvRef = j < n && t[j] == '%' && j > i + 2;
                if (isEnvRef)
                {
                    var v = env(t.Substring(i + 1, j - i - 1));
                    if (v is not null)
                    {
                        bool afterEnvQuote = j + 1 < n && t[j + 1] == '"';
                        AppendRaw(sb, v, afterEnvQuote);
                        i = j + 1;
                        continue;
                    }
                    // Keep undefined %d...% literal untouched instead of expanding %d.
                    sb.Append(t, i, j - i + 1);
                    i = j + 1;
                    continue;
                }
                AppendRaw(sb, ctx.Directory, nextIsQuote);
                i += 2;
            }
            else
            {
                int j = i + 1;
                while (j < n && IsEnvNameChar(t[j])) j++;
                if (j < n && t[j] == '%' && j > i + 1)
                {
                    var v = env(t.Substring(i + 1, j - i - 1));
                    if (v is not null)
                    {
                        bool afterEnvQuote = j + 1 < n && t[j + 1] == '"';
                        AppendRaw(sb, v, afterEnvQuote);
                        i = j + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Environment lookup against the current process (case-insensitive on Windows).</summary>
    public static string? ProcessEnv(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>Program path with environment variables expanded and surrounding quotes removed.</summary>
    public static string ExpandProgram(string program, Func<string, string?>? env = null)
    {
        var p = ExpandEnvOnly(program, env ?? ProcessEnv).Trim();
        if (p.Length >= 2 && p[0] == '"' && p[^1] == '"') p = p[1..^1];
        return p;
    }

    /// <summary>
    /// Mirrors DisplayTitle in src/Shell/core/config.cpp (stem of the raw program when the title is
    /// blank). The automatic title follows the Windows display language like the shell handler does,
    /// not the settings app's language, so the preview matches what Explorer shows.
    /// </summary>
    public static string DisplayTitle(CommandDef c, CultureInfo? menuCulture = null)
    {
        var t = (c.Title ?? "").Trim();
        if (t.Length > 0) return t;
        var stem = StemOf(c.Action.Program ?? "");
        if (stem.Length == 0) return c.Id;
        return AutoTitle(stem, menuCulture ?? CultureInfo.InstalledUICulture);
    }

    /// <summary>Same rule as the native side: a Japanese UI gets the Japanese form, anything else English.</summary>
    public static string AutoTitle(string stem, CultureInfo menuCulture) =>
        menuCulture.TwoLetterISOLanguageName == "ja" ? stem + " で開く" : "Open in " + stem;

    static string StemOf(string path)
    {
        int end = path.Length;
        while (end > 0 && path[end - 1] is '\\' or '/') end--;
        int start = end;
        while (start > 0 && path[start - 1] is not ('\\' or '/' or ':')) start--;
        var name = path[start..end];
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }
}
