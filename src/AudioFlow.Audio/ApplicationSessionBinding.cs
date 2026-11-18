using NAudio.CoreAudioApi;

namespace AudioFlow.Audio;

internal sealed class ApplicationSessionBinding : IDisposable
{
    private readonly AudioSessionControl session;
    public OriginalAudioSession Original { get; }
    public float Peak
    {
        get { try { return (int)session.State == 1 ? session.AudioMeterInformation.MasterPeakValue : 0; } catch { return 0; } }
    }
    public ApplicationSessionBinding(string endpointId, AudioSessionControl session)
    {
        this.session = session;
        Original = new()
        {
            EndpointId = endpointId, Identifier = session.GetSessionIdentifier, Instance = session.GetSessionInstanceIdentifier,
            Volume = session.SimpleAudioVolume.Volume, Muted = session.SimpleAudioVolume.Mute
        };
    }
    public void Restore(float? appliedVolume, bool appliedMute)
    {
        var volume = session.SimpleAudioVolume;
        bool ownedVolume = appliedVolume is { } applied && Math.Abs(volume.Volume - applied) < .001f;
        bool ownedMute = volume.Mute == appliedMute;
        if (ownedVolume) volume.Volume = Original.Volume;
        if (ownedMute) volume.Mute = Original.Muted;
    }
    public void Dispose() => session.Dispose();
}
