using IPop.Application.Approval;
using Xunit;

namespace IPop.Tests;

/// HC's "ALL DATA_SJA" sheet → rows. Headers are found by name (they carry line breaks and
/// double spaces), never by position.
public class ApproverWorkbookTests
{
    private static readonly string[] Header =
    [
        "CATEGORY", "NIK ex karyawan ", "PDMR feb 2026", "PDRM per 2 April 2026", "NIK", "Nama Karyawan",
        "Division", "Department", "Div / Dept", "Bagian", "Nama Jabatan", "Level Jabatan", "Work Location",
        "NIK Approver1 (Manager)", "Nama Approver1 (Manager)",
        "NIK Approver2 \n(Asst. Manager)", "Nama Approver2 \n(Asst. Manager)",
        "NIK Approver3 \n(Asst. Manager)", "Nama Approver3 \n(Asst. Manager)",
        "NIK Approver4 \n(Asst. Manager)", "Nama Approver4 \n(Asst. Manager)",
        "NIK Approver5 \n(Asst. Manager)", "Nama Approver5 \n(Asst. Manager)",
        "NIK Approver6 \n((Senior Manager/ Plant Manager)", "Nama Approver6 \n((Senior Manager/ Plant Manager)",
        "NIK Approver7 \n((Senior Manager/ Plant Ops Manager)", "Nama Approver7 \n((Senior Manager/ Plant Ops Manager)",
        "NIK Approver8 \n(General Manager)", "Nama Approver8 \n(General Manager)",
        "NIK Approver9 (Director)", "Nama Approver9 (Director)", "", "",
    ];

    private static string[] Row(string nik, string name, string? pdmr = null, string? a1 = null, string? a8 = null)
    {
        var r = new string[Header.Length];
        Array.Fill(r, "");
        r[0] = "OPS"; r[2] = pdmr ?? ""; r[4] = nik; r[5] = name; r[6] = "OPERATION"; r[7] = "ENGINEERING";
        r[8] = "ENGINEERING"; r[9] = "ENGINEERING"; r[10] = "ENGINEERING TECHNICIAN"; r[11] = "SENIOR STAFF BELOW"; r[12] = "SJA-1";
        r[13] = a1 ?? ""; r[27] = a8 ?? "";
        return r;
    }

    [Fact]
    public void Parse_SkipsExRows()
    {
        var res = ApproverWorkbook.Parse([Header, Row("00000277", "GIRI"), Row("ex_00010199", "ANANG", pdmr: " EX ")]);
        Assert.Empty(res.Errors);
        var row = Assert.Single(res.Rows);
        Assert.Equal("00000277", row.Nik);
        Assert.Equal(1, res.SkippedResigned);
        Assert.Equal("SJA-1", row.WorkLocation);
        Assert.Equal("ENGINEERING", row.Bagian);
        Assert.Equal(2, row.RowNumber);
    }

    [Fact]
    public void Parse_PadsNumericNiks()
    {
        var row = Assert.Single(ApproverWorkbook.Parse([Header, Row("277", "GIRI", a1: "653", a8: "1250.0")]).Rows);
        Assert.Equal("00000277", row.Nik);
        Assert.Equal("00000653", row.Slots[0]);
        Assert.Equal("00001250", row.Slots[7]);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("None")]
    [InlineData("#N/A")]
    [InlineData("   ")]
    public void Parse_TreatsPlaceholdersAsEmpty(string cell)
    {
        var row = Assert.Single(ApproverWorkbook.Parse([Header, Row("00000277", "GIRI", a1: cell)]).Rows);
        Assert.Null(row.Slots[0]);
    }

    [Fact]
    public void Parse_FindsColumnsByNormalisedHeader()
    {
        // Shuffle: move NIK Approver2 to the front and add an unnamed trailing column.
        string[] header = ["NIK  Approver2\n (Asst. Manager)", "Nama Karyawan", "NIK", "Work  Location", "x"];
        string[] row = ["00033194", "GIRI", "00000277", "SJA-2", "junk"];
        var res = ApproverWorkbook.Parse([header, row]);
        Assert.Empty(res.Errors);
        var r = Assert.Single(res.Rows);
        Assert.Equal("00033194", r.Slots[1]);
        Assert.Equal("SJA-2", r.WorkLocation);
        Assert.Null(r.Slots[0]);
    }

    [Fact]
    public void Parse_MissingNikHeader_ReturnsError()
    {
        var res = ApproverWorkbook.Parse([["Nama Karyawan", "Division"], ["GIRI", "OPS"]]);
        Assert.Empty(res.Rows);
        Assert.Contains(res.Errors, e => e.Contains("NIK"));
    }

    [Fact]
    public void Parse_SkipsBlankRows()
    {
        var blank = new string[Header.Length]; Array.Fill(blank, "");
        Assert.Single(ApproverWorkbook.Parse([Header, blank, Row("00000277", "GIRI")]).Rows);
    }

    [Fact]
    public void ToSheet_ThenParse_RoundTrips()
    {
        var first = ApproverWorkbook.Parse([Header, Row("00000277", "GIRI", a1: "00000653", a8: "00001250")]).Rows;
        var again = ApproverWorkbook.Parse(ApproverWorkbook.ToSheet(first)).Rows;
        var a = Assert.Single(first); var b = Assert.Single(again);
        Assert.Equal(a.Nik, b.Nik);
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Division, b.Division);
        Assert.Equal(a.Bagian, b.Bagian);
        Assert.Equal(a.WorkLocation, b.WorkLocation);
        Assert.Equal(a.Slots, b.Slots);
    }
}
