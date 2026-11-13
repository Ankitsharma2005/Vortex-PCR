using System.Runtime.InteropServices;

public interface ILegacyApiProbe
{
    void Read(out long value);
    void Fail();
}
public interface IUnavailableApiProbe { }
public sealed class LegacyApiProbe : ILegacyApiProbe
{
    public void Read(out long value) => value = 48000;
    public void Fail() => throw new COMException("Driver rejection", unchecked((int)0x80070057));
}
