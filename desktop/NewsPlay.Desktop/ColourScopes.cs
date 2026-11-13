using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NewsPlay.Desktop;

// Scopes sample decoded RGB pixels. They are monitoring aids, not an SDI analyser.
public sealed class ColourScopes : FrameworkElement
{
    private BitmapSource? plot;
    private string description = "Waiting for video";
    public string Mode { get; set; } = "Waveform";
    public void Update(byte[] pixels, int width, int height)
    {
        const int w = 512, h = 192; var image = new byte[w * h * 4]; var histogram = new int[256]; var clipped = 0; var count = 0;
        void Dot(int x, int y, byte r, byte g, byte b) { x = Math.Clamp(x,0,w-1); y = Math.Clamp(y,0,h-1); var p = (y*w+x)*4; image[p] = (byte)Math.Min(255,image[p]+b); image[p+1] = (byte)Math.Min(255,image[p+1]+g); image[p+2] = (byte)Math.Min(255,image[p+2]+r); image[p+3]=255; }
        for (var y = 0; y < height; y += 3) for (var x = 0; x < width; x += 3)
        {
            var i=(y*width+x)*4; var b=pixels[i]/255d; var g=pixels[i+1]/255d; var r=pixels[i+2]/255d; var l=.2126*r+.7152*g+.0722*b;
            histogram[Math.Clamp((int)(l*255),0,255)]++; count++; if(r<=0||g<=0||b<=0||r>=1||g>=1||b>=1)clipped++;
            if (Mode=="Vectorscope") { var cb=(b-l)/1.8556; var cr=(r-l)/1.5748; Dot((int)(w/2+cb*340),(int)(h/2-cr*170),30,65,38); }
            else if(Mode=="RGB Parade") { Dot(x*w/width/3,(int)((1-r)*(h-1)),55,4,4); Dot(w/3+x*w/width/3,(int)((1-g)*(h-1)),4,55,4); Dot(2*w/3+x*w/width/3,(int)((1-b)*(h-1)),4,8,65); }
            else if(Mode=="Waveform") Dot(x*w/width,(int)((1-l)*(h-1)),32,60,42);
        }
        if(Mode=="Histogram") { var max=Math.Max(1,histogram.Max()); for(var x=0;x<256;x++)for(var y=0;y<histogram[x]*(h-1)/max;y++){Dot(x*2,h-1-y,80,165,145);Dot(x*2+1,h-1-y,80,165,145);} }
        plot=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,image,w*4); plot.Freeze();
        description=$"{Mode} · decoded RGB · 0–100% · edge/clipped pixels {100d*clipped/Math.Max(1,count):0.0}%"; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(12,17,22)),null,new Rect(RenderSize));
        var plotRect=new Rect(36,28,Math.Max(1,ActualWidth-48),Math.Max(1,ActualHeight-54));
        if(plot is not null)dc.DrawImage(plot,plotRect);
        var pen=new Pen(new SolidColorBrush(Color.FromArgb(90,130,150,165)),1);
        for(var i=0;i<=4;i++){var y=plotRect.Top+plotRect.Height*i/4;dc.DrawLine(pen,new Point(plotRect.Left,y),new Point(plotRect.Right,y));if(Mode is "Waveform" or "RGB Parade")Text(dc,(100-i*25).ToString(),2,y-7);}
        if(Mode=="Vectorscope") { var centre=new Point(plotRect.Left+plotRect.Width/2,plotRect.Top+plotRect.Height/2); dc.DrawEllipse(null,pen,centre,plotRect.Width*.34,plotRect.Height*.44);dc.DrawLine(new Pen(Brushes.Orange,1),centre,new Point(centre.X-plotRect.Width*.19,centre.Y-plotRect.Height*.36));Text(dc,"Skin-tone reference",plotRect.Left+4,plotRect.Bottom-18); }
        Text(dc,description,8,6);
    }
    private void Text(DrawingContext dc,string text,double x,double y)=>dc.DrawText(new FormattedText(text,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,Brushes.LightGray,VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y));
}
