using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DeckLinkAPI;

namespace NewsPlay.Engine;

// Calls the exact vendor interface selected by QueryInterface; no guessed vtable slots.
public sealed class DeckLinkApiView
{
    public object Instance { get; }
    public Type Interface { get; }
    private readonly Dictionary<string, MethodInfo> methods;
    public DeckLinkApiView(object instance, Type type)
    {
        if (!type.IsInstanceOfType(instance)) throw new InvalidCastException("Interface unavailable: " + type.Name);
        Instance = instance; Interface = type;
        methods = type.GetMethods().ToDictionary(m => m.Name);
    }
    public bool Has(string name) => methods.ContainsKey(name);
    public object? Call(string name, params object?[] args)
    {
        try { return methods[name].Invoke(Instance, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    public static DeckLinkApiView? Find(object instance, params Type[] candidates)
    {
        foreach (var type in candidates) if (type.IsInstanceOfType(instance)) return new(instance, type);
        return null;
    }
}

public static class DeckLinkCompatibility
{
    public static readonly Type[] OutputInterfaces = [typeof(IDeckLinkOutput), typeof(IDeckLinkOutput_v15_3_1), typeof(IDeckLinkOutput_v14_2_1), typeof(IDeckLinkOutput_v11_4), typeof(IDeckLinkOutput_v10_11)];
    public static IDeckLinkIterator Iterator()
    {
        foreach (var type in new[] { typeof(CDeckLinkIteratorClass), typeof(CDeckLinkIterator_v10_11Class), typeof(CDeckLinkIterator_v10_8Class) })
        {
            try { return (IDeckLinkIterator)Activator.CreateInstance(type)!; }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80040154)) { }
            catch (TargetInvocationException ex) when (ex.InnerException is COMException com && com.HResult == unchecked((int)0x80040154)) { }
        }
        throw new InvalidOperationException("Blackmagic Desktop Video driver is not registered. Install the driver recommended for this card and restart Windows.");
    }
    public static DeckLinkApiView? Attributes(object device) => DeckLinkApiView.Find(device, typeof(IDeckLinkProfileAttributes), typeof(IDeckLinkProfileAttributes_v15_3_1), typeof(IDeckLinkAttributes_v10_11));
    public static DeckLinkApiView? Configuration(object device) => DeckLinkApiView.Find(device, typeof(IDeckLinkConfiguration), typeof(IDeckLinkConfiguration_v15_3_1), typeof(IDeckLinkConfiguration_v10_11), typeof(IDeckLinkConfiguration_v10_9), typeof(IDeckLinkConfiguration_v10_4), typeof(IDeckLinkConfiguration_v10_2));
    public static long? Attribute(object device, _BMDDeckLinkAttributeID id)
    {
        var api = Attributes(device); if (api is null) return null;
        object?[] args = [id, 0L];
        try { api.Call("GetInt", args); return (long)args[1]!; }
        catch (COMException ex) when (ex.HResult is unchecked((int)0x80004001) or unchecked((int)0x80070057) or unchecked((int)0x80004002)) { return null; }
    }
    public static string Identity(IDeckLink device)
    {
        var id = Attribute(device, _BMDDeckLinkAttributeID.BMDDeckLinkPersistentID);
        var sub = Attribute(device, _BMDDeckLinkAttributeID.BMDDeckLinkSubDeviceIndex) ?? 0;
        if (id is not null) return $"{id:X16}:{sub}";
        device.GetDisplayName(out var name);
        return "name:" + name + ":" + sub;
    }
    public static void WriteFrame(object frame, byte[] pixels)
    {
        var buffer = DeckLinkApiView.Find(frame, typeof(IDeckLinkVideoBuffer), typeof(IDeckLinkVideoBuffer_v15_3_1));
        var api = buffer ?? DeckLinkApiView.Find(frame, typeof(IDeckLinkMutableVideoFrame_v14_2_1), typeof(IDeckLinkVideoFrame_v14_2_1)) ?? throw new InvalidOperationException("Driver frame-buffer interface is unsupported");
        if (buffer is not null) api.Call("StartAccess", _BMDBufferAccessFlags.bmdBufferAccessWrite);
        try { object?[] args = [IntPtr.Zero]; api.Call("GetBytes", args); var pointer = (IntPtr)args[0]!; if (pointer == IntPtr.Zero) throw new InvalidOperationException("DeckLink returned an empty frame buffer"); Marshal.Copy(pixels, 0, pointer, pixels.Length); }
        finally { if (buffer is not null) api.Call("EndAccess", _BMDBufferAccessFlags.bmdBufferAccessWrite); }
    }
    public static bool TagColour(object frame, bool hd)
    {
        try { var api=DeckLinkApiView.Find(frame,typeof(IDeckLinkVideoFrameMutableMetadataExtensions));if(api is null)return false;api.Call("SetInt",_BMDDeckLinkFrameMetadataID.bmdDeckLinkFrameMetadataColorspace,(long)(hd?_BMDColorspace.bmdColorspaceRec709:_BMDColorspace.bmdColorspaceRec601));return true; }
        catch(COMException){return false;}
    }
    public static unsafe void LegalizeFrame(object frame,int width,int height,int stride)
    {
        var buffer=DeckLinkApiView.Find(frame,typeof(IDeckLinkVideoBuffer),typeof(IDeckLinkVideoBuffer_v15_3_1));
        var api=buffer??DeckLinkApiView.Find(frame,typeof(IDeckLinkMutableVideoFrame_v14_2_1),typeof(IDeckLinkVideoFrame_v14_2_1))??throw new InvalidOperationException("Frame-buffer interface unsupported");
        var access=_BMDBufferAccessFlags.bmdBufferAccessRead|_BMDBufferAccessFlags.bmdBufferAccessWrite;
        if(buffer is not null)api.Call("StartAccess",access);
        try{object?[] args=[IntPtr.Zero];api.Call("GetBytes",args);var pointer=(IntPtr)args[0]!;if(pointer==IntPtr.Zero)throw new InvalidOperationException("Empty DeckLink frame");NewsPlay.Core.BroadcastRange.ClampUyvy(new Span<byte>(pointer.ToPointer(),checked(stride*height)),width,height,stride);}
        finally{if(buffer is not null)api.Call("EndAccess",access);}
    }
    public static DeckLinkApiView Converter(bool legacyFrames)
    {
        Type[] interfaces = legacyFrames ? [typeof(IDeckLinkVideoConversion_v14_2_1)] : [typeof(IDeckLinkVideoConversion), typeof(IDeckLinkVideoConversion_v15_3_1)];
        Type[] classes = legacyFrames ? [typeof(CDeckLinkVideoConversion_v14_2_1Class), typeof(CDeckLinkVideoConversionClass)] : [typeof(CDeckLinkVideoConversionClass), typeof(CDeckLinkVideoConversion_v15_3_1Class)];
        foreach (var type in classes)
        {
            object? instance = null;
            try { instance = Activator.CreateInstance(type)!; var api = DeckLinkApiView.Find(instance, interfaces); if (api is not null) return api; }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80040154)) { }
            catch (TargetInvocationException ex) when (ex.InnerException is COMException com && com.HResult == unchecked((int)0x80040154)) { }
            DeckLinkOutputService.Release(instance);
        }
        throw new InvalidOperationException("The driver has no compatible video conversion interface; repair Desktop Video installation");
    }
}

