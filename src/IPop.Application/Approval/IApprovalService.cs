namespace IPop.Application.Approval;

public interface IApprovalService
{
    Task<ChainView?> GetAsync(string nik, CancellationToken ct);
    Task<IReadOnlyList<MyApproverStep>> MineAsync(string nik, CancellationToken ct);
    Task<ChainPage> ListAsync(ChainQuery q, CancellationToken ct);
    Task<ChainView> SetSlotsAsync(string nik, IReadOnlyList<string?> slots, string actorNik, CancellationToken ct);
    Task<int> BulkSetSlotAsync(IReadOnlyList<string> niks, int slot, string? approverNik, string actorNik, CancellationToken ct);
    Task<int> ReplaceEverywhereAsync(string oldNik, string? newNik, string actorNik, CancellationToken ct);
    Task<IReadOnlyList<ChainHistoryEntry>> HistoryAsync(string nik, CancellationToken ct);
}
