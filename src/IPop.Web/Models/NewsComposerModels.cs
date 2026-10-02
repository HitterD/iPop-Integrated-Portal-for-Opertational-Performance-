namespace IPop.Web.Models;

// UI-only types for the full-page News composer (Pages/NewsCompose.razor).
//
// The wire contracts live in IPop.Application.News (NewsComposerDtos.cs) and are referenced
// there by alias: IPop.Web.Models already defines NewsSummaryDto/NewsDetailDto/NewsTargetInput
// for the reader, and those must never shadow the Application composer records.

/// Autosave state shown in the composer's status pill. `Conflict` is terminal until the author
/// explicitly reloads or copies their work out — the page never silently overwrites on a 409.
public enum NewsSaveState { Idle, Dirty, Saving, Saved, Failed, Conflict }

public enum NewsPreviewDevice { Desktop, Mobile }

/// One regular attachment row in the composer's Attachments card.
/// A row with <see cref="Id"/> null is still uploading or failed and is never sent to the server;
/// only rows with <see cref="IsReady"/> are serialized into the draft snapshot.
public sealed record NewsComposerAttachment(
    Guid? Id, string FileName, string ContentType, long Size,
    int Progress = 100, string? Error = null)
{
    public bool IsReady => Id is not null && Error is null;
}

/// One inline/cover media asset the editor inserted, keyed by the canonical media id.
/// <see cref="Url"/> is an ephemeral display URL only; it is never stored in the body.
public sealed record NewsComposerMediaRef(Guid MediaId, string Kind, string? Url, string? FileName, long Size);
