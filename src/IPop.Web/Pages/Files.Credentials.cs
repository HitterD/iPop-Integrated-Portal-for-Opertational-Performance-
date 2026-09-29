// Files page — NAS sign-in: one account for every site, per-site status, disconnect.
//
// Part of the Files partial class. The class was split by responsibility out of a single
// ~1,300-line @code block; members were moved verbatim, never renamed or re-scoped.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using System.Net.Http.Json;
using IPop.Application.Files;
using IPop.Web.Components;
using IPop.Web.Layout;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Files
{
    private async Task SaveCredential()
    {
        credError = null;
        if (credAllSites) { await ConnectAllSites(); return; }
        credBusy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Put, "/api/files/credential", new { server = credServer, username = credUser, password = credPass });
            if (!res.IsSuccessStatusCode) { credError = "Synology rejected the credentials."; return; }
            showCred = false; credUser = ""; credPass = "";
            await LoadSiteCredentials();
            if (server != credServer) await SwitchServer(credServer); else await Reload(bypassCache: true);
        }
        finally { credBusy = false; StateHasChanged(); }
    }

    private async Task ConnectAllSites()
    {
        credBusy = true; lastConnect.Clear(); StateHasChanged();
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Put, "/api/files/credential/all", new { username = credUser, password = credPass });
            var results = res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<List<SiteConnectResultDto>>() : null;
            if (results is null) { credError = "Could not reach iPop, try again."; return; }
            lastConnect = results.ToDictionary(r => r.Server);
            await LoadSiteCredentials();
            if (results.All(r => r.Status != "connected")) { credError = "No site accepted these credentials."; return; }
            credPass = "";
            // Everything connected: close. Partial: keep the drawer open so the per-site list shows
            // which sites still need their own sign-in.
            if (results.All(r => r.Status == "connected")) { showCred = false; credUser = ""; }
            await Reload(bypassCache: true);
        }
        finally { credBusy = false; StateHasChanged(); }
    }

    private async Task LoadSiteCredentials()
    {
        var list = await Auth.GetAsync<List<CredentialStatusDto>>("/api/files/credentials");
        if (list is not null) siteCreds = list.Where(c => c.Server is not null).ToDictionary(c => c.Server!);
    }

    private (string Css, string Text) SiteStatus(string id)
    {
        if (lastConnect.TryGetValue(id, out var r))
            return r.Status switch
            {
                "connected" => ("on", "Connected"),
                "rejected" => siteCreds.TryGetValue(id, out var own) && !own.IsInvalid
                    ? ("on", $"Kept existing sign-in ({own.Username})")
                    : ("warn", "Rejected, sign in to this site separately"),
                _ => ("err", "Unreachable right now"),
            };
        if (siteCreds.TryGetValue(id, out var c))
            return c.IsInvalid ? ("warn", "Password no longer accepted") : ("on", $"Signed in as {c.Username}");
        return ("", "Not connected");
    }

    private async Task ClearCredential()
    {
        await Auth.SendAsync(HttpMethod.Delete, $"/api/files/credential?server={credServer}", null);
        showCred = false;
        await LoadSiteCredentials();
        await Reload(bypassCache: true);
    }
}
