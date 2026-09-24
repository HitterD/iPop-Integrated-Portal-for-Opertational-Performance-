namespace IPop.Core.Entities;

/// Admin-tunable setting (v6 foundation spec §7). Value is JSON so one table serves every type.
public class AppSetting
{
    public required string Key { get; set; }
    public required string ValueJson { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedByNik { get; set; }
}
