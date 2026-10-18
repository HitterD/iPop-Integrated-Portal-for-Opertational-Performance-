using Windows.Media.Core;
using Windows.Media.Playback;

namespace IPop.Notifier.Core;

/// iPop's own notification sounds (same files as the web app, wwwroot/sounds), one per module.
public static class Sounds
{
    public static string For(Notice n) => n.Module switch
    {
        "mail" => "mail",
        "tickets" => "tickets",
        "calendar" => n.EventType == "meeting.reminder" ? "meeting-urgent" : "meeting",
        "chat" => n.EventType == "chat.mention" ? "mention" : "dm",
        "projects" => "project",
        "news" => n.EventType is "memo.mandatory" or "news.condolence" ? "announce-firm" : "announce",
        "notes" => "notes",
        _ => "system",
    };

    public static void Play(string name, NotifierSettings s)
    {
        if (!s.Sound || s.Volume <= 0) return;
        var file = Path.Combine(AppInfo.AssetsDir, name + ".mp3");
        if (!File.Exists(file)) file = Path.Combine(AppInfo.AssetsDir, "system.mp3");
        try
        {
            var player = new MediaPlayer
            {
                Source = MediaSource.CreateFromUri(new Uri(file)),
                Volume = s.Volume / 100.0,
                AudioCategory = MediaPlayerAudioCategory.Alerts,
            };
            player.MediaEnded += (p, _) => p.Dispose();
            player.MediaFailed += (p, _) => p.Dispose();
            player.Play();
        }
        catch { }
    }
}
