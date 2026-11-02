using System.ComponentModel.DataAnnotations.Schema;

namespace IPop.Core.Entities;

/// One employee's approver chain, kept exactly as HC's workbook holds it: nine fixed slots
/// (1 Manager · 2–5 Asst. Manager · 6 Plant Manager · 7 Plant Ops Manager · 8 GM · 9 Director).
/// Keyed by NIK rather than a User FK so chains survive for people HRIS hasn't created yet.
public class ApproverChain
{
    public const int SlotCount = 9;

    public required string Nik { get; set; }
    public string? Slot1Nik { get; set; }
    public string? Slot2Nik { get; set; }
    public string? Slot3Nik { get; set; }
    public string? Slot4Nik { get; set; }
    public string? Slot5Nik { get; set; }
    public string? Slot6Nik { get; set; }
    public string? Slot7Nik { get; set; }
    public string? Slot8Nik { get; set; }
    public string? Slot9Nik { get; set; }

    /// What the workbook said about the employee, for people without an iPop account yet.
    public string? SourceName { get; set; }
    public string? SourceJobTitle { get; set; }
    public string? SourceLevel { get; set; }
    public string? SourceWorkLocation { get; set; }

    public DateTime UpdatedAt { get; set; }
    public string? UpdatedByNik { get; set; }

    [NotMapped]
    public string?[] Slots =>
        [Slot1Nik, Slot2Nik, Slot3Nik, Slot4Nik, Slot5Nik, Slot6Nik, Slot7Nik, Slot8Nik, Slot9Nik];

    public void SetSlot(int slot, string? nik)
    {
        switch (slot)
        {
            case 1: Slot1Nik = nik; break;
            case 2: Slot2Nik = nik; break;
            case 3: Slot3Nik = nik; break;
            case 4: Slot4Nik = nik; break;
            case 5: Slot5Nik = nik; break;
            case 6: Slot6Nik = nik; break;
            case 7: Slot7Nik = nik; break;
            case 8: Slot8Nik = nik; break;
            case 9: Slot9Nik = nik; break;
            default: throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot must be 1–9.");
        }
    }

    public void SetSlots(IReadOnlyList<string?> slots)
    {
        if (slots.Count != SlotCount) throw new ArgumentException("Exactly 9 slots are required.", nameof(slots));
        for (var i = 0; i < SlotCount; i++) SetSlot(i + 1, slots[i]);
    }
}
