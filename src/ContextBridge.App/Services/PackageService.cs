using ContextBridge.App.Resources;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ContextBridge.Core;

namespace ContextBridge.App.Services;

enum PackageState { Registered, Outdated, NotRegistered, NotInstalled }

sealed record PackageStatus(PackageState State, string? InstalledVersion, string ExpectedVersion);

/// <summary>Sparse package registration. Registration needs elevation (unsigned package).</summary>
static class PackageService
{
    public static string InstallDirectory
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            return dir.Length > 3 ? dir.TrimEnd('\\') : dir;
        }
    }
    public static string PackagePath => Path.Combine(InstallDirectory, "ContextBridge.msix");
    public static string ScriptPath => Path.Combine(InstallDirectory, "register.ps1");

    public static string AppVersion =>
        typeof(PackageService).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}.0" : "0.0.0.0";

    public static PackageStatus Query()
    {
        string expected = AppVersion;
        if (!File.Exists(PackagePath) || !File.Exists(ScriptPath))
            return new(PackageState.NotInstalled, null, expected);
        List<string> names;
        try
        {
            names = NativeMethods.PackagesByFamily(PackageIdentity.FamilyName);
        }
        catch
        {
            names = new();
        }
        if (names.Count == 0) return new(PackageState.NotRegistered, null, expected);
        var installed = PackageIdentity.VersionFromFullName(names[0]);
        return new(installed == expected ? PackageState.Registered : PackageState.Outdated, installed, expected);
    }

    /// <summary>Runs register.ps1 elevated. Returns null on success, otherwise an error message.</summary>
    public static async Task<string?> RunScriptAsync(string action)
    {
        string log = Path.Combine(Path.GetTempPath(), $"ContextBridge-{action}-{Environment.ProcessId}.log");
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptPath}\" -Action {action} -LogPath \"{log}\" -Language {System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return Strings.Err_PowerShell;
            await p.WaitForExitAsync();
            if (p.ExitCode == 0) return null;
            string detail = File.Exists(log) ? await File.ReadAllTextAsync(log) : string.Format(Strings.Err_ExitCode, p.ExitCode);
            return detail.Trim();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return Strings.Err_UacCancelled;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(log); } catch { }
        }
    }
}
