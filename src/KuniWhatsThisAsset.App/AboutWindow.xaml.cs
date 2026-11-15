using System.Diagnostics;
using System.Windows;
using KuniWhatsThisAsset.Core;
namespace KuniWhatsThisAsset.App;
public partial class AboutWindow : Window
{
    private readonly UpdateChecker checker;
    private readonly Version current;
    private readonly CancellationTokenSource lifetime = new();
    private AvailableUpdate? update;
    private bool checkedOnce;
    public AboutWindow() : this(UpdateChecker.CreateDefault(), typeof(AboutWindow).Assembly.GetName().Version!) { }
    public AboutWindow(UpdateChecker checker, Version current)
    {
        this.checker = checker;
        this.current = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        InitializeComponent();
        CurrentVersion.Text = "Version " + this.current.ToString(3);
        Loaded += CheckOnOpen;
    }
    private async void CheckOnOpen(object sender, RoutedEventArgs e)
    {
        if (checkedOnce) return;
        checkedOnce = true;
        update = await checker.CheckAsync(current, lifetime.Token);
        if (lifetime.IsCancellationRequested || update == null) return;
        UpdateMessage.Text = "新しいバージョン v" + update.Version.ToString(3) + " があります";
        UpdatePanel.Visibility = Visibility.Visible;
    }
    private void Stable_Click(object sender, RoutedEventArgs e) => OpenBrowser(UpdateChecker.StableUri);
    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (update != null) OpenBrowser(update.DownloadUri);
    }
    private void OpenBrowser(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            update = null;
            UpdatePanel.Visibility = Visibility.Collapsed;
            // Keep the selectable fixed link visible without a blocking error dialog.
        }
    }
    protected override void OnClosed(EventArgs e) { lifetime.Cancel(); base.OnClosed(e); }
}
