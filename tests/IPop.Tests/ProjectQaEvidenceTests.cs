using System.Net;
using Bunit;
using IPop.Application.Projects;
using IPop.Web.Components.Projects;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class ProjectQaEvidenceTests
{
    private sealed class EmptyApi : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public void Bulk_evidence_has_text_only_and_never_requests_empty_task_files()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new EmptyApi();
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton<ToastService>();
        ctx.Services.AddSingleton<OverlayStack>();
        ctx.Services.AddSingleton(new ChatAttachments(auth, http, ctx.JSInterop.JSRuntime));
        var task = new ProjectTaskDto(Guid.Empty, Guid.NewGuid(), null, "3 selected tasks", "", "todo", "medium", null, null,
            null, null, 0, null, false, null, false, null, Guid.NewGuid(), "Owner", [], 0, 0, DateTime.UtcNow, DateTime.UtcNow);
        var cut = ctx.Render<ProjectStatusEvidenceModal>(p => p.Add(x => x.IsOpen, true).Add(x => x.Task, task).Add(x => x.TargetStatus, "done"));
        Assert.Empty(cut.FindComponents<TaskFiles>());
        Assert.Contains("each task", cut.Markup);
        Assert.DoesNotContain(api.Calls, path => path.Contains(Guid.Empty.ToString()));
    }
}
