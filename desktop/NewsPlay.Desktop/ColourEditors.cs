using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed class ColourWheel : FrameworkElement
{
    public double[] Values { get; set; } = [0,0,0];
    public Action? Changed { get; set; }
    public ColourWheel() { Width=140;Height=140;MouseLeftButtonDown+=(_,e)=>{CaptureMouse();Pick(e.GetPosition(this));};MouseMove+=(_,e)=>{if(IsMouseCaptured)Pick(e.GetPosition(this));};MouseLeftButtonUp+=(_,_)=>ReleaseMouseCapture(); }
    private void Pick(Point p)
    {
        var x=(p.X-70)/62;var y=(70-p.Y)/62;var radius=Math.Min(1,Math.Sqrt(x*x+y*y));var hue=(Math.Atan2(y,x)*180/Math.PI+360)%360;
        var rgb=AdvancedColourTransform.FromHsl(new((float)hue,(float)radius,.5f));Values[0]=rgb.X-.5;Values[1]=rgb.Y-.5;Values[2]=rgb.Z-.5;Changed?.Invoke();InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        for(var angle=0;angle<360;angle+=4){var rgb=AdvancedColourTransform.FromHsl(new(angle,1,.5f));var a=angle*Math.PI/180;var b=(angle+4)*Math.PI/180;var geometry=new StreamGeometry();using(var ctx=geometry.Open()){ctx.BeginFigure(new Point(70,70),true,true);ctx.LineTo(new Point(70+62*Math.Cos(a),70-62*Math.Sin(a)),true,false);ctx.LineTo(new Point(70+62*Math.Cos(b),70-62*Math.Sin(b)),true,false);}dc.DrawGeometry(new SolidColorBrush(Color.FromRgb((byte)(rgb.X*255),(byte)(rgb.Y*255),(byte)(rgb.Z*255))),null,geometry);}
        dc.DrawEllipse(new RadialGradientBrush(Colors.Gray,Colors.Transparent),null,new Point(70,70),62,62);
        var hsl=AdvancedColourTransform.Hsl(new((float)(Values[0]+.5),(float)(Values[1]+.5),(float)(Values[2]+.5)));var radians=hsl.X*Math.PI/180;dc.DrawEllipse(Brushes.Black,new Pen(Brushes.White,2),new Point(70+Math.Cos(radians)*hsl.Y*62,70-Math.Sin(radians)*hsl.Y*62),5,5);
    }
}

public sealed class ColourCurve : FrameworkElement
{
    public double[] Values { get; set; } = [0,.25,.5,.75,1];
    public bool HueCurve { get; set; }
    public Action? Changed { get; set; }
    private int index;
    public ColourCurve(){Height=160;MinWidth=250;MouseLeftButtonDown+=(_,e)=>{var p=e.GetPosition(this);index=Math.Clamp((int)Math.Round(p.X/Math.Max(1,ActualWidth)*(Values.Length-1)),0,Values.Length-1);CaptureMouse();Pick(p);};MouseMove+=(_,e)=>{if(IsMouseCaptured)Pick(e.GetPosition(this));};MouseLeftButtonUp+=(_,_)=>ReleaseMouseCapture();}
    private void Pick(Point p){var v=1-Math.Clamp(p.Y/Math.Max(1,ActualHeight),0,1);Values[index]=HueCurve?v*2-1:v;Changed?.Invoke();InvalidateVisual();}
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(14,19,26)),new Pen(Brushes.DimGray,1),new Rect(RenderSize));
        var pen=new Pen(Brushes.DimGray,.5);for(var i=1;i<4;i++){dc.DrawLine(pen,new Point(0,ActualHeight*i/4),new Point(ActualWidth,ActualHeight*i/4));dc.DrawLine(pen,new Point(ActualWidth*i/4,0),new Point(ActualWidth*i/4,ActualHeight));}
        Point P(int i)=>new(i*ActualWidth/(Values.Length-1),ActualHeight*(1-(HueCurve?(Values[i]+1)/2:Values[i])));
        for(var i=0;i<Values.Length;i++){if(i>0)dc.DrawLine(new Pen(Brushes.LightSkyBlue,2),P(i-1),P(i));dc.DrawEllipse(Brushes.White,null,P(i),4,4);}
    }
}
