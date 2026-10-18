using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace IPop.Notifier.Core;

/// Every minute: tells iPop this PC is online (computer name, Windows user, IPs, versions — for the ICT Devices
/// list), and reads the person's iPop status so popups can wait while they are Busy.
public sealed class DeviceReporter(IPopClient api)
{
    public bool IPopBusy { get; private set; }
    public bool IctTools { get; private set; }
    public string? DisplayName { get; private set; }
    /// Public iPop web address from the server ("Notifier:WebBase"); null = same as the server address.
    public Uri? WebBase { get; private set; }
    public event Action? Beat;

    public async Task RunAsync(CancellationToken ct)
    {
        var meChecked = false;
        while (!ct.IsCancellationRequested)
        {
            if (api.IsSignedIn)
            {
                try
                {
                    using var _ = await api.SendAsync(HttpMethod.Post, "api/notifier/devices/register", Report(api.Session!.DeviceKey), ct);
                    var me = await api.GetJsonAsync("api/directory/me", ct);
                    if (me is not null)
                    {
                        IPopBusy = string.Equals(me["status"]?.GetValue<string>(), "busy", StringComparison.OrdinalIgnoreCase);
                        DisplayName = me["name"]?.GetValue<string>() ?? DisplayName;
                    }
                    if (!meChecked)
                    {
                        var tools = await api.GetJsonAsync("api/notifier/me", ct);
                        if (tools is not null)
                        {
                            IctTools = tools["ictTools"]?.GetValue<bool>() == true;
                            if (Uri.TryCreate(tools["webBase"]?.GetValue<string>(), UriKind.Absolute, out var wb) && wb.Scheme is "https" or "http")
                                WebBase = new Uri(wb.ToString().TrimEnd('/') + "/");
                            meChecked = true;
                        }
                    }
                    Beat?.Invoke();
                }
                catch (OperationCanceledException) { return; }
                catch { }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static object Report(string deviceKey) => new
    {
        deviceKey,
        machineName = Environment.MachineName,
        windowsUser = $"{Environment.UserDomainName}\\{Environment.UserName}",
        osVersion = OsName(),
        appVersion = AppInfo.Version,
        localIps = LocalIps(),
    };

    private static string OsName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            // Windows 11 still reports "Windows 10" in ProductName; build 22000+ is 11.
            if (Environment.OSVersion.Version.Build >= 22000) name = name.Replace("Windows 10", "Windows 11");
            var display = k?.GetValue("DisplayVersion") as string;
            return display is null ? name : $"{name} {display}";
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    private static List<string> LocalIps()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct().Take(8).ToList();
        }
        catch { return []; }
    }
}
