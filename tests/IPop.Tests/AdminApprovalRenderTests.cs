using System.Net;
using System.Text;
using Bunit;
using IPop.Web.Components.Admin;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Admin › Approval: who sees which Administration sections, and the approver editor.
public class AdminApprovalRenderTests
{
    private sealed class Api(Func<HttpRequestMessage, string> body) : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add((r.Method.Method, r.RequestUri!.PathAndQuery, r.Content is null ? null : await r.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(r), Encoding.UTF8, "application/json") };
        }
    }

    private const string Page = """
        {"items":[{"nik":"E1","name":"Giri Widodo","plant":"SJA SEPANJANG","division":"Operational › Engineering","department":null,
          "slots":["GONE",null,null,null,null,null,null,"GM",null],
          "steps":[{"slot":1,"label":"Manager","nik":"GONE","name":"Old Boss","active":false,"known":true},
                   {"slot":8,"label":"General Manager","nik":"GM","name":"Tan Novilia","active":true,"known":true}],
          "issues":["inactive_approver"],"updatedAt":null,"updatedByName":null}],
         "totalCount":1,"page":1,"pageSize":50}
        """;

    private static (BunitContext Ctx, Api Api) Setup(string[] roles, string[] permissions, Func<HttpRequestMessage, string>? body = null)
    {
        var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new Api(body ?? (r => r.RequestUri!.AbsolutePath switch
        {
            "/api/approval/chains" => Page,
            "/api/admin/org" => """{"departments":[],"sites":[],"divisions":[]}""",
            _ => "[]",
        }));
        var auth = new AuthState(new HttpClient(api) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime);
        typeof(AuthState).GetProperty("Me")!.SetValue(auth, new DirectoryEmployeeDto("HC1", "Hana", null, null, null, null, null, null, "", null, null, "on", roles));
        typeof(AuthState).GetProperty("Permissions")!.SetValue(auth, permissions.ToHashSet());
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new PhoneState(ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(new OverlayStack());
        ctx.Services.AddSingleton<ToastService>();
        ctx.Services.AddSingleton<ConfirmService>();
        return (ctx, api);
    }

    [Fact]
    public void Sections_HcApprovalSeesOnlyApproval()
    {
        var visible = AdminSections.Visible(isAdmin: false, can: c => c is "approval.read" or "approval.manage").Select(s => s.Slug);
        Assert.Equal(new[] { "approval" }, visible);
    }

    [Fact]
    public void Sections_DirectoryManageAddsOrganization()
    {
        var visible = AdminSections.Visible(false, c => c is "approval.read" or "directory.manage").Select(s => s.Slug).ToList();
        Assert.Equal(new[] { "organization", "approval" }, visible);
    }

    [Fact]
    public void Sections_AdminSeesAll()
    {
        var visible = AdminSections.Visible(true, _ => false).Select(s => s.Slug).ToList();
        Assert.Contains("users", visible);
        Assert.Contains("approval", visible);
        Assert.Contains("organization", visible);
        Assert.Equal(AdminSections.All.Count, visible.Count);
    }

    [Fact]
    public void Table_RendersInactiveApproverChipRed()
    {
        var (ctx, _) = Setup(["Employee"], ["approval.read", "approval.manage"]);
        using var _ctx = ctx;
        var cut = ctx.Render<AdminApproval>();
        cut.WaitForAssertion(() => Assert.Contains("Giri Widodo", cut.Markup));
        var bad = cut.Find(".apv-chip.bad");
        Assert.Contains("Old Boss", bad.TextContent);
        Assert.Equal("No longer active", bad.GetAttribute("title"));
        Assert.Contains("Tan Novilia", cut.Find(".apv-chip:not(.bad)").TextContent);
    }

    [Fact]
    public void Panel_ClearSlot_SendsNullInPut()
    {
        var (ctx, api) = Setup(["Employee"], ["approval.read", "approval.manage"], r => r.RequestUri!.AbsolutePath switch
        {
            "/api/approval/chains" => Page,
            "/api/approval/chains/E1/history" => "[]",
            _ when r.Method == HttpMethod.Put => Page.Split("\"items\":[")[1].Split("],\n         \"totalCount\"")[0],
            _ => "[]",
        });
        using var _ctx = ctx;
        var cut = ctx.Render<AdminApproval>();
        cut.WaitForAssertion(() => Assert.Contains("Giri Widodo", cut.Markup));
        cut.Find("tr.apv-row").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".apv-slot")));
        cut.Find(".apv-slot[data-slot='1'] button.apv-clear").Click();
        cut.WaitForAssertion(() => Assert.Contains(api.Calls, c => c.Method == "PUT" && c.Path == "/api/approval/chains/E1"), TimeSpan.FromSeconds(3));
        var put = api.Calls.Last(c => c.Method == "PUT");
        Assert.Contains("\"slots\":[null,null,null,null,null,null,null,\"GM\",null]", put.Body);
    }

    [Fact]
    public void ReadOnlyViewer_HasNoEditButtons()
    {
        var (ctx, _) = Setup(["Employee"], ["approval.read"]);
        using var _ctx = ctx;
        var cut = ctx.Render<AdminApproval>();
        cut.WaitForAssertion(() => Assert.Contains("Giri Widodo", cut.Markup));
        Assert.Empty(cut.FindAll("button.apv-import"));
        Assert.NotEmpty(cut.FindAll("button.apv-export"));
    }

    [Fact]
    public void Import_ApplyDisabledUntilPreviewLoaded()
    {
        var cut = new BunitContext(); cut.JSInterop.Mode = JSRuntimeMode.Loose;
        using var _c = cut;
        var (ctx, _) = Setup(["Employee"], ["approval.read", "approval.manage"]);
        using var _ctx = ctx;
        var dlg = ctx.Render<AdminApprovalImport>();
        Assert.True(dlg.Find("button.apv-apply").HasAttribute("disabled"));
        Assert.Contains("People not in the file will lose their approvers", dlg.Markup);
    }

    [Fact]
    public void Nav_HcUserCanOpenAdmin_EmployeeCannot()
    {
        var (hcCtx, _) = Setup(["HC – Approval"], ["approval.read", "approval.manage"]);
        using var _h = hcCtx;
        Assert.True(hcCtx.Services.GetRequiredService<AuthState>().CanOpenAdmin);
        var (empCtx, _) = Setup(["Employee"], ["chat.send"]);
        using var _e = empCtx;
        Assert.False(empCtx.Services.GetRequiredService<AuthState>().CanOpenAdmin);
    }

    [Fact]
    public void PhoneAdminHome_HcSeesOnlyTheirSections()
    {
        var (ctx, _) = Setup(["HC – Approval"], ["approval.read", "approval.manage"]);
        using var _ctx = ctx;
        var cut = ctx.Render<IPop.Web.Components.Phone.Admin.AdminHomePhone>(p => p.Add(x => x.IsAdmin, false));
        Assert.NotEmpty(cut.FindAll("a[href='/admin/approval']"));
        Assert.Empty(cut.FindAll("a[href='/admin/users']"));
        Assert.DoesNotContain("Admin only", cut.Markup);
    }
}
