param([Parameter(Mandatory=$true)][string]$SourcePng)
$ErrorActionPreference = 'Stop'
$assetDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'desktop\NewsPlay.Desktop\Assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
public static class NewsPlayBrandAssets
{
    public static void Prepare(string source, string directory)
    {
        using (var original = new Bitmap(source))
        {
            int left=original.Width, top=original.Height, right=-1, bottom=-1;
            for(int y=0;y<original.Height;y++) for(int x=0;x<original.Width;x++)
                if(original.GetPixel(x,y).A>8) { left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y); }
            if(right<left) throw new InvalidOperationException("Logo is empty");
            var bounds = Rectangle.FromLTRB(left,top,right+1,bottom+1);
            using (var square = new Bitmap(512,512,PixelFormat.Format32bppArgb))
            {
                using(var graphics=Graphics.FromImage(square))
                {
                    graphics.Clear(Color.Transparent); graphics.CompositingMode=CompositingMode.SourceCopy;
                    graphics.InterpolationMode=InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    double scale=480d/Math.Max(bounds.Width,bounds.Height); int w=(int)Math.Round(bounds.Width*scale), h=(int)Math.Round(bounds.Height*scale);
                    graphics.DrawImage(original,new Rectangle((512-w)/2,(512-h)/2,w,h),bounds,GraphicsUnit.Pixel);
                }
                square.Save(Path.Combine(directory,"NewsPlayLogo.png"),ImageFormat.Png);
                int[] sizes={16,20,24,32,40,48,64,128,256}; var frames=new byte[sizes.Length][];
                for(int i=0;i<sizes.Length;i++) using(var frame=new Bitmap(sizes[i],sizes[i],PixelFormat.Format32bppArgb))
                {
                    using(var graphics=Graphics.FromImage(frame)) { graphics.Clear(Color.Transparent);graphics.CompositingMode=CompositingMode.SourceCopy;graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;graphics.DrawImage(square,0,0,sizes[i],sizes[i]); }
                    using(var memory=new MemoryStream()) { frame.Save(memory,ImageFormat.Png);frames[i]=memory.ToArray(); }
                }
                using(var writer=new BinaryWriter(File.Create(Path.Combine(directory,"NewsPlay.ico"))))
                {
                    writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;
                    for(int i=0;i<sizes.Length;i++) { writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(frames[i].Length);writer.Write(offset);offset+=frames[i].Length; }
                    foreach(var frame in frames) writer.Write(frame);
                }
            }
        }
        File.Copy(source,Path.Combine(directory,"NewsPlayLogo.Source.png"),true);
    }
}
'@
[NewsPlayBrandAssets]::Prepare((Resolve-Path -LiteralPath $SourcePng).Path, $assetDirectory)
Write-Output 'Logo preserved; transparent header PNG and multi-size Windows ICO prepared.'
