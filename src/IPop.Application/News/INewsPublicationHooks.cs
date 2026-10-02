namespace IPop.Application.News;

/// Publication side effects run only after the promotion transaction commits.
/// The caller must claim promotion once before invoking this hook.
public interface INewsPublicationHooks
{
    Task AfterPublishedAsync(Guid newsPostId, bool notify, CancellationToken ct);
}
