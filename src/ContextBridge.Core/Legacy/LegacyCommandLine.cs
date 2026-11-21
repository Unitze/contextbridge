using System.Text;
using System.Text.RegularExpressions;

namespace ContextBridge.Core.Legacy;

/// <summary>Converts a legacy shell\verb\command string into program + ContextBridge argument template.</summary>
public static class LegacyCommandLine
{
    static readonly string[] ExecutableExtensions = { ".exe", ".com", ".bat", ".cmd" };
    static readonly Regex EmptyQuotesPattern = new(@"(?:^|\s)""""(?=\s|$)", RegexOptions.Compiled);

    public sealed record Parsed(string Program, string Arguments, bool UsesItemPath, bool UsesAllItems);

    /// <summary>Splits off the program. Handles quoted paths and unquoted paths that contain spaces.</summary>
    public static (string Program, string Tail) Split(string commandLine)
    {
        string s = commandLine.Trim();
        if (s.Length == 0) return ("", "");
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            if (end < 0) return (s[1..], "");
            return (s[1..end], s[(end + 1)..].TrimStart());
        }
        int sp = s.IndexOfAny(new[] { ' ', '\t' });
        if (sp < 0) return (s, "");

        string firstToken = s[..sp];
        // Only perform CreateProcess-style space-spanning executable search when the command starts
        // with a path (contains '\', '/', ':', or '%...%'). Bare names like "notepad foo.exe"
        // must split at the first whitespace so an argument ending in ".exe" is not absorbed.
        bool startsWithPath = firstToken.IndexOfAny(new[] { '\\', '/', ':', '%' }) >= 0;
        if (startsWithPath)
        {
            for (int i = 0; i < s.Length; i++)
            {
                // Do not scan past a quote or switch-like token into arguments.
                if (s[i] == '"') break;
                foreach (var ext in ExecutableExtensions)
                {
                    int endPos = i + ext.Length;
                    if (endPos > s.Length) continue;
                    if (string.Compare(s, i, ext, 0, ext.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                    if (endPos == s.Length || s[endPos] == ' ' || s[endPos] == '\t')
                        return (s[..endPos], s[endPos..].TrimStart());
                }
            }
        }
        return (firstToken, s[(sp + 1)..].TrimStart());
    }

    static bool IsEnvNameChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '(' or ')';

    /// <summary>
    /// Maps legacy placeholders to the ContextBridge template syntax:
    /// %1 %L %V %0 %D → %1 (item path), %W → %d (working folder), %* → %*,
    /// unsupported codes (%I %S %H %U %2..%9) are dropped, %ENV% references are kept.
    /// </summary>
    public static string ConvertPlaceholders(string args, out bool usesItem, out bool usesAll)
    {
        usesItem = false;
        usesAll = false;
        var sb = new StringBuilder(args.Length);
        int i = 0, n = args.Length;
        while (i < n)
        {
            char c = args[i];
            if (c != '%')
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (i + 1 >= n)
            {
                sb.Append("%%");
                i++;
                continue;
            }
            // %NAME% environment reference: must start with ASCII letter or '_' and have at least
            // 2 chars, so "%1.%2" or "%1-%V" is never misread as an env var.
            char first = args[i + 1];
            if (char.IsAsciiLetter(first) || first == '_')
            {
                int j = i + 1;
                while (j < n && IsEnvNameChar(args[j])) j++;
                if (j < n && args[j] == '%' && j - i - 1 >= 2)
                {
                    sb.Append(args, i, j - i + 1);
                    i = j + 1;
                    continue;
                }
            }
            char d = args[i + 1];
            switch (d)
            {
                case '%':
                    sb.Append("%%");
                    break;
                case '1' or '0' or 'l' or 'L' or 'v' or 'V' or 'd' or 'D':
                    sb.Append("%1");
                    usesItem = true;
                    break;
                case '*':
                    sb.Append("%*");
                    usesAll = true;
                    break;
                case 'w' or 'W':
                    sb.Append("%d");
                    break;
                case >= '2' and <= '9':
                case 'i' or 'I' or 's' or 'S' or 'h' or 'H' or 'u' or 'U':
                    break;  // not representable; drop
                default:
                    sb.Append("%%").Append(d);  // keep as literal text
                    break;
            }
            i += 2;
        }
        // Dropping codes can leave standalone empty quotes ("") behind; remove them cleanly.
        string res = EmptyQuotesPattern.Replace(sb.ToString(), " ");
        return Regex.Replace(res, @"\s{2,}", " ").Trim();
    }

    public static Parsed Parse(string commandLine)
    {
        var (program, rest) = Split(commandLine);
        string converted = ConvertPlaceholders(rest, out bool item, out bool all);
        // The program part may itself contain env refs (REG_EXPAND_SZ); keep them as they are.
        return new Parsed(program, converted, item, all);
    }
}
