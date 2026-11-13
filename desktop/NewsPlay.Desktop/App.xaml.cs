using System.Windows;
namespace NewsPlay.Desktop;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var smoke = Environment.GetCommandLineArgs().SkipWhile(a => a != "--smoke-test").Skip(1).FirstOrDefault();
            if (smoke is not null) { System.IO.File.WriteAllText(smoke + ".result.txt", "FAIL: " + args.Exception); args.Handled = true; Shutdown(1); }
            else { MessageBox.Show(args.Exception.Message, "NewsPlay", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; }
        };
        base.OnStartup(e);
    }
}