public sealed class CompatibleDeckLinkOutput
{
    private readonly DeckLinkApiView api;
    public string ApiName => api.Interface.Name;
    public bool LegacyFrames => api.Interface == typeof(IDeckLinkOutput_v14_2_1) || api.Interface == typeof(IDeckLinkOutput_v11_4) || api.Interface == typeof(IDeckLinkOutput_v10_11);
    public CompatibleDeckLinkOutput(object device) => api = DeckLinkApiView.Find(device, DeckLinkCompatibility.OutputInterfaces) ?? throw new InvalidOperationException("Device exposes no supported DeckLink playback interface (current, 15.3, 14.2, 11.4 or 10.11 APIs)");
    public void GetDisplayModeIterator(out IDeckLinkDisplayModeIterator iterator) { object?[] args = [null]; api.Call("GetDisplayModeIterator", args); iterator = (IDeckLinkDisplayModeIterator)args[0]!; }
    public void GetDisplayMode(_BMDDisplayMode id, out IDeckLinkDisplayMode mode)
    {
        if (api.Has("GetDisplayMode")) { object?[] args = [id, null]; api.Call("GetDisplayMode", args); mode = (IDeckLinkDisplayMode)args[1]!; return; }
        GetDisplayModeIterator(out var iterator);
        try { while (true) { iterator.Next(out var next); if (next is null) break; if (next.GetDisplayMode() == id) { mode = next; return; } DeckLinkOutputService.Release(next); } }
        finally { DeckLinkOutputService.Release(iterator); }
        throw new InvalidOperationException("Video format is no longer available on this device/profile");
    }
    public void DoesSupportVideoMode(_BMDVideoConnection connection, _BMDDisplayMode mode, _BMDPixelFormat pixel, _BMDVideoOutputConversionMode conversion, _BMDSupportedVideoModeFlags flags, out _BMDDisplayMode actual, out int supported)
    {
        if (api.Interface == typeof(IDeckLinkOutput_v10_11))
        {
            object?[] args = [mode, pixel, _BMDVideoOutputFlags.bmdVideoOutputFlagDefault, _BMDDisplayModeSupport_v10_11.bmdDisplayModeNotSupported_v10_11, null];
            api.Call("DoesSupportVideoMode", args); var display = args[4] as IDeckLinkDisplayMode;
            try { actual = display?.GetDisplayMode() ?? mode; supported = (_BMDDisplayModeSupport_v10_11)args[3]! == _BMDDisplayModeSupport_v10_11.bmdDisplayModeSupported_v10_11 ? 1 : 0; } finally { DeckLinkOutputService.Release(display); }
        }
        else if (api.Interface == typeof(IDeckLinkOutput_v11_4)) { object?[] args = [connection, mode, pixel, flags, mode, 0]; api.Call("DoesSupportVideoMode", args); actual = (_BMDDisplayMode)args[4]!; supported = (int)args[5]!; }
        else { object?[] args = [connection, mode, pixel, conversion, flags, mode, 0]; api.Call("DoesSupportVideoMode", args); actual = (_BMDDisplayMode)args[5]!; supported = (int)args[6]!; }
    }
    public void EnableVideoOutput(_BMDDisplayMode mode, _BMDVideoOutputFlags flags) => api.Call("EnableVideoOutput", mode, flags);
    public void DisableVideoOutput() => api.Call("DisableVideoOutput");
    public void RowBytesForPixelFormat(_BMDPixelFormat pixel, int width, out int stride)
    {
        if (api.Has("RowBytesForPixelFormat")) { object?[] args = [pixel, width, 0]; api.Call("RowBytesForPixelFormat", args); stride = (int)args[2]!; }
        else stride = pixel == _BMDPixelFormat.bmdFormat8BitBGRA ? checked(width * 4) : pixel == _BMDPixelFormat.bmdFormat8BitYUV ? checked(width * 2) : throw new InvalidOperationException("Unsupported legacy pixel format");
    }
    public void CreateVideoFrame(int width, int height, int stride, _BMDPixelFormat pixel, _BMDFrameFlags flags, out object frame) { object?[] args = [width, height, stride, pixel, flags, null]; api.Call("CreateVideoFrame", args); frame = args[5]!; }
    public void ScheduleVideoFrame(object frame, long time, long duration, long scale) => api.Call("ScheduleVideoFrame", frame, time, duration, scale);
    public void GetBufferedVideoFrameCount(out uint count) { object?[] args = [0u]; api.Call("GetBufferedVideoFrameCount", args); count = (uint)args[0]!; }
    public long ScheduledTime(long scale) { object?[] args = [scale, 0L, 0d]; api.Call("GetScheduledStreamTime", args); return (long)args[1]!; }
    public void EnableAudioOutput(_BMDAudioSampleRate rate, _BMDAudioSampleType type, uint channels, _BMDAudioOutputStreamType stream) => api.Call("EnableAudioOutput", rate, type, channels, stream);
    public void DisableAudioOutput() => api.Call("DisableAudioOutput");
    public void FlushBufferedAudioSamples() => api.Call("FlushBufferedAudioSamples");
    public void GetBufferedAudioSampleFrameCount(out uint count) { object?[] args = [0u]; api.Call("GetBufferedAudioSampleFrameCount", args); count = (uint)args[0]!; }
    public void ScheduleAudioSamples(IntPtr buffer, uint count, long time, long scale, out uint written) { object?[] args = [buffer, count, time, scale, 0u]; api.Call("ScheduleAudioSamples", args); written = (uint)args[4]!; }
    public void StartScheduledPlayback(long time, long scale, double speed) => api.Call("StartScheduledPlayback", time, scale, speed);
    public void StopScheduledPlayback(long time, out long actual, long scale) { object?[] args = [time, 0L, scale]; api.Call("StopScheduledPlayback", args); actual = (long)args[1]!; }
}
