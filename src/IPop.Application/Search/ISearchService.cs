namespace IPop.Application.Search;

public record SearchResultItem(
    string Group,
    string Title,
    string? Subtitle,
    string Url,
    string Icon,
    string? AvatarColor = null,
    string? Initials = null,
    /// People only: their NIK (calendar link) and "Free now" / "In a meeting until 11:00".
    string? Nik = null,
    string? Status = null);

public interface ISearchService
{
    Task<IReadOnlyList<SearchResultItem>> SearchAsync(Guid userId, string query, CancellationToken ct = default);
}
