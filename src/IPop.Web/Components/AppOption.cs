namespace IPop.Web.Components;

/// Single option for the <see cref="AppSelect"/> custom dropdown.
public record AppOption(string Value, string Label, string? Hint = null, string? Icon = null, bool Disabled = false);