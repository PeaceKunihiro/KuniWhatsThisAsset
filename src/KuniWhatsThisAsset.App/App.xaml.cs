using System.Windows;
namespace KuniWhatsThisAsset.App;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--apply-update", var plan])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await SelfUpdater.ApplyAsync(plan));
            return;
        }
        if (e.Args is ["--model-worker"])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await Task.Run(ModelWorker.RunAsync));
            return;
        }
        base.OnStartup(e);
        new MainWindow().Show();
        if (e.Args is ["--update-completed", var work, var helper] && int.TryParse(helper, out int helperId))
            _ = Task.Run(() => SelfUpdater.CleanupAsync(work, helperId));
    }
}

