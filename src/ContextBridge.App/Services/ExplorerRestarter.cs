using ContextBridge.App.Resources;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace ContextBridge.App.Services;

/// <summary>An open File Explorer window (or tab) to reopen after the restart.</summary>
sealed record FolderWindow(string Path, long Hwnd, int Left, int Top, int Width, int Height);

/// <summary>
/// Restarts the current user's Explorer and reopens the folder windows that were open.
/// The shell is asked to exit cleanly the way "Exit Explorer" on the taskbar does (fast, and it saves
/// its state); the Restart Manager is only used as a fallback when there is no taskbar window.
/// </summary>
static class ExplorerRestarter
{
    // ---- Win32 ----

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>WM_USER + 436: what the taskbar's hidden "Exit Explorer" command sends to itself.</summary>
    const uint WmExitExplorer = 0x0400 + 436;

    [StructLayout(LayoutKind.Sequential)]
    struct RmUniqueProcess
    {
        public int ProcessId;
        public FILETIME ProcessStartTime;
    }

    const int RmForceShutdown = 0x1;
    const int CchRmSessionKey = 32;  // CCH_RM_SESSION_KEY - 1

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint session, uint nFiles, string[]? files, uint nApplications,
                                          [In] RmUniqueProcess[] applications, uint nServices, string[]? services);

    [DllImport("rstrtmgr.dll")]
    static extern int RmShutdown(uint session, int flags, IntPtr progress);

    [DllImport("rstrtmgr.dll")]
    static extern int RmRestart(uint session, int reserved, IntPtr progress);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint session);

    static string ExplorerExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    static List<Process> ExplorersInSession()
    {
        int session = Process.GetCurrentProcess().SessionId;
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            if (p.SessionId == session) result.Add(p);
            else p.Dispose();
        }
        return result;
    }

    static bool AnyExplorerRunning()
    {
        var list = ExplorersInSession();
        bool any = list.Count > 0;
        list.ForEach(p => p.Dispose());
        return any;
    }

    static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(150);
        }
        return condition();
    }

    // ---- open folder windows ----

    /// <summary>Folder windows currently open in File Explorer. Call on an STA thread (the UI thread).</summary>
    public static List<FolderWindow> CaptureFolderWindows()
    {
        var result = new List<FolderWindow>();
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return result;
            shell = Activator.CreateInstance(type);
            if (shell is null) return result;
            dynamic windows = ((dynamic)shell).Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic w = windows.Item(i);
                    if (w is null) continue;
                    string fullName = w.FullName ?? "";
                    if (!fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string? path = null;
                    try { path = (string)w.Document.Folder.Self.Path; } catch (Exception) { }
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        string url = w.LocationURL ?? "";
                        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
                    }
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    result.Add(new FolderWindow(path, (long)w.HWND, (int)w.Left, (int)w.Top, (int)w.Width, (int)w.Height));
                }
                catch (Exception) { /* window closed meanwhile */ }
            }
        }
        catch (Exception) { /* shell not available: nothing to restore */ }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
        // The same folder twice (e.g. two tabs) only once.
        return result.GroupBy(w => w.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>Reopens the windows (best effort) and puts each back at its old position. Runs on its own STA thread.</summary>
    public static Task ReopenAsync(IReadOnlyList<FolderWindow> windows)
    {
        var done = new TaskCompletionSource();
        if (windows.Count == 0)
        {
            done.SetResult();
            return done.Task;
        }
        var thread = new Thread(() =>
        {
            try { Reopen(windows); } catch (Exception) { }
            done.SetResult();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    static void Reopen(IReadOnlyList<FolderWindow> windows)
    {
        var positioned = new HashSet<long>();
        foreach (var w in windows)
        {
            string arg = w.Path.StartsWith("::", StringComparison.Ordinal) ? "shell:" + w.Path : w.Path;
            try
            {
                Process.Start(new ProcessStartInfo(ExplorerExe, "\"" + arg + "\"") { UseShellExecute = false })?.Dispose();
            }
            catch (Exception)
            {
                continue;
            }
            // Wait for the new window, then restore its size and position.
            WaitFor(() => TryPlace(w, positioned), 4000);
        }
    }

    static bool TryPlace(FolderWindow target, HashSet<long> positioned)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return true;
            shell = Activator.CreateInstance(type);
            if (shell is null) return true;
            dynamic windows = ((dynamic)shell).Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic w = windows.Item(i);
                    if (w is null) continue;
                    long hwnd = (long)w.HWND;
                    if (positioned.Contains(hwnd)) continue;
                    string path = "";
                    try { path = (string)w.Document.Folder.Self.Path; } catch (Exception) { }
                    if (!string.Equals(path, target.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (target.Width > 0 && target.Height > 0)
                    {
                        w.Left = target.Left;
                        w.Top = target.Top;
                        w.Width = target.Width;
                        w.Height = target.Height;
                    }
                    positioned.Add(hwnd);
                    return true;
                }
                catch (Exception) { }
            }
            return false;
        }
        catch (Exception)
        {
            return true;  // give up quietly
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    // ---- restart ----

    /// <summary>Blocking (a few seconds). Returns null on success, otherwise an error message.</summary>
    public static string? Restart()
    {
        try
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            string? error = tray != IntPtr.Zero ? ExitShellGracefully(tray) : RestartWithRestartManager();
            if (error is not null) return error;

            // Make sure a shell comes back. Starting explorer.exe while a shell is already running
            // would only open a folder window, so check first.
            if (!WaitFor(AnyExplorerRunning, 3000))
                Process.Start(new ProcessStartInfo(ExplorerExe) { UseShellExecute = false })?.Dispose();
            // Wait until the taskbar exists again so reopened folder windows attach to the new shell.
            WaitFor(() => FindWindow("Shell_TrayWnd", null) != IntPtr.Zero, 20000);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    static string? ExitShellGracefully(IntPtr tray)
    {
        if (!PostMessage(tray, WmExitExplorer, IntPtr.Zero, IntPtr.Zero)) return RestartWithRestartManager();
        // The shell normally exits within a second or two; folder-window processes ("launch folder
        // windows in a separate process") and hung instances are ended after a grace period.
        if (!WaitFor(() => !AnyExplorerRunning(), 6000))
        {
            foreach (var p in ExplorersInSession())
            {
                try { p.Kill(); p.WaitForExit(3000); } catch (Exception) { }
                p.Dispose();
            }
        }
        Process.Start(new ProcessStartInfo(ExplorerExe) { UseShellExecute = false })?.Dispose();
        return null;
    }

    static string? RestartWithRestartManager()
    {
        var explorers = ExplorersInSession();
        try
        {
            var apps = new List<RmUniqueProcess>();
            foreach (var p in explorers)
            {
                try
                {
                    long ft = p.StartTime.ToFileTime();
                    apps.Add(new RmUniqueProcess
                    {
                        ProcessId = p.Id,
                        ProcessStartTime = new FILETIME { dwLowDateTime = (int)(ft & 0xFFFFFFFF), dwHighDateTime = (int)(ft >> 32) },
                    });
                }
                catch (Exception) { /* exited or not accessible: skip */ }
            }
            if (apps.Count == 0) return null;

            var key = new StringBuilder(CchRmSessionKey + 1);
            int rc = RmStartSession(out uint session, 0, key);
            if (rc != 0) return string.Format(Strings.Err_RmStart, rc);
            try
            {
                rc = RmRegisterResources(session, 0, null, (uint)apps.Count, apps.ToArray(), 0, null);
                if (rc != 0) return string.Format(Strings.Err_RmRegister, rc);
                rc = RmShutdown(session, 0, IntPtr.Zero);
                if (rc != 0) rc = RmShutdown(session, RmForceShutdown, IntPtr.Zero);
                if (rc != 0) return string.Format(Strings.Err_RmShutdown, rc);
                RmRestart(session, 0, IntPtr.Zero);  // failures are covered by the caller's check
            }
            finally
            {
                RmEndSession(session);
            }
            return null;
        }
        finally
        {
            explorers.ForEach(p => p.Dispose());
        }
    }
}
