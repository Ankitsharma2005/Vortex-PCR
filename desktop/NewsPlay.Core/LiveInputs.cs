namespace NewsPlay.Core;

public sealed partial class PlayoutController
{
    private readonly Dictionary<int, (long Frames, double Quiet)> liveFrames = [];
    private bool Available(MediaAsset asset) => asset.Kind == "Live" ? asset.Live is not null : exists(asset.Path);
    public static void ValidateLive(MediaAsset asset)
    {
        var settings = asset.Live ?? throw new InvalidOperationException("Live input settings are required");
        if (string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 200) throw new InvalidOperationException("Enter an input name (up to 200 characters)");
        Number(settings.CacheMs, 100, 10000); Number(settings.GainDb, -60, 6); Number(settings.SignalTimeoutSeconds, 3, 120);
        if (settings.Type == "Browser")
        {
            if (!Uri.TryCreate(asset.Path, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https")) throw new InvalidOperationException("Browser input requires an http:// or https:// URL");
        }
        else if (settings.Type == "NDI")
        {
            if (!asset.Path.StartsWith("ndi://", StringComparison.Ordinal) || asset.Path.Length <= 6 || asset.Path.Length > 2048) throw new InvalidOperationException("Select a discovered NDI source");
        }
        else if (settings.Type == "HLS")
        {
            if (!Uri.TryCreate(asset.Path, UriKind.Absolute, out var stream) || stream.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(stream.Host)) throw new InvalidOperationException("HLS requires an http:// or https:// playlist URL");
        }
        else if (settings.Type is "USB / Webcam" or "DeckLink")
        {
            if (asset.Path != "dshow://" || string.IsNullOrWhiteSpace(settings.VideoDevice)) throw new InvalidOperationException("Choose a detected video capture device");
            if (settings.VideoDevice.Length > 1024 || settings.AudioDevice.Length > 1024 || settings.VideoDevice.Contains('\n') || settings.AudioDevice.Contains('\n')) throw new InvalidOperationException("Invalid capture device name");
        }
        else if (settings.Type == "Screen")
        {
            if (asset.Path != "screen://") throw new InvalidOperationException("Desktop capture address must be screen://");
        }
        else
        {
            if (settings.Type is not ("SRT" or "RTMP" or "RTSP")) throw new InvalidOperationException("This input adapter is not installed");
            if (!Uri.TryCreate(asset.Path, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(settings.Type, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(uri.Host))
                throw new InvalidOperationException("Enter a valid " + settings.Type + " source URL");
            if (settings.Type == "SRT" && uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Any(p => p.Length == 2 && Uri.UnescapeDataString(p[0]).Equals("mode", StringComparison.OrdinalIgnoreCase) && !Uri.UnescapeDataString(p[1]).Equals("caller", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This SRT receiver supports caller mode. Connect to a listening sender; listener/rendezvous receiver modes are unavailable.");
        }
        asset.Kind = "Live"; asset.Duration = 86400; asset.Category = "Live";
    }
    private void ExecuteLive(EngineCommand cmd, ChannelState c)
    {
        switch (cmd.Action)
        {
            case "Live.Delete":
                var deleted = Media(cmd.MediaId);
                if (deleted.Kind != "Live") throw new InvalidOperationException("Select a live input");
                if (!cmd.Confirm) throw new InvalidOperationException("Confirm input deletion: its playlist entries and active outputs will be stopped");
                var deletedRows = State.Rundowns.SelectMany(p => p.Items).Where(i => i.MediaId == deleted.Id).Select(i => i.Id).ToHashSet();
                foreach (var ch in State.Channels)
                {
                    if (ch.LiveInputId == deleted.Id || deletedRows.Contains(ch.CurrentId ?? Guid.Empty)) { Transport(ch, new EngineCommand { Action = "Stop", Confirm = true }); ch.CurrentId = null; }
                    if (deletedRows.Contains(ch.QueuedId ?? Guid.Empty)) ch.QueuedId = null;
                    if (deletedRows.Contains(ch.PlaylistReturn?.ItemId ?? Guid.Empty)) ch.PlaylistReturn = null;
                    if (deletedRows.Contains(ch.Interrupted?.ItemId ?? Guid.Empty)) ch.Interrupted = null;
                }
                foreach (var list in State.Rundowns) list.Items.RemoveAll(i => i.MediaId == deleted.Id);
                foreach (var history in undo.Values.Concat(redo.Values)) foreach (var snapshot in history) snapshot.RemoveAll(i => i.MediaId == deleted.Id);
                State.Schedule.RemoveAll(s => deletedRows.Contains(s.ItemId));
                for (var index = 0; index < State.Carts.Count; index++) if (State.Carts[index] == deleted.Id) State.Carts[index] = null;
                State.Media.Remove(deleted);
                break;
            case "Live.Add":
                var entry = cmd.Item ?? throw new InvalidOperationException("Live item required");
                if (Media(entry.MediaId).Kind != "Live" || entry.Kind != "Live") throw new InvalidOperationException("Select a live input");
                if (cmd.Schedule is { } scheduled && scheduled.At <= now()) throw new InvalidOperationException("Schedule must be in the future");
                EditRundown(new EngineCommand { Action = "Rundown.Add", Channel = c.Id, Item = entry }, c);
                if (cmd.Schedule is { } scheduledTake) State.Schedule.Add(new ScheduledTake { Channel = c.Id, RundownId = c.RundownId, ItemId = entry.Id, Title = entry.Title, At = scheduledTake.At });
                break;
            case "Live.Save":
                var asset = Json.Copy(cmd.Asset ?? throw new InvalidOperationException("Input required")); ValidateLive(asset);
                if (asset.Live!.BackupMediaId == asset.Id) throw new InvalidOperationException("An input cannot be its own backup");
                if (asset.Live.BackupMediaId is { } backup) { var b = Media(backup); if (b.Kind == "Live") throw new InvalidOperationException("Choose a local backup clip in this build"); }
                if (State.Channels.Any(ch => Current(ch)?.MediaId == asset.Id && (ch.Status == "Playing" || ch.Emergency))) throw new InvalidOperationException("Return from this live input before editing it");
                var old = State.Media.FindIndex(m => m.Id == asset.Id);
                if (old >= 0 && State.Media[old].Kind != "Live") throw new InvalidOperationException("Cannot replace local media with a live input");
                if (old >= 0) { var preserveBrowser = State.Media[old].Live?.Type == "Browser" && asset.Live.Type == "Browser"; asset.LiveReleaseRevision = State.Media[old].LiveReleaseRevision + (preserveBrowser ? 0 : 1); State.Media[old] = asset; } else State.Media.Add(asset);
                break;
            case "Live.Take": TakeLive(c, Media(cmd.MediaId)); break;
            case "Live.Back": BackFromLive(c); break;
            default: throw new InvalidOperationException("Unknown live command");
        }
    }
    private void TakeLive(ChannelState c, MediaAsset asset)
    {
        ValidateLive(asset);
        if (c.Emergency) throw new InvalidOperationException("Return from standby before taking a live input");
        var point = c.PlaylistReturn ?? new ResumePoint(c.CurrentId, c.Position, c.Status, c.Pass);
        Finish(c, "Live override");
        c.PlaylistReturn = point; c.LiveInputId = asset.Id; c.Position = 0; c.Status = "Playing";
        c.LoopCurrent = false; c.BreakLoop = false; c.Rate = 1; liveFrames[c.Id] = (decoders[c.Id].Frames, 0);
        try { decoders[c.Id].Load(asset, 0, true); decoders[c.Id].SetAudio(c.GainDb + asset.Live!.GainDb, c.Mute || asset.Live.Mute); StartLog(c); }
        catch { BackFromLive(c); throw; }
    }
    private void BackFromLive(ChannelState c)
    {
        if (c.LiveInputId is null && c.PlaylistReturn is null) { if (Current(c)?.Kind == "Live") Next(c, 1, true); return; }
        var resume = c.PlaylistReturn; Finish(c, "Back to playlist"); c.LiveInputId = null; c.PlaylistReturn = null; liveFrames.Remove(c.Id);
        if (resume?.ItemId is { } id)
        {
            Load(c, id, resume.Status == "Playing", resume.Position); c.Pass = resume.Pass;
            if (c.Status != "Error") c.Status = resume.Status;
        }
        else { decoders[c.Id].Stop(); c.Status = "Stopped"; c.Position = 0; }
    }
    private bool CheckLiveSignal(ChannelState c, MediaAsset input, double elapsed)
    {
        var decoder = decoders[c.Id];
        var previous = liveFrames.GetValueOrDefault(c.Id, (decoder.Frames, 0d));
        var quiet = previous.Item1 == decoder.Frames ? previous.Item2 + elapsed : 0;
        liveFrames[c.Id] = (decoder.Frames, quiet);
        if (decoder.Error is null && !decoder.Ended && quiet < input.Live!.SignalTimeoutSeconds) return false;
        Alarm("Live signal lost: " + input.Name, c.Id);
        try
        {
            if (input.Live!.BackupMediaId is { } backup && Available(Media(backup)))
            {
                var returnPoint = c.PlaylistReturn;
                var fallback = NewItem(Media(backup)); fallback.Loops = -1;
                var p = Playlist(c.RundownId); p.Items.Add(fallback); Load(c, fallback.Id, true);
                c.PlaylistReturn = returnPoint;
            }
            else if (c.LiveInputId is not null) BackFromLive(c);
            else { Next(c, 1, true); if (Current(c)?.Kind == "Live") Fail(c, "No backup or next clip for live input " + input.Name); }
        }
        catch (Exception ex) { Fail(c, "Live fallback failed: " + ex.Message); }
        return true;
    }
}
