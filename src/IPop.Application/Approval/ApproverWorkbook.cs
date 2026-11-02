using System.Text.RegularExpressions;

namespace IPop.Application.Approval;

public record WorkbookRow(int RowNumber, string Nik, string Name, string? Division, string? Department, string? DivDept,
    string? Bagian, string? JobTitle, string? Level, string? WorkLocation, string?[] Slots);

public record WorkbookParseResult(IReadOnlyList<WorkbookRow> Rows, int SkippedResigned, IReadOnlyList<string> Errors);

/// HC's approver workbook (sheet "4. ALL DATA_SJA" layout) ⇄ rows. Pure: no IO.
public static partial class ApproverWorkbook
{
    public const string PreferredSheet = "4. ALL DATA_SJA";

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"^nik approver ?(\d)\b")] private static partial Regex SlotHeader();

    private static string Norm(string? h) => Spaces().Replace(h ?? "", " ").Trim().ToLowerInvariant();

    public static WorkbookParseResult Parse(IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0) return new([], 0, ["The sheet is empty."]);
        var header = rows[0].Select(Norm).ToArray();
        int Col(string name) => Array.IndexOf(header, Norm(name));

        int nik = Col("NIK"), name = Col("Nama Karyawan");
        var errors = new List<string>();
        if (nik < 0) errors.Add("Column \"NIK\" was not found in the first row.");
        if (name < 0) errors.Add("Column \"Nama Karyawan\" was not found in the first row.");
        if (errors.Count > 0) return new([], 0, errors);

        int pdmr = Col("PDMR feb 2026"), division = Col("Division"), department = Col("Department"),
            divDept = Col("Div / Dept"), bagian = Col("Bagian"), job = Col("Nama Jabatan"),
            level = Col("Level Jabatan"), location = Col("Work Location");
        var slotCols = Enumerable.Repeat(-1, 9).ToArray();
        for (var c = 0; c < header.Length; c++)
            if (SlotHeader().Match(header[c]) is { Success: true } m && int.Parse(m.Groups[1].Value) is var n and >= 1 and <= 9
                && slotCols[n - 1] < 0)
                slotCols[n - 1] = c;

        var result = new List<WorkbookRow>();
        var skipped = 0;
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            string? Cell(int c) => c >= 0 && c < row.Length && !string.IsNullOrWhiteSpace(row[c]) ? row[c].Trim() : null;
            string? Text(int c) => Cell(c) is { } v && v != "-" && !v.Equals("None", StringComparison.OrdinalIgnoreCase)
                && !v.Equals("#N/A", StringComparison.OrdinalIgnoreCase) ? v : null;

            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var rawNik = Cell(nik);
            if (string.Equals(Cell(pdmr), "ex", StringComparison.OrdinalIgnoreCase)
                || (rawNik?.StartsWith("ex_", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                skipped++;
                continue;
            }
            var id = WorkbookNik.Normalize(rawNik);
            if (id is null) { errors.Add($"Row {r + 1}: no NIK."); continue; }
            result.Add(new WorkbookRow(r + 1, id, Text(name) ?? id, Text(division), Text(department), Text(divDept),
                Text(bagian), Text(job), Text(level), Text(location),
                slotCols.Select(c => WorkbookNik.Normalize(Cell(c))).ToArray()));
        }
        return new(result, skipped, errors);
    }

    private static readonly string[] SlotTitles =
    [
        "Manager", "Asst. Manager", "Asst. Manager", "Asst. Manager", "Asst. Manager",
        "Senior Manager/ Plant Manager", "Senior Manager/ Plant Ops Manager", "General Manager", "Director",
    ];

    /// Export layout: the same columns HC uses, minus the bookkeeping flag columns.
    public static List<string[]> ToSheet(IEnumerable<WorkbookRow> rows, Func<string, string?>? nameOf = null)
    {
        var header = new List<string> { "NIK", "Nama Karyawan", "Division", "Department", "Div / Dept", "Bagian",
            "Nama Jabatan", "Level Jabatan", "Work Location" };
        for (var i = 0; i < 9; i++)
        {
            header.Add($"NIK Approver{i + 1} ({SlotTitles[i]})");
            header.Add($"Nama Approver{i + 1} ({SlotTitles[i]})");
        }
        var sheet = new List<string[]> { header.ToArray() };
        foreach (var r in rows)
        {
            var line = new List<string> { r.Nik, r.Name, r.Division ?? "", r.Department ?? "", r.DivDept ?? "", r.Bagian ?? "",
                r.JobTitle ?? "", r.Level ?? "", r.WorkLocation ?? "" };
            for (var i = 0; i < 9; i++)
            {
                var s = r.Slots.ElementAtOrDefault(i);
                line.Add(s ?? "");
                line.Add(s is null ? "" : nameOf?.Invoke(s) ?? "");
            }
            sheet.Add(line.ToArray());
        }
        return sheet;
    }
}
