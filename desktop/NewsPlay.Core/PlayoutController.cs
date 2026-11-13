namespace NewsPlay.Core;

public interface IDecoder : IDisposable
{
    bool HasMedia { get; }
    double Position { get; }
    bool Ended { get; }
    string? Error { get; }
    long Frames { get; }
    void Load(MediaAsset media, double position, bool play);
    void Play();
    void Pause();
    void Stop();
    void Seek(double seconds);
    void SetAudio(double gainDb, bool mute);
    void SetRate(double rate);
    void SetColour(ColourCorrection correction) { }
    void SetAutoLevel(AudioLevelSettings settings) { }
    IReadOnlyList<AudioDeviceOption> AudioDevices() => [];
    void SetAudioDevice(string id) { if (id.Length != 0) throw new InvalidOperationException("Audio routing is not supported by this decoder"); }
    void SetAudioFormat(int rate, int bits) { }
    void ConfigureAudio(string device, int rate, int bits) { SetAudioFormat(rate, bits); SetAudioDevice(device); }
    double AudioLeft => 0;
    double AudioRight => 0;
}
public interface IStateStore
{
    EngineState? Load();
    void Save(EngineState state);
    void Log(AsRun row);
    void Audit(string action, string detail);
}
public sealed partial class PlayoutController
{
    public EngineState State { get; }
    private readonly IStateStore store;
    private readonly IReadOnlyDictionary<int, IDecoder> decoders;
    private readonly Func<DateTimeOffset> now;
    private readonly Func<string, bool> exists;
    private readonly Dictionary<Guid, Stack<List<RundownItem>>> undo = [], redo = [];
    public PlayoutController(IStateStore store, IReadOnlyDictionary<int, IDecoder> decoders, Func<DateTimeOffset>? now = null, Func<string, bool>? exists = null)
    {
        this.store = store; this.decoders = decoders; this.now = now ?? (() => DateTimeOffset.Now); this.exists = exists ?? File.Exists;
        State = store.Load() ?? EngineState.Create();
        if (State.Version != 1) throw new InvalidOperationException("Unsupported database state version");
        foreach (var c in State.Channels)
        {
            decoders[c.Id].SetAudioFormat(c.AudioSampleRate, c.AudioBitDepth);
            decoders[c.Id].SetAutoLevel(c.AutoLevel);
            try { decoders[c.Id].SetAudioDevice(c.AudioDeviceId); } catch (Exception ex) { Alarm("Audio device unavailable: " + ex.Message, c.Id); c.AudioDeviceId = ""; decoders[c.Id].SetAudioDevice(""); }
            Finish(c, "Engine restart");
            if (c.LiveInputId is { } liveId && c.Status == "Playing" && !c.Emergency)
            {
                try { TakeLive(c, Media(liveId)); } catch (Exception ex) { Fail(c, ex.Message); }
                continue;
            }
            if (c.CurrentId is not null && !c.Emergency && c.Status is "Playing" or "Paused" or "Cued" or "Held")
            {
                var status = c.Status; var position = c.Position; var pass = c.Pass; var breakLoop = c.BreakLoop; var loopCurrent = c.LoopCurrent; var queued = c.QueuedId;
                try { Load(c, c.CurrentId.Value, status == "Playing", position); if (c.Status != "Error") { c.Status = status; c.Pass = pass; c.BreakLoop = breakLoop; c.LoopCurrent = loopCurrent; c.QueuedId = queued; } }
                catch (Exception ex) { Fail(c, ex.Message); }
            }
        }
        Save();
    }
    public ChannelState Channel(int id) => State.Channels.SingleOrDefault(c => c.Id == id) ?? throw new InvalidOperationException("Unknown channel");
    public Rundown Playlist(Guid id) => State.Rundowns.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Rundown not found");
    public RundownItem? Current(ChannelState c) { if (c.LiveInputId is not { } live) return Playlist(c.RundownId).Items.FirstOrDefault(i => i.Id == c.CurrentId); var item = NewItem(Media(live)); item.Id = live; return item; }
    public MediaAsset Media(Guid? id) => State.Media.SingleOrDefault(m => m.Id == id) ?? throw new InvalidOperationException("Media not found");
    public IReadOnlyList<AudioDeviceOption> AudioDevices(int channel) => decoders[Channel(channel).Id].AudioDevices();
    public Guid? NextItemId(ChannelState c)
    {
        var p = Playlist(c.RundownId);
        if (c.QueuedId is { } queued && p.Items.Any(i => i.Id == queued)) return queued;
        var index = p.Items.FindIndex(i => i.Id == c.CurrentId) + 1;
        if (index >= p.Items.Count && p.Loop) index = 0;
        return index < p.Items.Count ? p.Items[index].Id : null;
    }
    public PreflightReport Preflight(Guid rundownId)
    {
        var p = Playlist(rundownId); var issues = new List<PreflightIssue>();
        foreach (var item in p.Items)
        {
            try { Validate(item); if (item.MediaId is not null && !Available(Media(item.MediaId))) throw new InvalidOperationException("Media file missing or inaccessible"); }
            catch (InvalidOperationException ex) { issues.Add(new(item.Id, item.Title, ex.Message)); }
        }
        return new(p.Id, now(), p.Items.Count, issues);
    }
    public void Save() { State.SavedAt = now(); store.Save(State); }
    public void Alarm(string message, int? channel = null) { State.Alarms.Insert(0, new Alarm(Guid.NewGuid(), now(), channel, message)); if (State.Alarms.Count > 200) State.Alarms.RemoveRange(200, State.Alarms.Count - 200); }
    public void AddMedia(MediaAsset media) { var existing = State.Media.FirstOrDefault(m => string.Equals(m.Path, media.Path, StringComparison.OrdinalIgnoreCase)); if (existing is null) State.Media.Add(media); else existing.RemovedFromBrowser = false; Save(); }
    private void StartLog(ChannelState c) { c.RunId = Guid.NewGuid(); c.StartedAt = now(); c.PlayedSeconds = 0; }
    private void Finish(ChannelState c, string result)
    {
        if (c.RunId is { } id && c.StartedAt is { } at && Current(c) is { } item) store.Log(new AsRun(id, c.Id, item.Id, item.Title, at, now(), c.PlayedSeconds, c.Position, result));
        c.RunId = null; c.StartedAt = null; c.PlayedSeconds = 0;
    }
    private void Fail(ChannelState c, string error) { Finish(c, "Error"); decoders[c.Id].Stop(); c.Emergency = true; c.Interrupted = null; c.LiveInputId = null; c.PlaylistReturn = null; c.Status = "Error"; Alarm(error, c.Id); }
    private void Load(ChannelState c, Guid itemId, bool play, double? resume = null)
    {
        var item = Playlist(c.RundownId).Items.SingleOrDefault(i => i.Id == itemId) ?? throw new InvalidOperationException("Select a rundown item first");
        Validate(item);
        if (item.MediaId is not null && !Available(Media(item.MediaId)) && (c.Status == "Playing" || c.Emergency))
            throw new InvalidOperationException("Media unavailable; current output preserved: " + item.Title);
        Finish(c, "Replaced"); c.LiveInputId = null; c.PlaylistReturn = null; liveFrames.Remove(c.Id); c.CurrentId = item.Id; c.Position = resume ?? item.In; c.Pass = 1; c.BreakLoop = false; c.LoopCurrent = false; c.Rate = 1; c.Emergency = false; c.Interrupted = null;
        if (c.QueuedId == item.Id) c.QueuedId = null;
        var decoder = decoders[c.Id];
        if (item.MediaId is not null)
        {
            var media = Media(item.MediaId); if (!Available(media)) { Fail(c, "Missing media: " + media.Name); return; }
            decoder.Load(media, c.Position, play); decoder.SetAudio(c.GainDb + item.GainDb, c.Mute || media.Live?.Mute == true);
        }
        else decoder.Stop();
        c.Status = play ? "Playing" : "Cued"; if (play) StartLog(c);
    }
    private void Next(ChannelState c, int direction, bool manual)
    {
        var p = Playlist(c.RundownId); var index = direction == 1 ? p.Items.FindIndex(i => i.Id == NextItemId(c)) : p.Items.FindIndex(i => i.Id == c.CurrentId) + direction;
        if (index >= p.Items.Count && p.Loop) index = 0;
        if (index < 0 || index >= p.Items.Count) { Finish(c, "Completed"); decoders[c.Id].Pause(); c.Status = c.EndMode == "Black" ? "Stopped" : "Held"; return; }
        var item = p.Items[index]; Load(c, item.Id, manual || (c.AutoAdvance && item.StartMode != "Manual"));
    }
    public void Tick(double elapsed)
    {
        foreach (var c in State.Channels)
        {
            var d = decoders[c.Id]; c.DecodedFrames = d.Frames; c.AudioLeft = d.AudioLeft; c.AudioRight = d.AudioRight;
            if (c.Status != "Playing" || c.Emergency) continue;
            var item = Current(c); if (item is null) { Fail(c, "Playing rundown item disappeared"); continue; }
            if (item.MediaId is { } inputId && Media(inputId).Kind == "Live")
            {
                if (CheckLiveSignal(c, Media(inputId), elapsed)) continue;
                c.Position += elapsed; c.PlayedSeconds += elapsed;
                if (c.LiveInputId is not null || c.Position < item.Out) continue;
                Finish(c, "Live duration completed");
                try { Next(c, 1, false); } catch (InvalidOperationException ex) { Fail(c, ex.Message); }
                continue;
            }
            if (d.Error is { } error) { Fail(c, error); continue; }
            c.Position = item.MediaId is null || Media(item.MediaId).Kind == "Still" ? c.Position + elapsed : Math.Max(item.In, d.Position);
            c.PlayedSeconds += elapsed;
            if (c.Position + .005 < item.Out && !(d.Ended && item.Kind != "Still")) continue;
            c.Position = item.Out;
            if (!c.BreakLoop && (c.LoopPlayback || c.LoopCurrent || item.Loops == -1 || c.Pass < item.Loops))
            {
                Finish(c, "Loop"); c.Pass++; c.Position = item.In;
                if (item.MediaId is not null) { if (d.Ended) d.Load(Media(item.MediaId), item.In, true); else { d.Seek(item.In); d.Play(); } }
                StartLog(c);
            }
            else
            {
                Finish(c, "Completed"); d.Pause(); c.Status = "Held";
                if (c.EndMode == "Auto") { try { Next(c, 1, false); } catch (InvalidOperationException ex) { Fail(c, ex.Message); } }
                else c.Status = c.EndMode == "Black" ? "Stopped" : "Held";
            }
        }
        foreach (var scheduled in State.Schedule.Where(s => !s.Fired && s.At <= now()).ToArray())
        {
            scheduled.Fired = true;
            if (now() - scheduled.At > TimeSpan.FromSeconds(30)) { Alarm("Missed scheduled take: " + scheduled.Title, scheduled.Channel); continue; }
            try { var c = Channel(scheduled.Channel); Finish(c, "Scheduled interrupt"); c.RundownId = scheduled.RundownId; Load(c, scheduled.ItemId, true); }
            catch (Exception ex) { Alarm("Scheduled take failed: " + ex.Message, scheduled.Channel); }
            Save();
        }
    }
    public void Execute(EngineCommand command)
    {
        var c = Channel(command.Channel); var action = command.Action;
        var priorLiveReferences = State.Rundowns.SelectMany(p => p.Items).Where(i => i.Kind == "Live" && i.MediaId.HasValue).Select(i => i.MediaId!.Value).ToHashSet();
        if (action.StartsWith("Live.")) ExecuteLive(command, c);
        else if (action == "Media.Remove")
        {
            var ids = command.ItemIds.Distinct().ToArray();
            if (ids.Length == 0) throw new InvalidOperationException("Select clips in the browser first");
            var assets = ids.Select(id => Media(id)).ToArray();
            // Preserve playlist/cart references and playback while removing browser entries.
            foreach (var asset in assets) asset.RemovedFromBrowser = true;
        }
        else if (action == "Media.Colour")
        {
            var media = Media(command.MediaId);
            var requested = command.Colour ?? throw new InvalidOperationException("Colour settings required");
            requested.Validate();
            var correction = Json.Copy(requested);
            foreach (var channel in State.Channels.Where(ch => !ch.Emergency && Current(ch)?.MediaId == media.Id))
                decoders[channel.Id].SetColour(correction);
            media.Colour = correction;
        }
        else if (action == "Link") State.Linked = command.Enabled;
        else if (action == "Acknowledge") State.Alarms = State.Alarms.Select(a => a with { Acknowledged = true }).ToList();
        else if (action.StartsWith("Rundown.")) EditRundown(command, c);
        else if (action == "Schedule")
        {
            var s = command.Schedule ?? throw new InvalidOperationException("Schedule required"); var p = Playlist(s.RundownId);
            if ((!State.SharedRundown && p.Channel != s.Channel) || (State.SharedRundown && p.Id != Channel(s.Channel).RundownId) || !p.Items.Any(i => i.Id == s.ItemId) || s.At <= now()) throw new InvalidOperationException("Choose a valid future time and rundown item in the loaded rundown");
            State.Schedule.Add(s);
        }
        else if (action == "Schedule.Remove") State.Schedule.RemoveAll(s => s.Id == command.ItemId);
        else if (action == "Cart.Assign") { if (command.Index is < 0 or >= 48) throw new InvalidOperationException("Invalid cart"); if (command.MediaId is not null) Media(command.MediaId); State.Carts[command.Index] = command.MediaId; }
        else if (action == "Cart.Take")
        {
            if (command.Index is < 0 or >= 48) throw new InvalidOperationException("Invalid cart");
            var m = Media(State.Carts[command.Index]); var item = NewItem(m); var p = Playlist(c.RundownId); if (p.Locked) throw new InvalidOperationException("Unlock the rundown before inserting a cart"); if (!exists(m.Path)) throw new InvalidOperationException("Cart media is unavailable"); Checkpoint(p); var index = p.Items.FindIndex(i => i.Id == c.CurrentId); p.Items.Insert(index + 1, item); Load(c, item.Id, true);
        }
        else if (action == "WatchFolder") { if (!string.IsNullOrWhiteSpace(command.Path) && !Directory.Exists(command.Path)) throw new InvalidOperationException("Folder does not exist"); State.WatchFolder = command.Path ?? ""; }
        else
        {
            var targets = State.Linked && action is "Play" or "Pause" or "Stop" or "Next" ? State.Channels.ToArray() : [c];
            if (action == "Stop" && targets.Any(t => t.Status == "Playing" || t.Emergency) && !command.Confirm) throw new InvalidOperationException("Confirm stop before removing output");
            if (action == "Play" && targets.Any(t => t.Emergency || (Current(t) is null && Playlist(t.RundownId).Items.Count == 0))) throw new InvalidOperationException("Cue media in both linked channels and return from emergency before play");
            foreach (var target in targets) Transport(target, command);
        }
        if (action.StartsWith("Rundown."))
            foreach (var asset in State.Media.Where(m => m.Kind == "Live" && priorLiveReferences.Contains(m.Id) && !State.Rundowns.SelectMany(p => p.Items).Any(i => i.MediaId == m.Id))) asset.LiveReleaseRevision++;
        store.Audit(action, $"Channel {c.Id}; {command.ItemId}; {command.Text}"); Save();
    }
    private void Transport(ChannelState c, EngineCommand cmd)
    {
        var d = decoders[c.Id]; var item = Current(c);
        if (item?.Kind == "Live" && cmd.Action is "Seek" or "Step" or "Rate" or "Pause" or "LoopClip") throw new InvalidOperationException("Live inputs cannot seek, pause or loop. Use Back to Playlist or Take another source.");
        switch (cmd.Action)
        {
            case "Cue":
                var queued = Playlist(c.RundownId).Items.SingleOrDefault(i => i.Id == cmd.ItemId) ?? throw new InvalidOperationException("Select a rundown item");
                Validate(queued); if (queued.MediaId is not null && !Available(Media(queued.MediaId))) throw new InvalidOperationException("Cannot cue missing media: " + queued.Title);
                c.QueuedId = queued.Id; break;
            case "ClearCue": c.QueuedId = null; break;
            case "Park":
                if ((c.Status == "Playing" || c.Emergency) && !cmd.Confirm) throw new InvalidOperationException("Confirm Park: this replaces current output with the selected first frame");
                Load(c, cmd.ItemId ?? throw new InvalidOperationException("Select a rundown item"), false); break;
            case "Take": Load(c, cmd.ItemId ?? throw new InvalidOperationException("Select a rundown item"), true); break;
            case "AutoAdvance": c.AutoAdvance = cmd.Enabled; break;
            case "Play":
                if (c.Status == "Playing") break;
                if (item is null) Load(c, c.QueuedId ?? Playlist(c.RundownId).Items.First().Id, true);
                else if (c.Status is "Stopped" or "Held" || d.Ended) Load(c, item.Id, true);
                else { d.Play(); c.Status = "Playing"; c.Rate = 1; d.SetRate(1); StartLog(c); }
                break;
            case "Pause": if (c.Status == "Playing") { d.Pause(); Finish(c, "Paused"); c.Status = "Paused"; } break;
            case "Stop": d.Stop(); Finish(c, "Stopped"); c.Status = "Stopped"; c.Emergency = false; c.Interrupted = null; c.LiveInputId = null; c.PlaylistReturn = null; c.Position = item?.In ?? 0; break;
            case "Next": Next(c, 1, true); break;
            case "Previous": Next(c, -1, true); break;
            case "Seek": if (item is null) throw new InvalidOperationException("Cue an item first"); c.Position = Number(cmd.Value, item.In, item.Out); d.Seek(c.Position); break;
            case "Step":
                if (item is null) throw new InvalidOperationException("Cue an item first"); d.Pause(); Finish(c, "Paused"); c.Status = "Paused"; c.Position = Math.Clamp(c.Position + cmd.Value / c.Fps, item.In, item.Out); d.Seek(c.Position); break;
            case "Rate": c.Rate = Number(cmd.Value, .25, 8); d.SetRate(c.Rate); break;
            case "LoopClip": if (item is null) throw new InvalidOperationException("Play or park a clip first"); c.LoopCurrent = cmd.Enabled; c.BreakLoop = !cmd.Enabled; break;
            case "LoopPlayback": c.LoopPlayback = cmd.Enabled; c.BreakLoop = false; break;
            case "BreakLoop": c.LoopCurrent = false; c.BreakLoop = true; break;
            case "Emergency":
                if (!c.Emergency) { c.Interrupted = new ResumePoint(c.CurrentId, c.Position, c.Status, c.Pass); d.Pause(); Finish(c, "Emergency interrupt"); c.Emergency = true; c.Status = "Emergency"; } break;
            case "Return":
                if (c.Interrupted is { } resume) { c.CurrentId = resume.ItemId; c.Position = resume.Position; c.Status = resume.Status; c.Pass = resume.Pass; if (c.Status == "Playing") { d.Play(); StartLog(c); } } else c.Status = "Stopped";
                c.Emergency = false; c.Interrupted = null; break;
            case "Mute": c.Mute = cmd.Enabled; d.SetAudio(c.GainDb + (item?.GainDb ?? 0), c.Mute); break;
            case "AudioDevice":
                if (c.Status == "Playing" || c.Emergency) throw new InvalidOperationException("Pause or stop before changing audio output");
                var device = cmd.Text ?? "";
                if (device.Length != 0 && !d.AudioDevices().Any(a => a.Id == device)) throw new InvalidOperationException("Selected audio device is no longer available");
                d.SetAudioDevice(device); c.AudioDeviceId = device; break;
            case "AudioFormat":
                if (c.Status == "Playing" || c.Emergency) throw new InvalidOperationException("Pause or stop before changing audio format");
                if (cmd.Value is not (32000 or 44100 or 48000 or 88200 or 96000 or 176400 or 192000) || cmd.Index is not (16 or 24 or 32)) throw new InvalidOperationException("Unsupported PCM format");
                d.SetAudioFormat((int)cmd.Value, cmd.Index); c.AudioSampleRate = (int)cmd.Value; c.AudioBitDepth = cmd.Index; break;
            case "AudioProfile":
                if (c.Status == "Playing" || c.Emergency) throw new InvalidOperationException("Pause or stop before changing audio routing");
                var endpoint = cmd.Text ?? "";
                if (cmd.Value is not (32000 or 44100 or 48000 or 88200 or 96000 or 176400 or 192000) || cmd.Index is not (16 or 24 or 32)) throw new InvalidOperationException("Unsupported PCM format");
                if (endpoint.Length > 0 && !d.AudioDevices().Any(a => a.Id == endpoint)) throw new InvalidOperationException("Audio device unavailable");
                d.ConfigureAudio(endpoint, (int)cmd.Value, cmd.Index); c.AudioDeviceId = endpoint; c.AudioSampleRate = (int)cmd.Value; c.AudioBitDepth = cmd.Index; break;
            case "AudioAutoLevel": var level = cmd.AutoLevel ?? throw new InvalidOperationException("Missing audio leveling settings"); level.Validate(); d.SetAutoLevel(level); c.AutoLevel = level with { }; break;
            case "Gain": c.GainDb = Number(cmd.Value, -60, 12); d.SetAudio(c.GainDb + (item?.GainDb ?? 0), c.Mute); break;
            case "EndMode": if (cmd.Text is not ("Auto" or "Hold" or "Black")) throw new InvalidOperationException("Invalid end mode"); c.EndMode = cmd.Text; break;
            case "Format": if (c.Status == "Playing") throw new InvalidOperationException("Stop before changing format"); if (!Timecode.Formats.Contains(cmd.Text)) throw new InvalidOperationException("Invalid format"); c.Format = cmd.Text!; break;
            case "Graphics": c.Graphics = cmd.Graphics ?? new(); c.Graphics.TickerSpeed = Math.Clamp(c.Graphics.TickerSpeed, 10, 300); break;
            default: throw new InvalidOperationException("Unknown command: " + cmd.Action);
        }
    }
    public static double Number(double n, double min, double max) => double.IsFinite(n) && n >= min && n <= max ? n : throw new InvalidOperationException($"Value must be between {min} and {max}");
    public static RundownItem NewItem(MediaAsset m) => new() { MediaId = m.Id, Kind = m.Kind, Title = m.Name, Out = m.Duration, GainDb = m.Live?.GainDb ?? 0 };
    private void Validate(RundownItem item)
    {
        var max = item.MediaId is null ? 86400 : Media(item.MediaId).Duration;
        Number(item.In, 0, max); Number(item.Out, item.In + .001, max); Number(item.GainDb, -60, 12);
        if (item.Loops != -1 && item.Loops is < 1 or > 100000) throw new InvalidOperationException("Loop passes must be −1 (infinite) or 1–100000");
        if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 200) throw new InvalidOperationException("Enter a title, up to 200 characters");
        if (item.StartMode is not ("Auto" or "Manual" or "Floating")) throw new InvalidOperationException("Invalid start mode");
        if (item.MediaId is null && item.Kind != "Slate") throw new InvalidOperationException("Only a slate can have no media");
    }
    private void Checkpoint(Rundown p) { if (!undo.TryGetValue(p.Id, out var history)) undo[p.Id] = history = new(); history.Push(Json.Copy(p.Items)); redo[p.Id] = new(); }
    private void EditRundown(EngineCommand cmd, ChannelState c)
    {
        if (cmd.Action == "Rundown.Share")
        {
            if (State.SharedRundown) return;
            if (State.Channels.Any(ch => ch.Status == "Playing" || ch.Emergency || ch.LiveInputId is not null)) throw new InvalidOperationException("Stop both ports and return from Live before enabling the shared rundown");
            if (State.Schedule.Any(s => !s.Fired)) throw new InvalidOperationException("Clear pending schedules before enabling the shared rundown");
            var shared = Playlist(Channel(1).RundownId);
            var additions = State.Channels.SelectMany(ch => Playlist(ch.RundownId).Items).Where(i => shared.Items.All(existing => existing.Id != i.Id)).DistinctBy(i => i.Id).Select(Json.Copy).ToList();
            if (shared.Items.Count + additions.Count > 10000) throw new InvalidOperationException("Shared rundown exceeds 10000 clips");
            shared.Items.AddRange(additions);
            foreach (var ch in State.Channels) ch.RundownId = shared.Id;
            State.SharedRundown = true; return;
        }
        if (cmd.Action == "Rundown.Create") { State.Rundowns.Add(new Rundown { Channel = c.Id, Name = cmd.Text ?? "Untitled rundown" }); return; }
        if (cmd.Action == "Rundown.Import") { var imported = cmd.Rundown ?? throw new InvalidOperationException("Invalid rundown"); if (imported.Items.Count > 10000) throw new InvalidOperationException("Maximum 10000 items"); foreach (var i in imported.Items) Validate(i); imported.Id = Guid.NewGuid(); imported.Channel = c.Id; foreach (var i in imported.Items) i.Id = Guid.NewGuid(); State.Rundowns.Add(imported); return; }
        var p = Playlist(cmd.RundownId ?? c.RundownId);
        if (cmd.Action == "Rundown.Lock") { p.Locked = cmd.Enabled; return; }
        if (p.Locked && cmd.Action is not ("Rundown.Load" or "Rundown.Duplicate")) throw new InvalidOperationException("Rundown is locked; unlock it to edit");
        var active = State.Channels.Where(ch => ch.RundownId == p.Id && (ch.Status == "Playing" || ch.Emergency)).ToArray();
        bool Locked(Guid id) => active.Any(ch => ch.CurrentId == id || ch.Interrupted?.ItemId == id ||
            (ch.AutoAdvance && ch.EndMode == "Auto" && NextItemId(ch) == id && Current(ch) is { } current && current.Out - ch.Position <= 5 && (ch.BreakLoop || (!ch.LoopCurrent && current.Loops != -1 && ch.Pass >= current.Loops))));
        switch (cmd.Action)
        {
            case "Rundown.Clear":
                if (active.Length != 0) throw new InvalidOperationException("Stop this port before removing all clips");
                Checkpoint(p); p.Items.Clear(); foreach (var ch in State.Channels.Where(ch => ch.RundownId == p.Id)) { decoders[ch.Id].Stop(); ch.CurrentId = ch.QueuedId = ch.LiveInputId = null; ch.PlaylistReturn = null; ch.Status = "Stopped"; ch.Position = 0; }
                break;
            case "Rundown.RemoveMany":
                if (cmd.ItemIds.Count == 0 || cmd.ItemIds.Any(id => p.Items.All(i => i.Id != id) || Locked(id))) throw new InvalidOperationException("Selection includes a missing, playing or imminent clip");
                Checkpoint(p); p.Items.RemoveAll(i => cmd.ItemIds.Contains(i.Id)); foreach (var ch in State.Channels.Where(ch => ch.RundownId == p.Id)) { if (ch.QueuedId is { } queued && cmd.ItemIds.Contains(queued)) ch.QueuedId = null; if (ch.CurrentId is { } currentId && cmd.ItemIds.Contains(currentId)) { decoders[ch.Id].Stop(); ch.CurrentId = null; ch.Position = 0; ch.Status = "Stopped"; } }
                break;
            case "Rundown.Paste":
                if (cmd.Items.Count == 0 || cmd.Items.Count + p.Items.Count > 10000) throw new InvalidOperationException("Paste requires 1–10000 clips within the playlist limit");
                var pasted = Json.Copy(cmd.Items); foreach (var clip in pasted) { Validate(clip); if (clip.MediaId is { } mediaId) _ = Media(mediaId); clip.Id = Guid.NewGuid(); }
                var pasteAt = cmd.ItemId is { } target ? p.Items.FindIndex(i => i.Id == target) + 1 : p.Items.Count;
                if (cmd.ItemId is not null && pasteAt == 0) throw new InvalidOperationException("Paste target no longer exists");
                if (active.Any(ch => pasteAt <= p.Items.FindIndex(i => i.Id == ch.CurrentId) + 1 && NextItemId(ch) is { } next && Locked(next))) throw new InvalidOperationException("Cannot paste ahead of imminent next clip");
                Checkpoint(p); p.Items.InsertRange(pasteAt, pasted); break;
            case "Rundown.Load":
                var targets = State.SharedRundown ? State.Channels.ToArray() : [c];
                if (targets.Any(ch => ch.Status == "Playing" || ch.Emergency || ch.LiveInputId is not null)) throw new InvalidOperationException("Stop affected ports before loading another rundown");
                if (!State.SharedRundown && p.Channel != c.Id) throw new InvalidOperationException("Rundown belongs to another channel");
                foreach (var ch in targets) { decoders[ch.Id].Stop(); ch.RundownId = p.Id; ch.CurrentId = ch.QueuedId = null; ch.Position = 0; ch.Status = "Stopped"; }
                break;
            case "Rundown.Rename": p.Name = cmd.Text ?? p.Name; break;
            case "Rundown.Duplicate": var copy = Json.Copy(p); copy.Id = Guid.NewGuid(); copy.Name += " (copy)"; foreach (var i in copy.Items) i.Id = Guid.NewGuid(); State.Rundowns.Add(copy); break;
            case "Rundown.Delete": if (State.Channels.Any(ch => ch.RundownId == p.Id) || State.Schedule.Any(s => !s.Fired && s.RundownId == p.Id)) throw new InvalidOperationException("Unload this rundown and remove pending schedules first"); State.Rundowns.Remove(p); break;
            case "Rundown.Loop": p.Loop = cmd.Enabled; break;
            case "Rundown.Add":
                var added = cmd.Item ?? NewItem(Media(cmd.MediaId)); Validate(added); added.Id = Guid.NewGuid();
                var insertAt = cmd.ItemId is { } after ? p.Items.FindIndex(i => i.Id == after) + 1 : p.Items.Count;
                if (cmd.ItemId is not null && insertAt == 0) throw new InvalidOperationException("Insertion target not found");
                var proposed = p.Items.ToList(); proposed.Insert(insertAt, added);
                foreach (var ch in active.Where(ch => ch.QueuedId is null))
                {
                    var imminent = NextItemId(ch); if (imminent is null || !Locked(imminent.Value)) continue;
                    var proposedNext = proposed.FindIndex(i => i.Id == ch.CurrentId) + 1; if (proposedNext >= proposed.Count && p.Loop) proposedNext = 0;
                    if (proposed.ElementAtOrDefault(proposedNext)?.Id != imminent) throw new InvalidOperationException("Cannot insert before imminent next item; use Cue or Take for an intentional override");
                }
                Checkpoint(p); p.Items.Insert(insertAt, added); break;
            case "Rundown.AddMedia":
                if (cmd.ItemIds.Count is < 1 or > 10000 || p.Items.Count + cmd.ItemIds.Count > 10000 || cmd.ItemIds.Distinct().Count() != cmd.ItemIds.Count)
                    throw new InvalidOperationException("Select 1–10000 unique media clips within the rundown limit");
                var mediaItems = cmd.ItemIds.Select(id => NewItem(Media(id))).ToList();
                foreach (var clip in mediaItems) { Validate(clip); clip.Id = Guid.NewGuid(); }
                var mediaInsertAt = cmd.Index < 0 ? p.Items.Count : Math.Clamp(cmd.Index, 0, p.Items.Count);
                var mediaProposed = p.Items.ToList(); mediaProposed.InsertRange(mediaInsertAt, mediaItems);
                foreach (var ch in active.Where(ch => ch.QueuedId is null))
                {
                    var imminent = NextItemId(ch); if (imminent is null || !Locked(imminent.Value)) continue;
                    var proposedNext = mediaProposed.FindIndex(i => i.Id == ch.CurrentId) + 1; if (proposedNext >= mediaProposed.Count && p.Loop) proposedNext = 0;
                    if (mediaProposed.ElementAtOrDefault(proposedNext)?.Id != imminent) throw new InvalidOperationException("Cannot insert before imminent next item; choose another rundown position");
                }
                Checkpoint(p); p.Items.InsertRange(mediaInsertAt, mediaItems); break;
            case "Rundown.Update":
                var old = p.Items.SingleOrDefault(i => i.Id == cmd.ItemId) ?? throw new InvalidOperationException("Item not found"); if (Locked(old.Id)) throw new InvalidOperationException("Playing or imminent next item is protected");
                var replacement = cmd.Item ?? throw new InvalidOperationException("Item required"); Validate(replacement); replacement.Id = old.Id; Checkpoint(p); p.Items[p.Items.IndexOf(old)] = replacement; break;
            case "Rundown.Remove": if (cmd.ItemId is null || Locked(cmd.ItemId.Value)) throw new InvalidOperationException("Cannot delete a playing or imminent next item"); Checkpoint(p); p.Items.RemoveAll(i => i.Id == cmd.ItemId); foreach (var ch in State.Channels.Where(ch => ch.RundownId == p.Id && ch.QueuedId == cmd.ItemId)) ch.QueuedId = null; break;
            case "Rundown.Move":
                var index = p.Items.FindIndex(i => i.Id == cmd.ItemId); if (index < 0 || cmd.Index < 0 || cmd.Index >= p.Items.Count) throw new InvalidOperationException("Invalid item position");
                if (active.Any(ch => index <= p.Items.FindIndex(i => i.Id == ch.CurrentId) || cmd.Index <= p.Items.FindIndex(i => i.Id == ch.CurrentId))) throw new InvalidOperationException("Only upcoming items can move during playback");
                if (p.Items.Skip(Math.Min(index, cmd.Index)).Take(Math.Abs(index - cmd.Index) + 1).Any(i => Locked(i.Id))) throw new InvalidOperationException("Imminent next item is protected");
                Checkpoint(p); var moved = p.Items[index]; p.Items.RemoveAt(index); p.Items.Insert(cmd.Index, moved); break;
            case "Rundown.Undo": case "Rundown.Redo":
                if (active.Length != 0) throw new InvalidOperationException("Stop before undo/redo");
                var from = cmd.Action == "Rundown.Undo" ? undo : redo; var to = cmd.Action == "Rundown.Undo" ? redo : undo;
                if (!from.TryGetValue(p.Id, out var stack) || stack.Count == 0) throw new InvalidOperationException("No edit history"); if (!to.TryGetValue(p.Id, out var other)) to[p.Id] = other = new(); other.Push(Json.Copy(p.Items)); p.Items = stack.Pop(); break;
            default: throw new InvalidOperationException("Unknown rundown command");
        }
    }
}
