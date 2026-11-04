// iPop Notifier GPO bootstrapper (compiled by build-gpo-msi.ps1 with the .NET Framework csc that ships with Windows,
// so C# 5 only: no string interpolation, no expression bodies).
//
// The machine-wide MSI (Notifier.wxs) puts this exe and the normal per-user Setup.exe in Program Files and adds an
// HKLM Run entry "iPopNotifierDeploy.exe --logon". Then:
//   --logon       (every user, every logon, no window) installs the Notifier for this user once, silently, when it is
//                 not installed yet. After that the app updates itself from the iPop feed; the MSI is never needed again.
//   --remove-all  (MSI uninstall, as SYSTEM) removes the Notifier from EVERY profile on the PC: stops it, deletes its
//                 folders and the per-user registry entries (Run, ipop-notifier:// and ipop:// links, Apps & features),
//                 loading the hives of users who are not logged on.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static class Bootstrap
{
    // The package next to this exe decides the identity: iPopNotifier-win-Setup.exe (prod) or iPopNotifierQA-… (QA).
    static readonly string PackId = FindPackId();
    static readonly string Title = PackId == "iPopNotifierQA" ? "iPop Notifier QA" : "iPop Notifier";
    static string RunName { get { return Title; } }

    static string FindPackId()
    {
        foreach (string f in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "iPopNotifier*-win-Setup.exe"))
        {
            string name = Path.GetFileName(f);
            return name.Substring(0, name.Length - "-win-Setup.exe".Length);
        }
        return "iPopNotifier";
    }
    static string[] Schemes { get { return PackId == "iPopNotifierQA" ? new[] { "ipop-notifier-qa" } : new[] { "ipop-notifier", "ipop" }; } }

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--logon";
        try
        {
            if (mode == "--remove-all") return RemoveAll();
            return Logon();
        }
        catch (Exception e) { Log("failed: " + e); return 1; }
    }

    // ---- logon: install for this user once -----------------------------------------------------------------------

    static int Logon()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return 0;
        string app = Path.Combine(local, PackId);
        if (File.Exists(Path.Combine(app, "Update.exe"))) return 0;            // already installed (GPO or Setup.exe)
        // Service accounts and the like have no real desktop session: nothing to install for them.
        if (Environment.UserName.EndsWith("$") || !Environment.UserInteractive) return 0;

        string setup = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PackId + "-win-Setup.exe");
        if (!File.Exists(setup)) { Log("setup missing: " + setup); return 2; }
        Log("installing for " + Environment.UserName);
        var psi = new ProcessStartInfo(setup, "--silent") { UseShellExecute = false, CreateNoWindow = true };
        using (var p = Process.Start(psi))
        {
            // Setup starts the app itself after installing (first run opens the browser sign-in once).
            if (!p.WaitForExit(10 * 60 * 1000)) { Log("setup still running after 10 minutes"); return 3; }
            Log("setup exit " + p.ExitCode);
            return p.ExitCode;
        }
    }

    // ---- remove-all: MSI uninstall as SYSTEM ---------------------------------------------------------------------

    static int RemoveAll()
    {
        StopRunning();
        EnablePrivilege("SeRestorePrivilege");
        EnablePrivilege("SeBackupPrivilege");
        using (var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
        {
            if (profiles == null) return 0;
            foreach (string sid in profiles.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-5-21-")) continue;                         // real (domain/local) users only
                string home;
                using (var p = profiles.OpenSubKey(sid)) home = p == null ? null : p.GetValue("ProfileImagePath") as string;
                if (string.IsNullOrEmpty(home) || !Directory.Exists(home)) continue;
                try { CleanProfile(sid, Environment.ExpandEnvironmentVariables(home)); }
                catch (Exception e) { Log("profile " + sid + ": " + e.Message); }
            }
        }
        return 0;
    }

    static void StopRunning()
    {
        foreach (var name in new[] { "iPopNotifier", "Update" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    // Only the Notifier's own install (…\AppData\Local\iPopNotifier\…), never anything else named Update.
                    if (path.IndexOf(@"\AppData\Local\" + PackId + @"\", StringComparison.OrdinalIgnoreCase) >= 0) { p.Kill(); p.WaitForExit(5000); }
                }
                catch { }
                finally { p.Dispose(); }
            }
    }

    static void CleanProfile(string sid, string home)
    {
        string local = Path.Combine(home, @"AppData\Local");
        bool had = Directory.Exists(Path.Combine(local, PackId));
        DeleteDir(Path.Combine(local, PackId));
        DeleteDir(Path.Combine(local, @"SantosJayaAbadi\" + PackId));      // session, settings cache, WebView2 data
        DeleteFile(Path.Combine(home, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\" + Title + ".lnk"));
        DeleteFile(Path.Combine(home, @"Desktop\" + Title + ".lnk"));

        // Per-user registry: the hive is loaded when the user is logged on, otherwise load NTUSER.DAT for a moment.
        RegistryKey users = Registry.Users;
        RegistryKey hive = users.OpenSubKey(sid, true);
        string temp = null;
        if (hive == null)
        {
            string dat = Path.Combine(home, "NTUSER.DAT");
            if (!File.Exists(dat)) return;
            temp = "iPopNotifierCleanup_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            int rc = RegLoadKey(HKEY_USERS, temp, dat);
            if (rc != 0) { Log("load hive " + sid + ": " + new Win32Exception(rc).Message); return; }
            hive = users.OpenSubKey(temp, true);
        }
        try
        {
            if (hive == null) return;
            using (var run = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                if (run != null) run.DeleteValue(RunName, false);
            foreach (string scheme in Schemes)
            {
                // ipop:// (Files "Open in File Explorer") belongs to the Notifier only when its command points at it.
                using (var cmd = hive.OpenSubKey(@"Software\Classes\" + scheme + @"\shell\open\command"))
                {
                    string value = cmd == null ? null : cmd.GetValue("") as string;
                    if (value == null || value.IndexOf(PackId, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                hive.DeleteSubKeyTree(@"Software\Classes\" + scheme, false);
            }
            hive.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\velopack." + PackId + ".idesk", false);
            hive.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + PackId, false);
            if (had) Log("removed for " + sid);
        }
        finally
        {
            hive.Close();
            if (temp != null) RegUnLoadKey(HKEY_USERS, temp);
        }
    }

    static void DeleteDir(string path)
    {
        if (!Directory.Exists(path)) return;
        for (int i = 0; i < 3; i++)
        {
            try { Directory.Delete(path, true); return; }
            catch (Exception e) { if (i == 2) Log("delete " + path + ": " + e.Message); else System.Threading.Thread.Sleep(1000); }
        }
    }

    static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---- log: %ProgramData%\iPop Notifier\deploy.log (SYSTEM) or %TEMP%\ipop-notifier-deploy.log (users) -------

    static void Log(string line)
    {
        try
        {
            bool system = Environment.UserName.EndsWith("$") || string.Equals(Environment.UserName, "SYSTEM", StringComparison.OrdinalIgnoreCase);
            string dir = system ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "iPop Notifier") : Path.GetTempPath();
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, system ? "deploy.log" : "ipop-notifier-deploy.log");
            if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);
            File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
        }
        catch { }
    }

    // ---- Win32 ---------------------------------------------------------------------------------------------------

    static readonly IntPtr HKEY_USERS = new IntPtr(unchecked((int)0x80000003));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegLoadKey(IntPtr hKey, string subKey, string file);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegUnLoadKey(IntPtr hKey, string subKey);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TokenPrivileges { public int Count; public long Luid; public int Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnLength);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    static void EnablePrivilege(string name)
    {
        IntPtr token;
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out token)) return;
        try
        {
            var tp = new TokenPrivileges { Count = 1, Attributes = 2 };
            if (LookupPrivilegeValue(null, name, out tp.Luid)) AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }
}
