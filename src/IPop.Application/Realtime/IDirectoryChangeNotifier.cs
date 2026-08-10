namespace IPop.Application.Realtime;

public interface IDirectoryChangeNotifier
{
    Task NotifyAsync(CancellationToken ct = default);
}
