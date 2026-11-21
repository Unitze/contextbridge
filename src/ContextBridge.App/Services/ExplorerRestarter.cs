using ContextBridge.App.Resources;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace ContextBridge.App.Services;

/// <summary>
/// Restarts the current user's Explorer through the Restart Manager (the same mechanism installers
/// use): Explorer is asked to shut down like at logoff and is relaunched by RmRestart, so it gets
/// the chance to save state instead of being killed.
/// </summary>
static class ExplorerRestarter
{
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

    /// <summary>Blocking (takes a few seconds). Returns null on success, otherwise an error message.</summary>
    public static string? Restart()
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

            if (apps.Count > 0)
            {
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
                    RmRestart(session, 0, IntPtr.Zero);  // failures are covered by the fallback below
                }
                finally
                {
                    RmEndSession(session);
                }
            }

            // Fallback: if no shell came back (or none was running), start it ourselves. Starting
            // explorer.exe while the shell is running would only open a folder window, so check first.
            for (int i = 0; i < 30; i++)
            {
                var now = ExplorersInSession();
                bool running = now.Count > 0;
                now.ForEach(p => p.Dispose());
                if (running) return null;
                Thread.Sleep(200);
            }
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                UseShellExecute = false,
            })?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            explorers.ForEach(p => p.Dispose());
        }
    }
}
