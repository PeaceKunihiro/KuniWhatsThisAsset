using System.Windows;
namespace KuniWhatsThisAsset.App;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--model-worker"])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await Task.Run(ModelWorker.RunAsync));
            return;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }
}

