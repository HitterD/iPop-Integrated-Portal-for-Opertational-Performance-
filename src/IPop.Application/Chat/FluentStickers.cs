namespace IPop.Application.Chat;

public record FluentSticker(string Slug, string Label)
{
    public string Key => StickerLimits.FluentPrefix + Slug;
    /// Relative to the web app base: self-hosted 256×256 WebP (Microsoft Fluent Emoji 3D, MIT).
    public string ImagePath => $"stickers/fluent/{Slug}.webp";
}

/// The "Default" sticker pack: a curated set of Microsoft Fluent Emoji 3D images, shipped as
/// static files under wwwroot/stickers/fluent (see LICENSE.txt there). Sent as "fluent:{slug}".
public static class FluentStickers
{
    public static readonly IReadOnlyList<FluentSticker> All =
    [
        new("face-with-tears-of-joy", "Face with tears of joy"),
        new("rolling-on-the-floor-laughing", "Rolling on the floor laughing"),
        new("smiling-face-with-heart-eyes", "Smiling face with heart-eyes"),
        new("smiling-face-with-hearts", "Smiling face with hearts"),
        new("face-blowing-a-kiss", "Face blowing a kiss"),
        new("beaming-face-with-smiling-eyes", "Beaming face with smiling eyes"),
        new("winking-face", "Winking face"),
        new("smiling-face-with-sunglasses", "Smiling face with sunglasses"),
        new("star-struck", "Star-struck"),
        new("partying-face", "Partying face"),
        new("thinking-face", "Thinking face"),
        new("saluting-face", "Saluting face"),
        new("hugging-face", "Hugging face"),
        new("face-with-hand-over-mouth", "Face with hand over mouth"),
        new("zany-face", "Zany face"),
        new("upside-down-face", "Upside-down face"),
        new("grimacing-face", "Grimacing face"),
        new("face-with-rolling-eyes", "Face with rolling eyes"),
        new("sleeping-face", "Sleeping face"),
        new("pleading-face", "Pleading face"),
        new("loudly-crying-face", "Loudly crying face"),
        new("pouting-face", "Pouting face"),
        new("face-with-steam-from-nose", "Face with steam from nose"),
        new("exploding-head", "Exploding head"),
        new("astonished-face", "Astonished face"),
        new("face-screaming-in-fear", "Face screaming in fear"),
        new("nerd-face", "Nerd face"),
        new("hot-face", "Hot face"),
        new("cold-face", "Cold face"),
        new("ghost", "Ghost"),
        new("skull", "Skull"),
        new("thumbs-up", "Thumbs up"),
        new("thumbs-down", "Thumbs down"),
        new("clapping-hands", "Clapping hands"),
        new("folded-hands", "Folded hands"),
        new("waving-hand", "Waving hand"),
        new("ok-hand", "Ok hand"),
        new("victory-hand", "Victory hand"),
        new("flexed-biceps", "Flexed biceps"),
        new("raising-hands", "Raising hands"),
        new("red-heart", "Red heart"),
        new("fire", "Fire"),
        new("sparkles", "Sparkles"),
        new("party-popper", "Party popper"),
        new("hundred-points", "Hundred points"),
        new("rocket", "Rocket"),
        new("trophy", "Trophy"),
        new("hot-beverage", "Hot beverage"),
        new("birthday-cake", "Birthday cake"),
        new("check-mark-button", "Check mark button"),
        new("light-bulb", "Light bulb"),
        new("eyes", "Eyes"),
    ];

    private static readonly Dictionary<string, FluentSticker> BySlug = All.ToDictionary(s => s.Slug);

    public static FluentSticker? Find(string? slug) => slug is not null && BySlug.TryGetValue(slug, out var s) ? s : null;
}
