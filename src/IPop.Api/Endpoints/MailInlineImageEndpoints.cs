using IPop.Application.Attachments;
using IPop.Infrastructure.Mail;
using IPop.Infrastructure.Persistence;

namespace IPop.Api.Endpoints;

public static class MailInlineImageEndpoints
{
    /// One-off move of pasted images out of existing mail bodies onto the NAS. Idempotent and
    /// resumable: call with the returned "next" until it is null; a rerun from the start only
    /// finds bodies that still hold a data: image (failed stores stay inline and are counted).
    public static IEndpointRouteBuilder MapMailInlineImages(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/mail/inline-images").RequireAuthorization("AdminOnly");

        // POST /api/admin/mail/inline-images/migrate?batch=50&after={lastMailId}
        admin.MapPost("/migrate", async (int? batch, Guid? after, IPopDbContext db, IAttachmentStore store,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("MailInlineImages");
            var r = await new MailInlineImages(db, store, log).BackfillBatchAsync(batch ?? 50, after, ct);
            log.LogInformation("Mail inline images batch: {Mails} mails, {Updated} updated, {Images} images stored, {Failed} failed; next {Next}.",
                r.Mails, r.Updated, r.Images, r.Failed, r.Next);
            return Results.Ok(new { mails = r.Mails, updated = r.Updated, images = r.Images, failed = r.Failed, next = r.Next });
        });

        return app;
    }
}
