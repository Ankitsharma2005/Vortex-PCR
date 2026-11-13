using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed class ColourWindow : Window
{
    private readonly EngineClient client;
    private readonly Guid session = Guid.NewGuid();
    private readonly Func<EngineCommand,Task> send;
    private readonly MediaAsset asset;
    private ColourCorrection settings;
    private ColourTransform transform;
    private readonly FrameReader reader = new();
    private readonly Image monitor = new() { Stretch = Stretch.Uniform };
    private readonly ColourScopes scopes = new() { Height = 230 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly TextBlock pipeline = new() { Text = "Live preview · CPU / 8-bit RGB · scopes are decoded-signal monitoring aids", Foreground = Brushes.LightSteelBlue, Margin = new Thickness(8) };
    private readonly TabControl tabs = new();
    private readonly CheckBox live = new() { Content = "Live program preview (both ports using this source)" };
    private readonly ComboBox viewMode = new() { Width = 170, ItemsSource = new[] { "After", "Before", "Before / After split", "Zebra >95%", "False colour", "Clipping alert" }, SelectedIndex = 0 };
    private readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer changes = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly SemaphoreSlim operations = new(1,1);
    private byte[]? lastRaw;
    private int width,height,slot,revision,compiledRevision=-1,paintedRevision=-1;
    private bool busy,closed,building,heartbeat;
    private DateTime nextHeartbeat=DateTime.MinValue,nextScopes=DateTime.MinValue;
    private WriteableBitmap? bitmap;
    internal IReadOnlyList<Slider> PrimarySliders => primarySliders;
    private readonly List<Slider> primarySliders=[];
    internal int PreviewRevision => paintedRevision;
    internal bool HasPreview => bitmap is not null;
    internal ColourCorrection CurrentSettings => Json.Copy(settings);
    internal Task<int> TestLive(bool enabled) { live.IsChecked=enabled;return client.ColourSource(new(){Action="Adjust",ItemId=session,MediaId=asset.Id,Colour=Json.Copy(settings),Enabled=enabled}); }
    public ColourWindow(EngineClient client,Func<EngineCommand,Task> send,MediaAsset asset)
    {
        this.client=client;this.send=send;this.asset=asset;settings=Json.Copy(asset.Colour);transform=new(settings);
        Title="Colour correction · "+asset.Name;Width=1220;Height=850;MinWidth=1000;MinHeight=680;Style=(Style)Application.Current.Resources[typeof(Window)];WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new Grid { Margin=new Thickness(12) };root.ColumnDefinitions.Add(new ColumnDefinition());root.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(450) });
        var left=new Grid();foreach(var h in new[]{new GridLength(35),new GridLength(1,GridUnitType.Star),new GridLength(40),new GridLength(230),new GridLength(65),new GridLength(50)})left.RowDefinitions.Add(new RowDefinition{Height=h});root.Children.Add(left);
        left.Children.Add(pipeline);var border=new Border { Background=Brushes.Black,Child=monitor };Grid.SetRow(border,1);left.Children.Add(border);
        var options=new WrapPanel { Margin=new Thickness(4) };options.Children.Add(viewMode);var scopeMode=new ComboBox { ItemsSource=new[]{"Waveform","Vectorscope","RGB Parade","Histogram"},SelectedIndex=0,Width=150 };options.Children.Add(scopeMode);Grid.SetRow(options,2);left.Children.Add(options);
        scopeMode.SelectionChanged+=(_,_)=>{scopes.Mode=(string)scopeMode.SelectedItem;nextScopes=DateTime.MinValue;};viewMode.SelectionChanged+=(_,_)=>paintedRevision=-1;
        Grid.SetRow(scopes,3);left.Children.Add(scopes);Grid.SetRow(status,4);left.Children.Add(status);
        var actions=new WrapPanel();Grid.SetRow(actions,5);left.Children.Add(actions);
        AddButton(actions,"Save correction",Save);AddButton(actions,"Reset",()=>{Reset();return Task.CompletedTask;});AddButton(actions,"Save preset",SavePreset);AddButton(actions,"Load preset",LoadPreset);AddButton(actions,"Close",()=>{Close();return Task.CompletedTask;});
        Grid.SetColumn(tabs,1);tabs.Margin=new Thickness(12,0,0,0);root.Children.Add(tabs);Content=root;Build();
        live.Click+=async(_,_)=>{Changed();if(live.IsChecked!=true)await Safe(()=>client.ColourSource(new(){Action="Adjust",ItemId=session,MediaId=asset.Id,Enabled=false,Colour=settings}));};
        changes.Tick+=async(_,_)=>{changes.Stop();await Compile();};renderTimer.Tick+=async(_,_)=>await RenderFrame();
        Loaded+=async(_,_)=>{await Safe(Connect);if(!closed)renderTimer.Start();};Closed+=async(_,_)=>{closed=true;changes.Stop();renderTimer.Stop();reader.Dispose();await operations.WaitAsync();try{await client.ColourSource(new(){Action="Stop",ItemId=session});}catch{}finally{operations.Release();}};
        monitor.MouseLeftButtonDown+=(_,e)=>
        {
            if(!settings.Qualifier||lastRaw is null||width==0)return;
            var p=e.GetPosition(monitor);var scale=Math.Min(monitor.ActualWidth/width,monitor.ActualHeight/height);var x=(int)((p.X-(monitor.ActualWidth-width*scale)/2)/scale);var y=(int)((p.Y-(monitor.ActualHeight-height*scale)/2)/scale);
            if(x<0||x>=width||y<0||y>=height)return;var i=(y*width+x)*4;var hsl=AdvancedColourTransform.Hsl(new(lastRaw[i+2]/255f,lastRaw[i+1]/255f,lastRaw[i]/255f));settings.QualifierHue=hsl.X;Build();Changed();
        };
    }
    private void AddButton(Panel panel,string label,Func<Task> action){var b=new Button{Content=label,Margin=new Thickness(3),Padding=new Thickness(9,5,9,5)};b.Click+=async(_,_)=>{b.IsEnabled=false;await Safe(action);b.IsEnabled=true;};panel.Children.Add(b);}
    private async Task Safe(Func<Task> action){try{await action();}catch(Exception ex){if(!closed)status.Text=ex.Message;}}
    private StackPanel Tab(string name){var panel=new StackPanel{Margin=new Thickness(10)};tabs.Items.Add(new TabItem{Header=name,Content=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=panel}});return panel;}
    private void Note(Panel panel,string text)=>panel.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightSteelBlue,Margin=new Thickness(0,6,0,8)});
    private Slider Field(Panel panel,string label,double value,double min,double max,Action<double> change)
    {
        var line=new DockPanel{Margin=new Thickness(0,7,0,0)};var number=new TextBlock{Text=value.ToString("0.00"),Width=60,TextAlignment=TextAlignment.Right};DockPanel.SetDock(number,Dock.Right);line.Children.Add(number);line.Children.Add(new TextBlock{Text=label});panel.Children.Add(line);
        var slider=new Slider{Minimum=min,Maximum=max,Value=value,SmallChange=.01,LargeChange=.1,TickFrequency=.01,IsSnapToTickEnabled=true};slider.ValueChanged+=(_,_)=>{change(slider.Value);number.Text=slider.Value.ToString("0.00");Changed();};panel.Children.Add(slider);return slider;
    }
    private void Check(Panel panel,string text,bool value,Action<bool> change){var check=new CheckBox{Content=text,IsChecked=value,Margin=new Thickness(0,8,0,8)};check.Click+=(_,_)=>{change(check.IsChecked==true);Changed();};panel.Children.Add(check);}
    private void Build()
    {
        building=true;if(live.Parent is Panel prior)prior.Children.Remove(live);var selected=tabs.SelectedIndex;tabs.Items.Clear();primarySliders.Clear();
        var p=Tab("Primary");Note(p,asset.Name+" · changes preview immediately; Save stores the grade for every occurrence of this source.");p.Children.Add(live);Check(p,"Enable correction / bypass",settings.Enabled,v=>settings.Enabled=v);
        primarySliders.Add(Field(p,"Brightness",settings.Brightness,-1,1,v=>settings.Brightness=v));primarySliders.Add(Field(p,"Contrast",settings.Contrast,0,2,v=>settings.Contrast=v));primarySliders.Add(Field(p,"Saturation",settings.Saturation,0,2,v=>settings.Saturation=v));
        Field(p,"Lift · shadows",settings.Lift,-1,1,v=>settings.Lift=v);Field(p,"Gamma · midtones",settings.Gamma,.2,3,v=>settings.Gamma=v);Field(p,"Gain · highlights",settings.Gain,0,2,v=>settings.Gain=v);Field(p,"Offset · master",settings.Offset,-1,1,v=>settings.Offset=v);Field(p,"Hue degrees",settings.Hue,-180,180,v=>settings.Hue=v);
        Field(p,"Black input level",settings.Black,0,.98,v=>settings.Black=Math.Min(v,settings.White-.001));Field(p,"White input level",settings.White,.02,1,v=>settings.White=Math.Max(v,settings.Black+.001));
        Field(p,"Red gain",settings.Red,0,2,v=>settings.Red=v);Field(p,"Green gain",settings.Green,0,2,v=>settings.Green=v);Field(p,"Blue gain",settings.Blue,0,2,v=>settings.Blue=v);
        AddButton(p,"Auto levels (sample current frame)",()=>{Auto(false);return Task.CompletedTask;});AddButton(p,"Auto white balance (gray-world)",()=>{Auto(true);return Task.CompletedTask;});
        p=Tab("Wheels");Note(p,"Drag toward a colour. Centre is neutral. Primary Lift/Gamma/Gain/Offset control luminance.");var wheelNames=new[]{"Shadows","Midtones","Highlights","Master"};var wheelValues=new[]{settings.Shadows,settings.Midtones,settings.Highlights,settings.Master};
        for(var i=0;i<4;i++){var values=wheelValues[i];Note(p,wheelNames[i]);var wheel=new ColourWheel{Values=values,Changed=Changed,HorizontalAlignment=HorizontalAlignment.Center};p.Children.Add(wheel);AddButton(p,"Neutral "+wheelNames[i],()=>{Array.Clear(values);wheel.InvalidateVisual();Changed();return Task.CompletedTask;});}
        p=Tab("Curves");Note(p,"Drag points vertically. Five-point piecewise-linear RGB/luma curves; six hue anchors wrap at red.");var curveNames=new[]{"Master RGB","Red","Green","Blue","Luma"};
        for(var i=0;i<5;i++){Note(p,curveNames[i]);p.Children.Add(new ColourCurve{Values=settings.Curves[i],Changed=Changed});}
        for(var i=0;i<3;i++){Note(p,new[]{"Hue vs Hue (±180°)","Hue vs Saturation","Hue vs Luma"}[i]+" · R / Y / G / C / B / M");p.Children.Add(new ColourCurve{Values=settings.HueCurves[i],HueCurve=true,Changed=Changed});}
        p=Tab("Secondary");Check(p,"Enable HSL qualifier",settings.Qualifier,v=>settings.Qualifier=v);Note(p,"With qualifier enabled, click a colour in the preview to pick its hue. Primary correction affects the selected range.");
        Field(p,"Hue centre",settings.QualifierHue,0,360,v=>settings.QualifierHue=v);Field(p,"Hue half-width",settings.QualifierWidth,0,180,v=>settings.QualifierWidth=v);Field(p,"Hue edge softness",settings.QualifierSoftness,.1,180,v=>settings.QualifierSoftness=v);
        Field(p,"Saturation minimum",settings.QualifierSatMin,0,1,v=>settings.QualifierSatMin=v);Field(p,"Saturation maximum",settings.QualifierSatMax,0,1,v=>settings.QualifierSatMax=v);Field(p,"Lightness minimum",settings.QualifierLumaMin,0,1,v=>settings.QualifierLumaMin=v);Field(p,"Lightness maximum",settings.QualifierLumaMax,0,1,v=>settings.QualifierLumaMax=v);
        AddButton(p,"Skin hue starting range",()=>{settings.Qualifier=true;settings.QualifierHue=25;settings.QualifierWidth=25;settings.QualifierSoftness=20;settings.QualifierSatMin=.08;settings.QualifierSatMax=.85;settings.QualifierLumaMin=.1;settings.QualifierLumaMax=.95;Build();Changed();return Task.CompletedTask;});
        Note(p,"Power window");var shape=new ComboBox{ItemsSource=new[]{"None","Ellipse","Rectangle"},SelectedItem=settings.Mask};shape.SelectionChanged+=(_,_)=>{settings.Mask=(string)shape.SelectedItem;Changed();};p.Children.Add(shape);
        Field(p,"Centre X",settings.MaskX,0,1,v=>settings.MaskX=v);Field(p,"Centre Y",settings.MaskY,0,1,v=>settings.MaskY=v);Field(p,"Width",settings.MaskWidth,.01,2,v=>settings.MaskWidth=v);Field(p,"Height",settings.MaskHeight,.01,2,v=>settings.MaskHeight=v);Field(p,"Feather",settings.MaskFeather,0,1,v=>settings.MaskFeather=v);Check(p,"Invert mask",settings.MaskInvert,v=>settings.MaskInvert=v);
        p=Tab("LUT / Output");Note(p,settings.Lut is null?"No LUT loaded":settings.Lut.Name+" · "+settings.Lut.Size+"³");AddButton(p,"Load 3D LUT (.cube / .3dl)",LoadLut);AddButton(p,"Remove LUT",()=>{settings.Lut=null;Build();Changed();return Task.CompletedTask;});
        Note(p,"Use a LUT authored for the incoming signal. The current decoder delivers 8-bit RGB. Rec.2020 / HDR conversions are not automatically inferred; a LUT cannot restore clipped HDR or missing 10-bit precision. 3DL: plain mesh + integer RGB rows.");
        Field(p,"Saturation ceiling (HSL)",settings.ChromaLimit,0,1,v=>settings.ChromaLimit=v);
        Note(p,"Scopes and zebra/false colour are preview-only. SDI legal-range conversion, output colour metadata and hardware proc-amp are output-pipeline functions, not this slider. No GPU or 10-bit mode is advertised by this 8-bit build.");
        tabs.SelectedIndex=Math.Clamp(selected,0,tabs.Items.Count-1);building=false;
    }
    private void Changed(){if(building||closed)return;revision++;changes.Stop();changes.Start();status.Text="Unsaved changes · live preview updating";}
    private async Task Compile()
    {
        var version=revision;var copy=Json.Copy(settings);
        await Safe(async()=>{var next=await Task.Run(()=>new ColourTransform(copy));if(closed||version!=revision)return;transform=next;compiledRevision=version;paintedRevision=-1;
            if(live.IsChecked==true){await operations.WaitAsync();try{if(!closed&&version==revision)await client.ColourSource(new(){Action="Adjust",ItemId=session,MediaId=asset.Id,Colour=copy,Enabled=true});}finally{operations.Release();}}
        });
    }
    private async Task Connect()
    {
        await operations.WaitAsync();try{if(closed)return;var next=await client.ColourSource(new(){Action="Load",ItemId=session,MediaId=asset.Id});if(next!=slot){reader.Dispose();lastRaw=null;slot=next;}}finally{operations.Release();}
    }
    private async Task RenderFrame()
    {
        if(closed||busy)return;
        if(DateTime.UtcNow>=nextHeartbeat&&!heartbeat){heartbeat=true;nextHeartbeat=DateTime.UtcNow.AddSeconds(1);_ = Refresh();async Task Refresh(){try{await Safe(Connect);}finally{heartbeat=false;}}}
        if(slot==0)return;var fresh=reader.Read(client.Instance,slot);
        if(fresh&&reader.MediaId==asset.Id&&reader.Image is { } image){lastRaw=reader.Snapshot();width=image.PixelWidth;height=image.PixelHeight;}
        else if(fresh&&reader.MediaId!=asset.Id){lastRaw=null;monitor.Source=null;status.Text="Source changed on port; reconnecting preview";return;}
        if(lastRaw is null||(!fresh&&paintedRevision==compiledRevision))return;
        busy=true;
        try
        {
            var raw=lastRaw;var w=width;var h=height;var current=transform;var version=compiledRevision;var mode=(string)viewMode.SelectedItem;
            var corrected=await Task.Run(()=>{var copy=raw.ToArray();current.Apply(copy,copy.Length,w,h);return copy;});if(closed)return;
            if(DateTime.UtcNow>=nextScopes){scopes.Update(corrected,w,h);nextScopes=DateTime.UtcNow.AddMilliseconds(200);}
            var display=mode=="Before"?raw:corrected;
            if(mode is not ("After" or "Before")) for(var i=0;i<display.Length;i+=4)
            {
                var x=i/4%w;var y=i/4/w;
                if(mode=="Before"||(mode=="Before / After split"&&x<w/2)){display[i]=raw[i];display[i+1]=raw[i+1];display[i+2]=raw[i+2];}
                else if(mode=="Zebra >95%"&&(.2126*display[i+2]+.7152*display[i+1]+.0722*display[i])>242&&((x+y)/6)%2==0){display[i]=0;display[i+1]=255;display[i+2]=255;}
                else if(mode=="Clipping alert"&&(display[i]==0||display[i+1]==0||display[i+2]==0||display[i]==255||display[i+1]==255||display[i+2]==255)){display[i]=255;display[i+1]=0;display[i+2]=255;}
                else if(mode=="False colour"){var l=.2126*display[i+2]+.7152*display[i+1]+.0722*display[i];var colour=l<10?Colors.Purple:l<40?Colors.Blue:l<110?Colors.Gray:l<150?Colors.Green:l<235?Colors.Yellow:Colors.Red;display[i]=colour.B;display[i+1]=colour.G;display[i+2]=colour.R;}
            }
            if(bitmap is null||bitmap.PixelWidth!=w||bitmap.PixelHeight!=h)bitmap=new WriteableBitmap(w,h,96,96,PixelFormats.Bgr32,null);
            bitmap.WritePixels(new Int32Rect(0,0,w,h),display,w*4,0);monitor.Source=bitmap;paintedRevision=version;
        }
        catch(Exception ex){status.Text=ex.Message;}finally{busy=false;}
    }
    internal async Task Save()
    {
        var copy=Json.Copy(settings);copy.Validate();await operations.WaitAsync();try{await send(new(){Action="Media.Colour",MediaId=asset.Id,Colour=copy});status.Text="Saved for this source. Playback continues.";}finally{operations.Release();}
    }
    internal void Reset(){settings=new();Build();Changed();}
    private async Task SavePreset(){var dialog=new SaveFileDialog{Filter="NewsPlay colour preset|*.json",FileName="Colour-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".json"};if(dialog.ShowDialog(this)==true){settings.Validate();await File.WriteAllTextAsync(dialog.FileName,JsonSerializer.Serialize(settings,Json.Options));status.Text="Preset saved";}}
    private async Task LoadPreset(){var dialog=new OpenFileDialog{Filter="NewsPlay colour preset|*.json"};if(dialog.ShowDialog(this)==true){if(new FileInfo(dialog.FileName).Length>32*1024*1024)throw new InvalidOperationException("Preset exceeds 32 MB");var copy=JsonSerializer.Deserialize<ColourCorrection>(await File.ReadAllTextAsync(dialog.FileName),Json.Options)??throw new InvalidOperationException("Invalid preset");copy.Validate();settings=copy;Build();Changed();}}
    private async Task LoadLut(){var dialog=new OpenFileDialog{Filter="3D LUT|*.cube;*.3dl"};if(dialog.ShowDialog(this)==true){if(new FileInfo(dialog.FileName).Length>32*1024*1024)throw new InvalidOperationException("LUT exceeds 32 MB");int? bits=null;if(dialog.FileName.EndsWith(".3dl",StringComparison.OrdinalIgnoreCase)){var value=PromptWindow.Ask(this,"3DL precision","Output sample bit depth from LUT author (8, 10, 12 or 16). This does not change the video pipeline bit depth.","12");if(value is null)return;if(!int.TryParse(value,out var parsed)||parsed is not (8 or 10 or 12 or 16))throw new InvalidOperationException("Choose 8, 10, 12 or 16");bits=parsed;}var text=await File.ReadAllTextAsync(dialog.FileName);settings.Lut=await Task.Run(()=>CubeLut.Parse(text,Path.GetFileName(dialog.FileName),bits));Build();Changed();}}
    private void Auto(bool balance)
    {
        if(lastRaw is null)throw new InvalidOperationException("Wait for a preview frame");
        var pixels=lastRaw;var sums=new double[3];var histogram=new int[256];var count=pixels.Length/4;
        for(var i=0;i<pixels.Length;i+=4){sums[0]+=pixels[i+2];sums[1]+=pixels[i+1];sums[2]+=pixels[i];histogram[(int)(.2126*pixels[i+2]+.7152*pixels[i+1]+.0722*pixels[i])]++;}
        if(balance){var average=sums.Average();settings.Red=Math.Clamp(average/Math.Max(1,sums[0]),.25,2);settings.Green=Math.Clamp(average/Math.Max(1,sums[1]),.25,2);settings.Blue=Math.Clamp(average/Math.Max(1,sums[2]),.25,2);}
        else{var sum=0;var black=0;var white=255;for(var i=0;i<256;i++){sum+=histogram[i];if(sum<count*.01)black=i;if(sum<count*.99)white=i;}settings.Black=Math.Min(.95,black/255d);settings.White=Math.Max(settings.Black+.01,white/255d);}
        Build();Changed();
    }
}
