namespace NewsPlay.Core;

public static class LiveUsage
{
    public static bool IsOnAir(EngineState state, Guid id) => state.Channels.Any(c =>
        (c.Status == "Playing" || c.Emergency) && (c.LiveInputId == id ||
        (c.LiveInputId is null && state.Rundowns.FirstOrDefault(p => p.Id == c.RundownId)?.Items.Any(i => i.Id == c.CurrentId && i.MediaId == id) == true)));
}
