namespace IPop.Api.Endpoints;

// Wraps the LibreOffice binary so the FileEndpoints preview handler can be unit-tested
// without spawning soffice. The non-test production path runs soffice directly.
public class OfficeOptions
{
    public string SofficePath { get; set; } = "soffice";
    public string TempDirectory { get; set; } = Path.GetTempPath();
    public virtual Task<int> ConvertAsync(string inFile, string outDir, CancellationToken ct) => Task.Run(() =>
    {
        // --headless must be the first argument (before --convert-to) or soffice refuses to start.
        var psi = new System.Diagnostics.ProcessStartInfo(SofficePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--convert-to");
        psi.ArgumentList.Add("pdf:writer_pdf_Export");
        psi.ArgumentList.Add("--outdir");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add(inFile);
        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null) return -1;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }, ct);
}
