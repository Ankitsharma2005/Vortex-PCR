using System.Windows;
using System.Windows.Controls;
using NewsPlay.Core;
namespace NewsPlay.Desktop;
public partial class MainWindow
{
    private ColourWindow? colourWindow;
    private async Task TestColourWorkflow()
    {
        var asset=state!.Media.First(m=>m.Kind=="Clip");var original=Json.Copy(asset.Colour);await OpenColour(asset.Id);var window=colourWindow!;
        try
        {
            for(var i=0;i<150&&!window.HasPreview;i++)await Task.Delay(100);
            if(!window.HasPreview)throw new InvalidOperationException("Colour live preview did not produce a frame");
            var oldRevision=window.PreviewRevision;window.PrimarySliders[0].Value=.2;window.PrimarySliders[2].Value=.4;
            for(var i=0;i<100&&window.PreviewRevision<=oldRevision;i++)await Task.Delay(50);
            if(window.PreviewRevision<=oldRevision)throw new InvalidOperationException("Slider did not update preview before Save");
            var before=(await client.GetState()).Media.Single(m=>m.Id==asset.Id).Colour;
            if(before.Brightness!=original.Brightness)throw new InvalidOperationException("Preview unexpectedly saved settings");
            await window.Save();var saved=(await client.GetState()).Media.Single(m=>m.Id==asset.Id).Colour;
            if(saved.Brightness!=.2||saved.Saturation!=.4)throw new InvalidOperationException("Colour save failed");
            window.Reset();await window.Save();saved=(await client.GetState()).Media.Single(m=>m.Id==asset.Id).Colour;
            if(saved.Brightness!=0||saved.Saturation!=1)throw new InvalidOperationException("Colour reset failed");
            await Task.Delay(400);window.UpdateLayout();CaptureWindow(window,smokeFile+".colour.png");
            var item=Rundown(1).Items.First(i=>i.MediaId==asset.Id);
            await Send(new(){Action="Take",Channel=1,ItemId=item.Id});await Task.Delay(300);
            window.PrimarySliders[0].Value=1;await window.TestLive(true);
            using var probe=new FrameReader();
            async Task<bool> Picture(bool white)
            {
                for(var attempt=0;attempt<60;attempt++){await Task.Delay(50);if(probe.Read(client.Instance,1)){var pixels=probe.Snapshot();var isWhite=pixels.Length>0;for(var i=0;i<pixels.Length&&isWhite;i+=4)isWhite=pixels[i]>=250&&pixels[i+1]>=250&&pixels[i+2]>=250;if(isWhite==white)return true;}}return false;
            }
            if(!await Picture(true))throw new InvalidOperationException("Live programme audition did not reach output");
            if((await client.GetState()).Media.Single(m=>m.Id==asset.Id).Colour.Brightness!=0)throw new InvalidOperationException("Live audition changed saved grade");
            window.Close();if(!await Picture(false))throw new InvalidOperationException("Closing colour window did not restore saved programme grade");
            await Send(new(){Action="Stop",Channel=1,Confirm=true});
            await System.IO.File.WriteAllTextAsync(smokeFile+".colour.result.txt","PASS: dedicated moving preview, unsaved slider updates, Save/Reset, scopes, live programme audition and close rollback verified through actual frame buffer.");
        }
        finally{window.Close();await Send(new(){Action="Media.Colour",MediaId=asset.Id,Colour=original});}
    }
    private async void OpenOnAirColour(object sender,RoutedEventArgs e)
    {
        await Try(()=>{var port=int.Parse((string)((MenuItem)sender).Tag);var channel=Channel(port);return OpenColour(channel.LiveInputId??Rundown(port).Items.FirstOrDefault(i=>i.Id==channel.CurrentId)?.MediaId);});
    }
    private async Task OpenColour(Guid? id)
    {
        if(colourWindow is not null){colourWindow.Activate();return;}
        var asset=state?.Media.SingleOrDefault(m=>m.Id==id)??throw new InvalidOperationException("Select a video clip first");
        if(asset.Kind=="Audio")throw new InvalidOperationException("Colour correction requires video or an image");
        BrowserSources.Session? browser=null;var wasPreview=false;
        if(asset.Live?.Type=="Browser"){browser=BrowserInputs.Find(asset.Id);wasPreview=browser?.PreviewOpen==true;browser=await BrowserInputs.Ensure(asset.Id,asset.Path,true);}
        colourWindow=new ColourWindow(client,Send,asset){Owner=this};colourWindow.Closed+=(_,_)=>{colourWindow=null;if(browser is not null&&!wasPreview)browser.PreviewOpen=false;};colourWindow.Show();
    }
}
