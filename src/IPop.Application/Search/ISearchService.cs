namespace IPop.Application.Search;

public record SearchResultItem(
    string Group,
    string Title,
    string? Subtitle,
    string Url,
    string Icon,
    string? AvatarColor = null,
    string? Initials = null);

public interface ISearchService
{
    Task<IReadOnlyList<SearchResultItem>> SearchAsync(Guid userId, string query, CancellationToken ct = default);
}
