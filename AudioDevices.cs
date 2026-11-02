using NAudio.CoreAudioApi;

namespace RitschyMirror;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>Active Windows audio endpoints for the audio-mirror source list in Settings.</summary>
public static class AudioDevices
{
    /// <summary>Active output (<paramref name="render"/>) or input endpoints; the default first.</summary>
    public static List<AudioDeviceInfo> List(bool render)
    {
        var flow = render ? DataFlow.Render : DataFlow.Capture;
        var result = new List<AudioDeviceInfo>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string defaultId = "";
            if (en.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var def))
                using (def) defaultId = def.ID;
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                using (d) result.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultId));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Audio service unavailable → empty list; the status line explains the rest.
        }
        return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Endpoint ids of the target monitor's HDMI/DP audio (never offered as a source: it
    /// would feed the mirror's own output back into itself).</summary>
    public static HashSet<string> TargetEndpointIds(string targetLabel)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(targetLabel)) return ids;
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (d)
                    if (AudioEndpointMatcher.Matches(AudioMirror.EndpointDescription(d), d.FriendlyName, targetLabel))
                        ids.Add(d.ID);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        return ids;
    }
}
