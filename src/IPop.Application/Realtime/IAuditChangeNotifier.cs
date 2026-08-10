namespace IPop.Application.Realtime;

public interface IAuditChangeNotifier
{
    Task NotifyAsync(CancellationToken ct = default);
}
