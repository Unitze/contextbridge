using ContextBridge.Core.Legacy;
using Microsoft.Win32;

namespace ContextBridge.App.Services;

/// <summary>HKEY_CLASSES_ROOT (already the merged HKCU + HKLM view), read-only.</summary>
sealed class WindowsRegistryView : IRegistryView
{
    public IEnumerable<string> SubKeyNames(string path)
    {
        try
        {
            if (path.Length == 0) return Registry.ClassesRoot.GetSubKeyNames();
            using var k = Registry.ClassesRoot.OpenSubKey(path, writable: false);
            return k?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public bool KeyExists(string path)
    {
        try
        {
            if (path.Length == 0) return true;
            using var k = Registry.ClassesRoot.OpenSubKey(path, writable: false);
            return k is not null;
        }
        catch
        {
            return false;
        }
    }

    public bool HasValue(string path, string name)
    {
        try
        {
            if (path.Length == 0)
                return Registry.ClassesRoot.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
            using var k = Registry.ClassesRoot.OpenSubKey(path, writable: false);
            return k is not null && k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public string? GetString(string path, string? name)
    {
        try
        {
            // DoNotExpandEnvironmentNames keeps %VAR% so the template engine expands them at launch.
            if (path.Length == 0)
                return Registry.ClassesRoot.GetValue(name ?? "", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            using var k = Registry.ClassesRoot.OpenSubKey(path, writable: false);
            return k?.GetValue(name ?? "", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>App Paths lookup (what ShellExecute uses to find e.g. "code.exe").</summary>
    public static string? AppPath(string exe)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (k?.GetValue("") is string v && v.Length > 0)
                    return Environment.ExpandEnvironmentVariables(v.Trim('"'));
            }
            catch
            {
            }
        }
        return null;
    }
}
