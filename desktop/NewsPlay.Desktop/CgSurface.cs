using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace NewsPlay.Desktop;

public sealed class CgSurface : UserControl, IDisposable
{
    public static bool Enabled { get; set; }
    private static Task<CoreWebView2Environment>? environment;
    private readonly WebView2CompositionControl view = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private readonly string page;
    private bool initialized, disposed;
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CgSurface(int port, bool editor = false)
    {
        page = editor ? "index.html" : "output.html?port=" + port;
        Content = view; IsHitTestVisible = editor;
        Loaded += async (_, _) =>
        {
            if (initialized || disposed) return; initialized = true;
            try
            {
                using var client = new EngineClient();
                environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(client.DataDirectory, "CgProfile"));
                await view.EnsureCoreWebView2Async(await environment);
                if (disposed) return;
                var core = view.CoreWebView2;
                core.SetVirtualHostNameToFolderMapping("cg.newsplay.local", Path.Combine(AppContext.BaseDirectory, "Cg"), CoreWebView2HostResourceAccessKind.DenyCors);
                core.Settings.AreDevToolsEnabled = false; core.Settings.IsStatusBarEnabled = false;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://cg.newsplay.local/", StringComparison.Ordinal)) e.Cancel = true; };
                core.NavigationCompleted += (_, e) => { if (e.IsSuccess) Ready.TrySetResult(); else Ready.TrySetException(new InvalidOperationException("CG page failed to load")); };
                core.Navigate("https://cg.newsplay.local/" + page);
            }
            catch (Exception ex) { Ready.TrySetException(ex); Content = new TextBlock { Text = "CG unavailable: " + ex.Message, TextWrapping = TextWrapping.Wrap }; }
        };
    }
    public Task<string> Execute(string script) => view.CoreWebView2.ExecuteScriptAsync(script);
    public void Dispose() { if (disposed) return; disposed = true; view.Dispose(); }
}

