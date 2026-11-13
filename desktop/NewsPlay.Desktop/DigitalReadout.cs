using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;

namespace NewsPlay.Desktop;

// Vector seven-segment digits: no external font installation is required.
public sealed class DigitalReadout : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(DigitalReadout), new FrameworkPropertyMetadata("00:00:00:00", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private static readonly int[] Digits = [0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f];
    private static double GlyphWidth(char c) => c == ':' ? 4 : 9;
    protected override Size MeasureOverride(Size constraint) => new(Math.Min(constraint.Width, Math.Max(1, Text.Sum(GlyphWidth)) * FontSize / 14), FontSize + 4);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var text = Text ?? ""; var width = Math.Max(1, text.Sum(GlyphWidth));
        if (text.Any(char.IsLetter))
        {
            var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(0, Math.Max(0, (ActualHeight - label.Height) / 2))); return;
        }
        var scale = Math.Min(ActualWidth / width, ActualHeight / 14);
        if (scale <= 0) return;
        dc.PushTransform(new TranslateTransform(0, (ActualHeight - 14 * scale) / 2)); dc.PushTransform(new ScaleTransform(scale, scale));
        var on = new Pen(Foreground ?? Brushes.White, 1.15) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
        var dim = on.Clone(); dim.Brush = on.Brush.Clone(); dim.Brush.Opacity = .09;
        (Point A, Point B)[] lines = [(new(2,1),new(6,1)),(new(7,2),new(7,6)),(new(7,8),new(7,12)),(new(2,13),new(6,13)),(new(1,8),new(1,12)),(new(1,2),new(1,6)),(new(2,7),new(6,7))];
        double x = 0;
        foreach (var c in text)
        {
            dc.PushTransform(new TranslateTransform(x, 0));
            if (c == ':') { dc.DrawRectangle(on.Brush, null, new Rect(1,4,1.2,1.2)); dc.DrawRectangle(on.Brush,null,new Rect(1,9,1.2,1.2)); }
            else { var bits = c is >= '0' and <= '9' ? Digits[c - '0'] : c is '-' or '−' ? 0x40 : 0; for(var i=0;i<7;i++) dc.DrawLine((bits & (1 << i)) != 0 ? on : dim, lines[i].A, lines[i].B); }
            dc.Pop(); x += GlyphWidth(c);
        }
        dc.Pop(); dc.Pop();
    }
}

