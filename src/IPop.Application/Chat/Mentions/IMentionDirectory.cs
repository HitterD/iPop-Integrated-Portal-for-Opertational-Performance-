namespace IPop.Application.Chat.Mentions;

public interface IMentionDirectory
{
    bool TryResolve(Guid userId, out string nik, out string name, out bool isActive);
}
