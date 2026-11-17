using System.Security.Claims;
using System.Text.Json;
using IPop.Api.Notes;
using IPop.Api.Rbac;
using IPop.Application.Attachments;
using IPop.Application.Chat;
using IPop.Application.Notes;
using IPop.Application.Notifications;
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
            if (r.Success) await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteMeta", r.Data, ct);
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
                await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteMeta", r.Data, ct);
                if (req.Title is not null || req.Emoji is not null) await ChangedAsync(svc, rt, http, id, ct);
            }
            return From(r);
        })).RequirePermission("notes.write");

        g.MapPut("/{id:guid}/favorite", (Guid id, FavoriteRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.SetFavoriteAsync(me.Id, id, req.Favorite, ct))));

        g.MapDelete("/{id:guid}", (Guid id, HttpContext http, INoteService svc, IRealtime rt, IHubContext<NotesHub> hub,
            CancellationToken ct) => Run(http, async me =>
        {
            var audience = await svc.GetAudienceAsync(id, ct);
            var r = await svc.TrashAsync(me.Id, id, ct);
            if (r.Success)
            {
                await PushAsync(rt, http, audience, ct);
                await NotesHub.EvictLostAccessAsync(hub, svc, id, ct);   // a trashed page (and its sub-pages) is no longer live content
            }
            return From(r);
        })).RequirePermission("notes.write");
        g.MapPost("/{id:guid}/move", (Guid id, MoveNoteRequest req, HttpContext http, INoteService svc, IRealtime rt,
            IHubContext<NotesHub> hub, CancellationToken ct) => Run(http, async me =>
        {
            // Old and new audiences both need the refresh: some lose the page, others gain it.
            var before = await svc.GetAudienceAsync(id, ct);
            var r = await svc.MoveAsync(me.Id, id, req, ct);
            if (r.Success)
            {
                await PushAsync(rt, http, before.Union(await svc.GetAudienceAsync(id, ct)).ToList(), ct);
                await NotesHub.EvictLostAccessAsync(hub, svc, id, ct);   // moving out from under a shared parent drops its inherited viewers
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
        g.MapDelete("/{id:guid}/purge", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.PurgeAsync(me.Id, id, ct)))).RequirePermission("notes.write");

        // ── Document ─────────────────────────────────────────────────────────
        g.MapGet("/{id:guid}/doc", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetDocAsync(me.Id, id, ct))));
        g.MapPost("/{id:guid}/snapshot", (Guid id, NoteSnapshotRequest req, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.SnapshotAsync(me.Id, id, req, ct)))).RequirePermission("notes.write");
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
        g.MapDelete("/library/{itemId:guid}", (Guid itemId, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.DeleteLibraryItemAsync(me.Id, itemId, ct)))).RequirePermission("notes.write");

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
                if (r.Success) await PushAsync(rt, http, [me.Id], ct);
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
            IRealtime rt, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.UpdateShareAsync(me.Id, id, shareId, req.Level, ct);
            if (r.Success) await ChangedAsync(svc, rt, http, id, ct);
            return From(r);
        })).RequirePermission("notes.write");
        g.MapDelete("/{id:guid}/shares/{shareId:guid}", (Guid id, Guid shareId, HttpContext http, INoteService svc, IRealtime rt,
            IHubContext<NotesHub> hub, CancellationToken ct) => Run(http, async me =>
        {
            var before = await svc.GetAudienceAsync(id, ct);
            var r = await svc.RemoveShareAsync(me.Id, id, shareId, ct);
            if (r.Success)
            {
                await PushAsync(rt, http, before, ct);
                await NotesHub.EvictLostAccessAsync(hub, svc, id, ct);   // open tabs of whoever lost access stop receiving it
            }
            return From(r);
        })).RequirePermission("notes.write");

        // Share to a chat room and post the page card there in one step.
        g.MapPost("/{id:guid}/send", (Guid id, SendToChatRequest req, HttpContext http, INoteService svc, IChatService chat,
            IChatNotifier chatNotifier, IRealtime rt, CancellationToken ct) => Run(http, async me =>
        {
            var share = await svc.AddShareAsync(me.Id, id, new AddNoteShareRequest(null, req.RoomId, req.Level), ct);
            if (!share.Success) return Error(share.Error!);
            var card = await svc.GetCardAsync(me.Id, id, req.Level, ct);
            if (!card.Success) return Error(card.Error!);
            var sent = await chat.SendMessageAsync(me.Id, new SendMessageRequest(req.RoomId,
                CardPrefix + JsonSerializer.Serialize(card.Data, Json), Guid.NewGuid(), null, "note"), ct);
            if (!sent.Success) return Error(sent.Error ?? "send_failed");
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
        })).RequirePermission("notes.write");
        g.MapDelete("/{id:guid}/comments/{commentId:guid}", (Guid id, Guid commentId, HttpContext http,
            INoteService svc, IHubContext<NotesHub> hub, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.DeleteCommentAsync(me.Id, id, commentId, ct);
            if (!r.Success) return Error(r.Error!);
            await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteThreads", id, ct);
            return Results.Ok(new { threadRemoved = r.Data is null, thread = r.Data });
        })).RequirePermission("notes.write");
        g.MapPut("/{id:guid}/comments/{commentId:guid}/reactions", (Guid id, Guid commentId, ReactionRequest req, HttpContext http,
            INoteService svc, IHubContext<NotesHub> hub, CancellationToken ct) => Run(http, async me =>
        {
            var r = await svc.ToggleReactionAsync(me.Id, id, commentId, req.Emoji, ct);
            if (r.Success) await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteThreads", id, ct);
            return From(r);
        })).RequirePermission("notes.write");
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
                    string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, stream, me.Id, ct);
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
        }).DisableAntiforgery().RequirePermission("notes.write");

        // ── Photo cover ──────────────────────────────────────────────────────
        // Images already in this page, for "pick from this page" in the cover picker.
        g.MapGet("/{id:guid}/images", (Guid id, HttpContext http, INoteService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetPageImagesAsync(me.Id, id, ct))));

        // Upload a new cover photo: validated by content, shrunk, stored on the NAS like any note image, then set as the cover.
        g.MapPost("/{id:guid}/cover", async Task<IResult> (Guid id, IFormFile file, HttpContext http, INoteService svc, IAttachmentStore store,
            IRealtime rt, IHubContext<NotesHub> hub, CancellationToken ct) =>
        {
            var me = await MeAsync(http);
            if (me is null) return Results.Unauthorized();
            var can = await svc.CanUploadAsync(me.Id, id, ct);
            if (!can.Success) return Error(can.Error!);
            await using var input = file.OpenReadStream();
            await using var buffered = new MemoryStream();
            await input.CopyToAsync(buffered, ct);
            buffered.Position = 0;
            var prepared = NoteCoverImage.Prepare(buffered, buffered.Length, out var problem);
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
                if (!set.Success) return Error(set.Error!);
                await hub.Clients.Group(NotesHub.Group(id)).SendAsync("noteMeta", set.Data, ct);
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
        }).DisableAntiforgery().RequirePermission("notes.write");

        return app;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

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
