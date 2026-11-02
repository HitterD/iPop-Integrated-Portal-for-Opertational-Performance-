using IPop.Core.Entities;
using Xunit;

namespace IPop.Tests;

/// One approver chain per NIK, nine fixed slots as HC's workbook has them.
public class ApproverChainModelTests
{
    [Fact]
    public void ApproverChain_RoundTripsSlotsByNik()
    {
        using var t = new TestDb();
        var chain = new ApproverChain { Nik = "00000277", UpdatedAt = DateTime.UtcNow };
        chain.SetSlot(1, "00000653");
        chain.SetSlot(8, "00001250");
        t.Db.ApproverChains.Add(chain);
        t.Db.SaveChanges();

        var back = t.Verify().ApproverChains.Single(c => c.Nik == "00000277");
        Assert.Equal(9, back.Slots.Length);
        Assert.Equal("00000653", back.Slots[0]);
        Assert.Equal("00001250", back.Slots[7]);
        Assert.Null(back.Slots[1]);
    }

    [Fact]
    public void SetSlot_OutsideOneToNine_Throws()
    {
        var chain = new ApproverChain { Nik = "1" };
        Assert.Throws<ArgumentOutOfRangeException>(() => chain.SetSlot(10, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => chain.SetSlot(0, "x"));
    }
}
