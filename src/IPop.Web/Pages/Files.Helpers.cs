// Files page — formatting, file-type icons/colours and MIME lookup.
//
// Part of the Files partial class. The class was split by responsibility out of a single
// ~1,300-line @code block; members were moved verbatim, never renamed or re-scoped.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using System.Net.Http.Json;
using IPop.Application.Files;
using IPop.Web.Components;
using IPop.Web.Layout;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Files
{
    private static string FormatSize(long b) => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024:N0} KB" : $"{b / 1024 / 1024:N0} MB";
    private static string FormatMtime(long mtime) => mtime == 0 ? "—" : DateTimeOffset.FromUnixTimeSeconds(mtime).ToLocalTime().ToString("MMM dd");
    private static string GetFileExt(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant();
    private static bool IsPreviewable(string name) => GetFileExt(name) is ".pdf" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".xlsx" or ".xls" or ".csv" or ".docx" or ".doc" or ".pptx" or ".ppt" or ".rtf";
    private static bool IsOfficePreviewable(string name) => GetFileExt(name) is ".docx" or ".doc" or ".pptx" or ".ppt" or ".rtf";
    // Sheets renderable by RadzenSpreadsheet (XLSX/CSV). Legacy .xls stays on the SheetJS path.
    private static bool IsSheetPreviewable(string name) => GetFileExt(name) is ".xlsx" or ".csv";
    private static string GetFileIcon(string name) => GetFileExt(name) switch
    {
        ".xls" or ".xlsx" or ".csv" => "table",
        ".doc" or ".docx" or ".rtf" => "file-text",
        ".ppt" or ".pptx" => "presentation",
        ".pdf" => "file-text",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => "image",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "archive",
        ".mp4" or ".mov" or ".avi" or ".mkv" => "video",
        ".mp3" or ".wav" or ".ogg" => "music",
        _ => "file"
    };
    private static string GetFileIconBg(string name) => GetFileExt(name) switch
    {
        ".xls" or ".xlsx" or ".csv" => "var(--accent-soft)",
        ".doc" or ".docx" or ".rtf" => "var(--info-soft)",
        ".ppt" or ".pptx" => "var(--warn-soft)",
        ".pdf" => "var(--danger-soft)",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => "rgba(124, 58, 237, 0.12)",
        ".zip" or ".rar" or ".7z" => "var(--surface-3)",
        _ => "var(--surface-2)"
    };
    private static string GetFileIconColor(string name) => GetFileExt(name) switch
    {
        ".xls" or ".xlsx" or ".csv" => "var(--accent-ink)",
        ".doc" or ".docx" or ".rtf" => "var(--info)",
        ".ppt" or ".pptx" => "var(--warn)",
        ".pdf" => "var(--danger)",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => "#8b5cf6",
        ".zip" or ".rar" or ".7z" => "var(--ink-3)",
        _ => "var(--ink-3)"
    };
    private static string GetFileTagBg(string name) => GetFileExt(name) switch
    {
        ".xls" or ".xlsx" or ".csv" => "var(--accent-soft)",
        ".doc" or ".docx" or ".rtf" => "var(--info-soft)",
        ".ppt" or ".pptx" => "var(--warn-soft)",
        ".pdf" => "var(--danger-soft)",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => "rgba(124, 58, 237, 0.12)",
        _ => "var(--surface-2)"
    };
    private static string GetFileTagColor(string name) => GetFileExt(name) switch
    {
        ".xls" or ".xlsx" or ".csv" => "var(--accent-ink)",
        ".doc" or ".docx" or ".rtf" => "var(--info)",
        ".ppt" or ".pptx" => "var(--warn)",
        ".pdf" => "var(--danger)",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => "#8b5cf6",
        _ => "var(--ink-2)"
    };
    private static string GetFileExtLabel(string name)
    {
        var ext = GetFileExt(name);
        return string.IsNullOrEmpty(ext) ? "FILE" : ext.TrimStart('.').ToUpperInvariant();
    }
    private static string GetMimeType(string name) => GetFileExt(name) switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".doc" => "application/msword",
        ".rtf" => "application/rtf",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xls" => "application/vnd.ms-excel",
        ".csv" => "text/csv",
        _ => "application/octet-stream"
    };
    private void ShareViaMail(SynoItemDto it)
    {
        var subject = it.Name;
        var body = $"File: {it.Name}\nLocation: {server}:{it.Path}";
        Nav.NavigateTo($"/mail?shareSubject={Uri.EscapeDataString(subject)}&shareBody={Uri.EscapeDataString(body)}");
    }
}
