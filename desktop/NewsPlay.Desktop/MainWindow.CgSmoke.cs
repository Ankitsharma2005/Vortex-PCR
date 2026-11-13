using System.IO;
using System.Text.Json;
using System.Windows;

namespace NewsPlay.Desktop;

public partial class MainWindow
{
    private async Task TestAdvancedCg()
    {
        if (!Environment.GetCommandLineArgs().Contains("--cg-smoke")) return;
        OpenAdvancedCg(this, new RoutedEventArgs());
        var window = cgWindow!; window.Left = -16000; window.Top = -16000;
        var surface = (CgSurface)window.Content;
        try
        {
            await surface.Ready.Task.WaitAsync(TimeSpan.FromSeconds(25));
            await surface.Execute("import('./test.js').then(m=>m.run()).then(result=>window.cgTestResult=result).catch(e=>window.cgTestResult='FAIL: '+e.stack)");
            string? result = null;
            for (var i = 0; i < 200; i++) { await Task.Delay(100); result = JsonSerializer.Deserialize<string>(await surface.Execute("window.cgTestResult??null")); if (result is not null) break; }
            if (result is null || !result.StartsWith("PASS")) throw new InvalidOperationException(result ?? "CG test timed out");
            await surface.Execute("[...document.querySelectorAll('.card button')].find(b=>b.textContent==='OFF').click()");
            await Task.Delay(1000); CaptureWindow(window, smokeFile + ".advanced-cg.png");
            var program = ChannelOne.AdvancedCgSurface ?? throw new InvalidOperationException("CG program surface was not attached");
            await program.Ready.Task.WaitAsync(TimeSpan.FromSeconds(25));
            var mounted = false;
            for (var i = 0; i < 50; i++) { if (await program.Execute("document.querySelectorAll('.band').length > 0") == "true") { mounted = true; break; } await Task.Delay(100); }
            if (!mounted) throw new InvalidOperationException("CG ON AIR did not reach the program monitor");
            await surface.Execute("[...document.querySelectorAll('button')].find(b=>b.textContent==='Clear all CG').click()");
            await File.WriteAllTextAsync(smokeFile + ".advanced-cg.result.txt", result);
        }
        finally { window.Close(); }
    }
}
