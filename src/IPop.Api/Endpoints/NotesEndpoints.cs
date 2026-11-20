using System.Security.Claims;
using System.Text.Json;
using IPop.Api.Notes;
using IPop.Api.Rbac;
using IPop.Application.Attachments;
using IPop.Application.Chat;
using IPop.Application.Notes;
using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Application.Rbac;
using IPop.Application.Realtime;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class NotesEndpoints
{
    public const string CardPrefix = "[note]";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public record FavoriteRequest(bool Favorite);
    public record LockRequest(bool Locked);
    public record EditCommentRequest(string Body);
    public record ReactionRequest(string Emoji);
    public record ResolveRequest(bool Resolved);
    public record ShareLevelRequest(string Level);
    public record GuideThreadRequest(string Quote, string Anchor);
    public record SendToChatRequest(Guid RoomId, string Level, string? Message);
    public record DocMentionRequest(IReadOnlyList<Guid> UserIds, string? Context);
    public record NoteUploadDto(Guid Id, string FileName, string ContentType, long Size, string Url);
    public record FromUrlRequest(string? Url);

    public static IEndpointRouteBuilder MapNotes(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notes").RequireAuthorization().RequirePermission("notes.read");

        g.MapGet("/sidebar", (HttpContext http, INoteService svc, CancellationToken ct) => Run(http, async me =>
        {
            await svc.EnsureGuideAsync(me.Id, ct);
            return Results.Ok(await svc.GetSidebarAsync(me.Id, ct));
        }));
        g.MapGet("/search", (string? q, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => Results.Ok(await svc.SearchAsync(me.Id, q ?? "", ct))));
        g.MapGet("/trash", (HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => Results.Ok(await svc.GetTrashAsync(me.Id, ct))));
        // ?pageId= adds "can open this page" per person (the @mention picker asks to share with the others).
        g.MapGet("/people", (string? q, Guid? pageId, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => Results.Ok(await svc.SearchPeopleAsync(q ?? "", ct, me.Id, pageId))));

        g.MapGet("/{id:guid}", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetPageAsync(me.Id, id, ct))));

        g.MapGet("/{id:guid}/backlinks", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetBacklinksAsync(me.Id, id, ct))));

        // Lock / unlock: needs edit access. Everyone with the page open learns about it through the usual meta push.
        g.MapPut("/{id:guid}/lock", (Guid id, LockRequest req, HttpContext http, INoteService svc, IHubContext<NotesHub> hub,
            CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.SetLockedAsync(me.Id, id, req.Locked, ct);
            if (r.Success) await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteMeta", PublicMeta(r.Data!), ct);
            return From(r);
        })).RequirePermission("notes.write");

        g.MapPost("", (CreateNoteRequest req, HttpContext http, INoteService svc, IRealtime rt, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.CreateAsync(me.Id, req, ct);
            if (r.Success && req.ParentId is { } parent) await ChangedAsync(svc, rt, http, parent, ct);
            return From(r);
        })).RequirePermission("notes.write");

        g.MapPatch("/{id:guid}", (Guid id, UpdateNoteMetaRequest req, HttpContext http, INoteService svc, IRealtime rt,
            IHubContext<NotesHub> hub, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.UpdateMetaAsync(me.Id, id, req, ct);
            if (r.Success)
            {
                await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteMeta", PublicMeta(r.Data!), ct);
                if (req.Title is not null || req.Emoji is not null) await ChangedAsync(svc, rt, http, id, ct);
            }
            return From(r);
        })).RequirePermission("notes.write");

        g.MapPut("/{id:guid}/favorite", (Guid id, FavoriteRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.SetFavoriteAsync(me.Id, id, req.Favorite, ct))));

        g.MapDelete("/{id:guid}", (Guid id, HttpContext http, INoteService svc, IRealtime rt, IHubContext<NotesHub> hub,
            IPermissionEvaluator perms, CancellationToken ct) => Run(http, async me =>
        {
            var audience = await svc.GetAudienceAsync(id, ct);
            var r = await svc.TrashAsync(me.Id, id, ct);
            if (r.Success)
            {
                await PushAsync(rt, http, audience, ct);
                await NotesHub.EvictAllPodsAsync(hub, svc, id, ct, perms);   // a trashed page (and its sub-pages) is no longer live content
            }
            return From(r);
        })).RequirePermission("notes.write");
        g.MapPost("/{id:guid}/move", (Guid id, MoveNoteRequest req, HttpContext http, INoteService svc, IRealtime rt,
            IHubContext<NotesHub> hub, IPermissionEvaluator perms, CancellationToken ct) => Run(http, async me =>
        {
            // Old and new audiences both need the refresh: some lose the page, others gain it.
            var before = await svc.GetAudienceAsync(id, ct);
            var r = await svc.MoveAsync(me.Id, id, req, ct);
            if (r.Success)
            {
                await PushAsync(rt, http, before.Union(await svc.GetAudienceAsync(id, ct)).ToList(), ct);
                await NotesHub.EvictAllPodsAsync(hub, svc, id, ct, perms);   // moving out from under a shared parent drops its inherited viewers
            }
            return From(r);
        })).RequirePermission("notes.write");
        g.MapPost("/import", (ImportNotesRequest req, HttpContext http, INoteService svc, IRealtime rt, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.ImportAsync(me.Id, req, ct);
            if (r.Success) await (req.ParentId is { } parent ? ChangedAsync(svc, rt, http, parent, ct) : PushAsync(rt, http, [me.Id], ct));
            return From(r);
        })).RequirePermission("notes.write");
        g.MapPost("/imported", (FindImportedRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => Results.Ok(await svc.FindImportedAsync(me.Id, req.Keys ?? [], ct))));
        g.MapPost("/{id:guid}/duplicate", (Guid id, HttpContext http, INoteService svc, IRealtime rt, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.DuplicateAsync(me.Id, id, ct);
            if (r.Success) await ChangedAsync(svc, rt, http, r.Data!.Id, ct);
            return From(r);
        })).RequirePermission("notes.write");
        g.MapPost("/{id:guid}/restore", (Guid id, HttpContext http, INoteService svc, IRealtime rt, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.RestoreAsync(me.Id, id, ct);
            if (r.Success) await ChangedAsync(svc, rt, http, id, ct);
            return From(r);
        })).RequirePermission("notes.write");
        g.MapDelete("/{id:guid}/purge", (Guid id, HttpContext http, INoteService svc, IPopDbContext db, IAttachmentStore store,
            ILoggerFactory logs, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.PurgeWithOrphansAsync(me.Id, id, ct);
            if (r.Success && r.Data is { Count: > 0 } orphans)
                await IPop.Infrastructure.Notes.NotesTrashWorker.DeleteOrphanAttachmentsAsync(db, store, orphans, logs.CreateLogger("IPop.Notes"), ct);
            return From(r, _ => Results.Ok(true));
        })).RequirePermission("notes.write");

        // ── Document ─────────────────────────────────────────────────────────
        g.MapGet("/{id:guid}/doc", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetDocAsync(me.Id, id, ct))));
        g.MapPost("/{id:guid}/snapshot", (Guid id, NoteSnapshotRequest req, HttpContext http, INoteService svc, IProjectService projects,
            IPopDbContext db, IPermissionEvaluator perms, ILoggerFactory logs, CancellationToken ct) =>
            Run(http, async me =>
            {
                var result = await svc.SnapshotAsync(me.Id, id, req, ct);
                // A to-do that was ticked in the note completes the Projects task it was turned into ("→ Task"). Best effort: the page is
                // already saved, and a person who cannot change that task simply leaves it as it is.
                if (result.Success && req.TodoTasks is { Count: > 0 }
                    && await perms.IsAllowedAsync(me.Id, "projects.write", null, ct))
                {
                    var stateText = req.State is not null ? System.Text.Encoding.Latin1.GetString(Convert.FromBase64String(req.State)) : "";
                    var knownTaskIds = (await db.NoteTodoDues.AsNoTracking()
                        .Where(x => x.PageId == id && x.TaskId != null).Select(x => x.TaskId!.Value).ToListAsync(ct)).ToHashSet();
                    var validTasks = req.TodoTasks
                        .Where(t => knownTaskIds.Contains(t.TaskId) || stateText.Contains(t.TaskId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (validTasks.Count > 0)
                        await CompleteTasksOfDoneTodosAsync(db, projects, me.Id, validTasks, logs.CreateLogger("IPop.Notes"), ct);
                }
                return From(result);
            })).RequirePermission("notes.write");
        g.MapPost("/{id:guid}/seed", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.ClaimSeedAsync(me.Id, id, ct), t => new { template = t }))).RequirePermission("notes.write");
        g.MapGet("/{id:guid}/versions", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetVersionsAsync(me.Id, id, ct))));
        g.MapGet("/{id:guid}/versions/{versionId:guid}", (Guid id, Guid versionId, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetVersionStateAsync(me.Id, id, versionId, ct), s => new { state = s })));

        g.MapPut("/{id:guid}/versions/{versionId:guid}/name", (Guid id, Guid versionId, NameVersionRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.NameVersionAsync(me.Id, id, versionId, req.Name, ct)))).RequirePermission("notes.write");
        g.MapPost("/{id:guid}/versions", (Guid id, NameVersionRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.SaveNamedVersionAsync(me.Id, id, req.Name, ct)))).RequirePermission("notes.write");
        g.MapGet("/{id:guid}/viewers", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetViewersAsync(me.Id, id, ct))));

        // ── Whiteboard library ───────────────────────────────────────────────
        g.MapGet("/library", (HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => Results.Ok(await svc.GetLibraryAsync(me.Id, ct))));
        g.MapPost("/library", (SaveLibraryItemRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.SaveLibraryItemAsync(me.Id, req, ct)))).RequirePermission("notes.write");
        g.MapPatch("/library/{itemId:guid}", (Guid itemId, UpdateLibraryItemRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.UpdateLibraryItemAsync(me.Id, itemId, req, ct)))).RequirePermission("notes.write");
        g.MapDelete("/library/{itemId:guid}", (Guid itemId, HttpContext http, INoteService svc, IPermissionEvaluator perms, CancellationToken ct) =>
            Run(http, async me => From(await svc.DeleteLibraryItemAsync(me.Id, itemId, ct, await perms.IsAllowedAsync(me.Id, "notes.admin", null, ct))))).RequirePermission("notes.write");

        // ── Whiteboard templates (Template Center: My templates + Team templates) ──
        g.MapGet("/board-templates", (HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => Results.Ok(await svc.GetBoardTemplatesAsync(me.Id, ct))));
        g.MapPost("/board-templates", (SaveBoardTemplateRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.SaveBoardTemplateAsync(me.Id, req, ct)))).RequirePermission("notes.write");
        g.MapPatch("/board-templates/{templateId:guid}", (Guid templateId, UpdateBoardTemplateRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.UpdateBoardTemplateAsync(me.Id, templateId, req, ct)))).RequirePermission("notes.write");
        // anyone deletes their own; an admin (notes.admin) also removes one shared with the team
        g.MapDelete("/board-templates/{templateId:guid}", (Guid templateId, HttpContext http, INoteService svc, IPermissionEvaluator perms, CancellationToken ct) =>
            Run(http, async me => From(await svc.DeleteBoardTemplateAsync(me.Id, templateId, await perms.IsAllowedAsync(me.Id, "notes.admin", null, ct), ct)))).RequirePermission("notes.write");

        // ── Personal templates ───────────────────────────────────────────────
        g.MapGet("/templates", (HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => Results.Ok(await svc.GetTemplatesAsync(me.Id, ct))));
        g.MapPost("/{id:guid}/save-template", (Guid id, SaveTemplateRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.SaveTemplateAsync(me.Id, id, req.Name, ct)))).RequirePermission("notes.write");
        g.MapPost("/templates/{templateId:guid}/use", (Guid templateId, NewFromTemplateRequest req, HttpContext http, INoteService svc, IRealtime rt, CancellationToken ct) =>
            Run(http, async me =>
            {
                var r = await svc.CreateFromTemplateAsync(me.Id, templateId, req, ct);
                if (r.Success) await ChangedAsync(svc, rt, http, r.Data!.Id, ct);
                return From(r);
            })).RequirePermission("notes.write");
        g.MapDelete("/templates/{templateId:guid}", (Guid templateId, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.DeleteTemplateAsync(me.Id, templateId, ct)))).RequirePermission("notes.write");

        // ── Sharing ──────────────────────────────────────────────────────────
        g.MapGet("/{id:guid}/shares", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetSharesAsync(me.Id, id, ct))));
        g.MapPost("/{id:guid}/shares", (Guid id, AddNoteShareRequest req, HttpContext http, INoteService svc, IRealtime rt,
            INotificationGatewayService notify, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.AddShareAsync(me.Id, id, req, ct);
            if (r.Success)
            {
                await ChangedAsync(svc, rt, http, id, ct);
                if (req.UserId is { } uid) await NotifyAsync(notify, uid, "note.shared", $"{me.Name} shared a note with you",
                    await TitleAsync(http, id, ct), id, ct);
            }
            return From(r);
        })).RequirePermission("notes.write");
        g.MapPatch("/{id:guid}/shares/{shareId:guid}", (Guid id, Guid shareId, ShareLevelRequest req, HttpContext http, INoteService svc,
            IRealtime rt, IHubContext<NotesHub> hub, IPermissionEvaluator perms, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.UpdateShareAsync(me.Id, id, shareId, req.Level, ct);
            if (r.Success)
            {
                await ChangedAsync(svc, rt, http, id, ct);
                await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteAccessChanged", id, ct);
                await NotesHub.EvictAllPodsAsync(hub, svc, id, ct, perms);
            }
            return From(r);
        })).RequirePermission("notes.write");
        g.MapDelete("/{id:guid}/shares/{shareId:guid}", (Guid id, Guid shareId, HttpContext http, INoteService svc, IRealtime rt,
            IHubContext<NotesHub> hub, IPermissionEvaluator perms, CancellationToken ct) => Run(http, async me =>
        {
            var before = await svc.GetAudienceAsync(id, ct);
            var r = await svc.RemoveShareAsync(me.Id, id, shareId, ct);
            if (r.Success)
            {
                await PushAsync(rt, http, before, ct);
                await NotesHub.EvictAllPodsAsync(hub, svc, id, ct, perms);   // open tabs of whoever lost access stop receiving it
            }
            return From(r);
        })).RequirePermission("notes.write");

        // Share to a chat room and post the page card there in one step.
        g.MapPost("/{id:guid}/send", (Guid id, SendToChatRequest req, HttpContext http, INoteService svc, IChatService chat,
            IChatNotifier chatNotifier, IRealtime rt, CancellationToken ct) => Run(http, async me =>
        {
            var existingShares = await svc.GetSharesAsync(me.Id, id, ct);
            var alreadyShared = existingShares.Success && existingShares.Data!.Shares.Any(s => s.RoomId == req.RoomId && s.InheritedFromId is null);
            var share = await svc.AddShareAsync(me.Id, id, new AddNoteShareRequest(null, req.RoomId, req.Level), ct);
            if (!share.Success) return Error(share.Error!);
            var card = await svc.GetCardAsync(me.Id, id, req.Level, ct);
            if (!card.Success)
            {
                if (!alreadyShared) await svc.RemoveShareAsync(me.Id, id, share.Data!.Id, CancellationToken.None);
                return Error(card.Error!);
            }
            var sent = await chat.SendMessageAsync(me.Id, new SendMessageRequest(req.RoomId,
                CardPrefix + JsonSerializer.Serialize(card.Data, Json), Guid.NewGuid(), null, "note"), ct);
            if (!sent.Success)
            {
                if (!alreadyShared) await svc.RemoveShareAsync(me.Id, id, share.Data!.Id, CancellationToken.None);
                return Error(sent.Error ?? "send_failed");
            }
            await chatNotifier.NotifyNewMessageAsync(req.RoomId, sent.Message!, ct);
            var note = req.Message?.Trim();
            if (!string.IsNullOrEmpty(note))
            {
                var text = await chat.SendMessageAsync(me.Id, new SendMessageRequest(req.RoomId, note.Length > 4000 ? note[..4000] : note,
                    Guid.NewGuid(), null, "text"), ct);
                if (text.Success) await chatNotifier.NotifyNewMessageAsync(req.RoomId, text.Message!, ct);
            }
            await ChangedAsync(svc, rt, http, id, ct);
            return Results.Ok(share.Data);
        })).RequirePermission("notes.write").RequirePermission("chat.send");

        // ── Comments ─────────────────────────────────────────────────────────
        g.MapGet("/{id:guid}/threads", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetThreadsAsync(me.Id, id, ct))));
        g.MapPost("/{id:guid}/threads", (Guid id, CreateNoteThreadRequest req, HttpContext http, INoteService svc,
            IHubContext<NotesHub> hub, INotificationGatewayService notify, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.CreateThreadAsync(me.Id, id, req, ct);
            if (!r.Success) return Error(r.Error!);
            await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteThreads", id, ct);
            var title = await TitleAsync(http, id, ct);
            foreach (var uid in r.Data!.NotifyUserIds)
                await NotifyAsync(notify, uid, "note.mention", $"{me.Name} mentioned you in {title}", req.Body, id, ct);
            return Results.Ok(r.Data.Thread);
        }));
        g.MapPost("/{id:guid}/threads/{threadId:guid}/replies", (Guid id, Guid threadId, NoteReplyRequest req, HttpContext http,
            INoteService svc, IHubContext<NotesHub> hub, INotificationGatewayService notify, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.ReplyAsync(me.Id, id, threadId, req, ct);
            if (!r.Success) return Error(r.Error!);
            await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteThreads", id, ct);
            var title = await TitleAsync(http, id, ct);
            foreach (var uid in r.Data!.NotifyUserIds)
                await NotifyAsync(notify, uid, "note.comment", $"{me.Name} replied to a comment in {title}", req.Body, id, ct);
            return Results.Ok(r.Data.Thread);
        }));
        g.MapPut("/{id:guid}/threads/{threadId:guid}/resolved", (Guid id, Guid threadId, ResolveRequest req, HttpContext http,
            INoteService svc, IHubContext<NotesHub> hub, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.SetResolvedAsync(me.Id, id, threadId, req.Resolved, ct);
            if (r.Success) await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteThreads", id, ct);
            return From(r);
        }));
        // Edit / delete / react: every change pushes "noteThreads" so open pages refresh their comment panel.
        g.MapPut("/{id:guid}/comments/{commentId:guid}", (Guid id, Guid commentId, EditCommentRequest req, HttpContext http,
            INoteService svc, IHubContext<NotesHub> hub, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.EditCommentAsync(me.Id, id, commentId, req.Body, ct);
            if (r.Success) await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteThreads", id, ct);
            return From(r);
        }));
        g.MapDelete("/{id:guid}/comments/{commentId:guid}", (Guid id, Guid commentId, HttpContext http,
            INoteService svc, IHubContext<NotesHub> hub, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.DeleteCommentAsync(me.Id, id, commentId, ct);
            if (!r.Success) return Error(r.Error!);
            await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteThreads", id, ct);
            return Results.Ok(new { threadRemoved = r.Data is null, thread = r.Data });
        }));
        g.MapPut("/{id:guid}/comments/{commentId:guid}/reactions", (Guid id, Guid commentId, ReactionRequest req, HttpContext http,
            INoteService svc, IHubContext<NotesHub> hub, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.ToggleReactionAsync(me.Id, id, commentId, req.Emoji, ct);
            if (r.Success) await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteThreads", id, ct);
            return From(r);
        }));
        // An @mention typed into the page body notifies the people who can open it.
        g.MapPost("/{id:guid}/mentions", (Guid id, DocMentionRequest req, HttpContext http, INoteService svc,
            INotificationGatewayService notify, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.MentionInDocAsync(me.Id, id, req.UserIds, ct);
            if (!r.Success) return Error(r.Error!);
            var title = await TitleAsync(http, id, ct);
            foreach (var uid in r.Data!)
                await NotifyAsync(notify, uid, "note.mention", $"{me.Name} mentioned you in {title}", req.Context, id, ct);
            return Results.Ok(new { notified = r.Data.Count });
        })).RequirePermission("notes.write");
        // Mentions already in the page: which of these people can open it (the rest render greyed).
        g.MapPost("/{id:guid}/access-check", (Guid id, NoteAccessCheckRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.WhoCanAccessAsync(me.Id, id, req.UserIds, ct))));
        g.MapPost("/{id:guid}/guide-thread", (Guid id, GuideThreadRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.AddGuideThreadAsync(me.Id, id, req.Quote, req.Anchor, ct))));

        // ── Uploads (images/files inside a page) ─────────────────────────────
        g.MapPost("/{id:guid}/uploads", async Task<IResult> (Guid id, IFormFile file, HttpContext http, INoteService svc, IAttachmentStore store,
            CancellationToken ct) =>
        {
            var me = await MeAsync(http);
            if (me is null) return Results.Unauthorized();
            var can = await svc.CanUploadAsync(me.Id, id, ct);
            if (!can.Success) return Error(can.Error!);
            try
            {
                await using var stream = file.OpenReadStream();
                var stored = await store.PutAsync(AttachmentAreas.Notes, file.FileName,
                    SanitizeNoteContentType(file.FileName, file.ContentType), stream, me.Id, ct);
                await svc.LinkAttachmentAsync(id, stored.Id, ct);
                return Results.Ok(new NoteUploadDto(stored.Id, stored.FileName, stored.ContentType, stored.Size, $"/api/attachments/{stored.Id}"));
            }
            catch (AttachmentRejectedException ex)
            {
                return Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: 422);
            }
            catch (IPop.Application.Files.SynoException ex)
            {
                return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502);
            }
        }).DisableAntiforgery().RequirePermission("notes.write").RequireRateLimiting(ChatEndpoints.UploadRateLimitPolicy);

        // A picture pasted from a web page (the clipboard only has its address): fetched server-side and stored like an upload.
        // 204 = it couldn't be fetched (private address, not a picture, too big, site refused): the editor keeps it as a link.
        g.MapPost("/{id:guid}/uploads/from-url", async Task<IResult> (Guid id, FromUrlRequest req, HttpContext http, INoteService svc,
            IAttachmentStore store, IPop.Infrastructure.Links.SafeHttpFetcher fetcher, CancellationToken ct) =>
        {
            var me = await MeAsync(http);
            if (me is null) return Results.Unauthorized();
            var can = await svc.CanUploadAsync(me.Id, id, ct);
            if (!can.Success) return Error(can.Error!);
            var img = await WebImageImport.FetchAsync(fetcher, req.Url, ct);
            if (img is null) return Results.NoContent();
            try
            {
                await using var stream = new MemoryStream(img.Bytes, writable: false);
                var stored = await store.PutAsync(AttachmentAreas.Notes, img.FileName, img.ContentType, stream, me.Id, ct);
                await svc.LinkAttachmentAsync(id, stored.Id, ct);
                return Results.Ok(new NoteUploadDto(stored.Id, stored.FileName, stored.ContentType, stored.Size, $"/api/attachments/{stored.Id}"));
            }
            catch (AttachmentRejectedException ex) { return Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: 422); }
            catch (IPop.Application.Files.SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        }).RequirePermission("notes.write").RequireRateLimiting(LinkPreviewEndpoints.RateLimitPolicy);

        // ── Photo cover ──────────────────────────────────────────────────────
        // Images already in this page, for "pick from this page" in the cover picker.
        g.MapGet("/{id:guid}/images", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetPageImagesAsync(me.Id, id, ct))));

        // Upload a new cover photo: validated by content, shrunk, stored on the NAS like any note image, then set as the cover.
        g.MapPost("/{id:guid}/cover", async Task<IResult> (Guid id, IFormFile file, HttpContext http, INoteService svc, IAttachmentStore store,
            IPopDbContext db, IRealtime rt, IHubContext<NotesHub> hub, CancellationToken ct) =>
        {
            var me = await MeAsync(http);
            if (me is null) return Results.Unauthorized();
            var can = await svc.CanUploadAsync(me.Id, id, ct);
            if (!can.Success) return Error(can.Error!);
            using var buffered = await ReadCappedAsync(file, NoteCoverImage.MaxBytes, ct);
            NoteCoverImage.Result? prepared = null;
            string? problem = buffered is null ? "too_large" : null;
            if (buffered is not null) prepared = NoteCoverImage.Prepare(buffered, buffered.Length, out problem);
            if (prepared is null)
                return Results.Json(new { code = problem, message = problem switch
                {
                    "too_large" => "Covers can be up to 10 MB.",
                    "unsupported_format" => "Use a JPG, PNG or WebP image.",
                    _ => "That file isn't a usable image.",
                } }, statusCode: problem == "too_large" ? 413 : 422);
            try
            {
                var baseName = Path.GetFileNameWithoutExtension(file.FileName);
                await using var body = new MemoryStream(prepared.Bytes);
                var stored = await store.PutAsync(AttachmentAreas.Notes, (string.IsNullOrWhiteSpace(baseName) ? "cover" : baseName) + prepared.Extension,
                    prepared.ContentType, body, me.Id, ct);
                await svc.LinkAttachmentAsync(id, stored.Id, ct);
                var set = await svc.SetCoverImageAsync(me.Id, id, stored.Id, ct);
                if (!set.Success)
                {
                    await UnlinkAndDeleteAttachmentAsync(db, store, id, stored.Id, ct);
                    return Error(set.Error!);
                }
                await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteMeta", PublicMeta(set.Data!), ct);
                return Results.Ok(set.Data);
            }
            catch (AttachmentRejectedException ex)
            {
                return Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: 422);
            }
            catch (IPop.Application.Files.SynoException ex)
            {
                return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502);
            }
        }).DisableAntiforgery().RequirePermission("notes.write").RequireRateLimiting(ChatEndpoints.UploadRateLimitPolicy);

        // ── To-do → Task ─────────────────────────────────────────────────────
        // "→ Task" on a to-do: a Projects task made by the person themselves. They must be able to edit the page AND add tasks to the
        // chosen project (the Projects service checks the second, and that the assignee is a member of that project).
        g.MapPost("/{id:guid}/todo-task", (Guid id, NoteTodoTaskRequest req, HttpContext http, INoteService svc, IProjectService projects,
            IPopDbContext db, IRealtime rt, CancellationToken ct) => Run(http, async me =>
        {
            var can = await svc.CanUploadAsync(me.Id, id, ct);   // edit access to the page
            if (!can.Success) return Error(can.Error!);
            var title = (req.Title ?? "").Trim();
            if (title.Length == 0) title = "To-do from Notes";
            if (title.Length > 300) title = title[..300];
            DateTime? due = DateTime.TryParseExact(req.Due, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
            var pageTitle = await db.NotePages.AsNoTracking().Where(p => p.Id == id).Select(p => p.Title).FirstOrDefaultAsync(ct);
            var created = await projects.CreateTaskAsync(me.Id, new CreateTaskRequest(req.ProjectId, null, title,
                string.IsNullOrWhiteSpace(pageTitle) ? "From a note." : $"From the note “{pageTitle}”.", "todo", null, null, due, null, null, null), ct);
            if (!created.Success) return Results.Json(new { code = created.Error, message = created.Error }, statusCode: created.Error == "forbidden" ? 403 : 422);
            var task = created.Task!;
            var assigned = req.AssigneeId is { } who && (await projects.AssignUserAsync(me.Id, task.Id, who, null, ct)).Success;
            var projectName = await db.Projects.AsNoTracking().Where(p => p.Id == req.ProjectId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "";
            try { await rt.ToAllAsync(RealtimeEvents.ProjectChanged, req.ProjectId); } catch (Exception) { /* advisory */ }
            return Results.Ok(new NoteTodoTaskDto(task.Id, req.ProjectId, task.Title, projectName, assigned));
        })).RequirePermission("notes.write").RequirePermission("projects.write");

        // ── Repeat… ──────────────────────────────────────────────────────────
        // A page that repeats: a copy of it is made on a schedule (NotesRecurrenceWorker). The day is judged in Jakarta time.
        g.MapGet("/{id:guid}/repeat", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetRecurrenceAsync(me.Id, id, ct), r => r is null ? Results.NoContent() : Results.Ok(r))));
        g.MapPut("/{id:guid}/repeat", (Guid id, SetNoteRecurrenceRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.SetRecurrenceAsync(me.Id, id, req,
                DateOnly.FromDateTime(NoteRecurrenceRules.LocalNow(DateTime.UtcNow)), ct))))
            .RequirePermission("notes.write");
        g.MapDelete("/{id:guid}/repeat", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.StopRecurrenceAsync(me.Id, id, ct)))).RequirePermission("notes.write");

        // ── Whiteboard thumbnail ─────────────────────────────────────────────
        // The editor draws a small picture of the board and uploads it here after the board is saved. One per board: the previous
        // one is removed from the NAS. This is not an edit (no "edited just now"), so it never wakes other people's editors.
        g.MapPost("/{id:guid}/thumbnail", async Task<IResult> (Guid id, IFormFile file, HttpContext http, INoteService svc, IAttachmentStore store,
            IPopDbContext db, IHubContext<NotesHub> hub, CancellationToken ct) =>
        {
            var me = await MeAsync(http);
            if (me is null) return Results.Unauthorized();
            var can = await svc.CanUploadAsync(me.Id, id, ct);
            if (!can.Success) return Error(can.Error!);
            using var buffered = await ReadCappedAsync(file, NoteBoardThumbnail.MaxBytes, ct);
            NoteBoardThumbnail.Result? prepared = null;
            string? problem = buffered is null ? "too_large" : null;
            if (buffered is not null) prepared = NoteBoardThumbnail.Prepare(buffered, buffered.Length, out problem);
            if (prepared is null)
                return Results.Json(new { code = problem, message = "That isn't a usable picture." }, statusCode: problem == "too_large" ? 413 : 422);
            try
            {
                await using var body = new MemoryStream(prepared.Bytes);
                var stored = await store.PutAsync(AttachmentAreas.Notes, "board-thumbnail" + prepared.Extension, prepared.ContentType, body, me.Id, ct);
                await svc.LinkAttachmentAsync(id, stored.Id, ct);
                var set = await svc.SetThumbnailAsync(me.Id, id, stored.Id, ct);
                if (!set.Success)
                {
                    await UnlinkAndDeleteAttachmentAsync(db, store, id, stored.Id, ct);   // not a board / no access: don't leave the file behind
                    return Error(set.Error!);
                }
                if (set.Data is { } replaced) await store.DeleteAsync(replaced, ct);
                await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteThumbnail", id, stored.Id, ct);
                return Results.Ok(new { id = stored.Id });
            }
            catch (AttachmentRejectedException ex) { return Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: 422); }
            catch (IPop.Application.Files.SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        }).DisableAntiforgery().RequirePermission("notes.write").RequireRateLimiting(ChatEndpoints.UploadRateLimitPolicy);

        return app;
    }

    /// Done to-dos that point at a Projects task: that task becomes "done" (once; a task that is already done, or that the person cannot
    /// change, is left alone). Un-ticking the to-do does not reopen the task: the task is the system of record for work.
    internal static async Task CompleteTasksOfDoneTodosAsync(IPopDbContext db, IProjectService projects, Guid userId,
        IReadOnlyList<NoteTodoTaskLinkDto> todos, ILogger log, CancellationToken ct)
    {
        var ids = todos.Where(t => t.Done).Select(t => t.TaskId).Distinct().Take(50).ToList();
        if (ids.Count == 0) return;
        var open = await db.ProjectTasks.AsNoTracking().Where(t => ids.Contains(t.Id) && t.Status != "done").Select(t => t.Id).ToListAsync(ct);
        foreach (var taskId in open)
        {
            try
            {
                var r = await projects.UpdateTaskAsync(userId, taskId, new UpdateTaskRequest(null, null, "done", null, null, null, null, null, null, null, null, null, null, null), ct);
                if (!r.Success) log.LogInformation("Note to-do could not complete task {Task}: {Error}", taskId, r.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Completing task {Task} from a note failed", taskId); }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// Strips per-viewer fields (breadcrumb, access, DM room labels) before broadcasting metadata to a shared page group.
    private static NotePageDto PublicMeta(NotePageDto p) => p with { Access = "", IsFavorite = false, Breadcrumb = [], Rooms = [] };

    private static string SanitizeNoteContentType(string? fileName, string? contentType)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        var ct = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim().ToLowerInvariant();
        var semi = ct.IndexOf(';');
        if (semi >= 0) ct = ct[..semi].Trim();
        if (ext is ".svg" or ".html" or ".htm" or ".xhtml"
            || ct is "image/svg+xml" or "text/html" or "application/xhtml+xml" or "text/xml" or "application/xml")
            return "application/octet-stream";
        return ct;
    }

    private static async Task<MemoryStream?> ReadCappedAsync(IFormFile file, long maxBytes, CancellationToken ct)
    {
        if (file.Length > maxBytes) return null;
        await using var input = file.OpenReadStream();
        var ms = new MemoryStream((int)Math.Min(file.Length > 0 ? file.Length : 4096, maxBytes));
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await input.ReadAsync(buf, ct)) > 0)
        {
            total += n;
            if (total > maxBytes) { await ms.DisposeAsync(); return null; }
            ms.Write(buf, 0, n);
        }
        ms.Position = 0;
        return ms;
    }

    private static async Task UnlinkAndDeleteAttachmentAsync(IPopDbContext db, IAttachmentStore store, Guid pageId, Guid attachmentId, CancellationToken ct)
    {
        await db.NoteAttachments.Where(x => x.PageId == pageId && x.AttachmentId == attachmentId).ExecuteDeleteAsync(ct);
        await store.DeleteAsync(attachmentId, ct);
    }

    private record Me(Guid Id, string Nik, string Name);

    private static async Task<Me?> MeAsync(HttpContext http)
    {
        var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        var db = http.RequestServices.GetRequiredService<IPopDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => new Me(u.Id, u.Nik, u.Name)).FirstOrDefaultAsync();
    }

    private static async Task<IResult> Run(HttpContext http, Func<Me, Task<IResult>> body)
    {
        var me = await MeAsync(http);
        return me is null ? Results.Unauthorized() : await body(me);
    }

    private static IResult Error(string code) => Results.Json(new { code, message = code }, statusCode: code switch
    {
        "not_found" => 404,
        "forbidden" or "not_room_member" => 403,
        "version_conflict" or "confirm_audience" => 409,
        "invalid_import" => 400,
        "locked" => 423,
        _ => 422,
    });

    private static IResult From<T>(NoteResult<T> r) => r.Success ? Results.Ok(r.Data) : Error(r.Error!);
    private static IResult From<T>(NoteResult<T> r, Func<T?, object> map) => r.Success ? Results.Ok(map(r.Data)) : Error(r.Error!);
    private static IResult From<T>(NoteResult<T> r, Func<T?, IResult> choose) => r.Success ? choose(r.Data) : Error(r.Error!);

    private static async Task<string> TitleAsync(HttpContext http, Guid id, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<IPopDbContext>();
        var t = await db.NotePages.AsNoTracking().Where(p => p.Id == id).Select(p => p.Title).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(t) ? "Untitled" : t;
    }

    /// Tells everyone who can see the page (its sidebar may have changed) to refetch.
    private static async Task ChangedAsync(INoteService svc, IRealtime rt, HttpContext http, Guid id, CancellationToken ct) =>
        await PushAsync(rt, http, await svc.GetAudienceAsync(id, ct), ct);

    private static async Task PushAsync(IRealtime rt, HttpContext http, IReadOnlyList<Guid> users, CancellationToken ct)
    {
        if (users.Count == 0) return;
        var db = http.RequestServices.GetRequiredService<IPopDbContext>();
        var niks = await db.Users.AsNoTracking().Where(u => users.Contains(u.Id)).Select(u => u.Nik).ToListAsync(ct);
        await rt.ToUsersAsync(niks, RealtimeEvents.NotesChanged, null, ct);
    }

    private static async Task NotifyAsync(INotificationGatewayService notify, Guid userId, string evt, string title, string? message,
        Guid pageId, CancellationToken ct)
    {
        try
        {
            var body = message ?? "";
            await notify.PublishInternalAsync(clientSlug: "notes", eventType: evt, title: title,
                message: body.Length > 200 ? body[..200] + "…" : body, target: new NotificationTarget(UserId: userId),
                priority: "normal", targetUrl: $"/notes/{pageId}", ct: ct);
        }
        catch
        {
            // Non-blocking notification delivery
        }
    }
}
