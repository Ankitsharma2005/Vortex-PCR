namespace NewsPlay.Core;
public static class BroadcastRange
{
    // UYVY 4:2:2 nominal digital video range; this is not an EBU compliance analyser.
    public static void ClampUyvy(Span<byte> frame, int width, int height, int stride)
    {
        if(width<2||(width&1)!=0||height<1||stride<width*2||frame.Length<stride*height)throw new ArgumentException("Invalid UYVY raster");
        for(var y=0;y<height;y++)for(var x=0;x<width*2;x+=2){var i=y*stride+x;frame[i]=(byte)Math.Clamp((int)frame[i],16,240);frame[i+1]=(byte)Math.Clamp((int)frame[i+1],16,235);}
    }
}
