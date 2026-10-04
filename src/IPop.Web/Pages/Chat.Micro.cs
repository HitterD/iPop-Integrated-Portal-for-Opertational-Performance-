// Chat page — micro-interactions that need a little JS (wwwroot/js/chat-micro.js, loaded on demand).
// Decoration only: failures are swallowed and never affect sending, recording or viewing.
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Chat
{
    private const string MicroModulePath = "./js/chat-micro.js?v=20261004-micro";
    private Task<IJSObjectReference>? microModule;
    private ElementReference voiceWaveRef;
    private bool voiceWavePending;

    private async Task MicroAsync(string fn, params object?[] args)
    {
        try
        {
            microModule ??= JS.InvokeAsync<IJSObjectReference>("import", MicroModulePath).AsTask();
            var m = await microModule;
            if (!_disposed) await m.InvokeVoidAsync(fn, args);
        }
        catch { /* older cached shell or prerender: no motion */ }
    }

    /// #42: called from OnAfterRenderAsync once the recording pill (and its wave host) is in the DOM.
    private async Task MicroAfterRenderAsync()
    {
        if (voiceWavePending && isRecordingVoice && voiceWaveRef.Id is not null)
        {
            voiceWavePending = false;
            await MicroAsync("startWave", voiceWaveRef);
        }
        else if (!isRecordingVoice && voiceWavePending) voiceWavePending = false;
    }

    private void MicroVoiceStarted() => voiceWavePending = true;
    private void MicroVoiceStopped() { voiceWavePending = false; _ = MicroAsync("stopWave"); }

    /// #40: the viewer image grows out of the thumbnail just clicked (no-op for keyboard/other opens).
    private void MicroArmZoom() => _ = MicroAsync("armZoom");
}
