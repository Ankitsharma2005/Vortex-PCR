using LibVLCSharp.Shared;
using NewsPlay.Core;

namespace NewsPlay.Engine;

public sealed class MediaProbe(LibVLC vlc)
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".dv", ".hdv", ".m2v", ".mts", ".ac3", ".mp4", ".mov", ".mxf", ".avi", ".mkv", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".m4v", ".mp3", ".wav", ".aac", ".m4a", ".flac", ".ogg", ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".webp" };
    public async Task<MediaAsset> Inspect(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path) || !Extensions.Contains(System.IO.Path.GetExtension(path))) throw new InvalidOperationException("Unsupported file or file does not exist: " + path);
        using var media = new Media(vlc, path, FromType.FromPath);
        var status = await media.Parse(MediaParseOptions.ParseLocal, 15000);
        // LibVLC may complete the parsing task on its native event thread.
        // Metadata inspection and disposal must run outside that callback.
        await TaskSchedulerBoundary();
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var still = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp" }.Contains(ext);
        var video = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
        var audio = media.Tracks.Count(t => t.TrackType == TrackType.Audio);
        var seconds = still ? 10 : media.Duration / 1000d;
        if (seconds <= 0 || !double.IsFinite(seconds)) throw new InvalidOperationException("Could not determine duration: " + System.IO.Path.GetFileName(path));
        var v = video.Data.Video;
        return new MediaAsset { Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path), Kind = still ? "Still" : video.TrackType == TrackType.Video ? "Clip" : "Audio", Duration = seconds, Width = (int)v.Width, Height = (int)v.Height, Fps = v.FrameRateDen > 0 ? (double)v.FrameRateNum / v.FrameRateDen : 25, Codec = video.Codec != 0 ? System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(video.Codec)).Trim('\0') : ext.TrimStart('.'), AudioTracks = audio, Warning = !still && audio == 0 ? "No audio track" : status != MediaParsedStatus.Done ? "Metadata partially parsed" : "" };
    }
    private static async Task TaskSchedulerBoundary() => await Task.Delay(1).ConfigureAwait(false);
}
