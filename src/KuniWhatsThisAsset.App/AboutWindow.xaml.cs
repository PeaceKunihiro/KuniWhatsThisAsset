using System.Diagnostics;
using System.Windows;
using KuniWhatsThisAsset.Core;
namespace KuniWhatsThisAsset.App;
public partial class AboutWindow : Window
{
    private readonly UpdateChecker checker;
    private readonly Version current;
    private readonly IAppUpdater updater;
    private readonly CancellationTokenSource lifetime = new();
    private AvailableUpdate? update;
    private bool checkedOnce, busy;
    public AboutWindow() : this(UpdateChecker.CreateDefault(), typeof(AboutWindow).Assembly.GetName().Version!) { }
    public AboutWindow(UpdateChecker checker, Version current, IAppUpdater? updater = null)
    {
        this.checker = checker; this.updater = updater ?? new SelfUpdater();
        this.current = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        InitializeComponent(); CurrentVersion.Text = "Version " + this.current.ToString(3); Loaded += CheckOnOpen;
    }
    private async void CheckOnOpen(object sender, RoutedEventArgs e)
    {
        if (checkedOnce) return; checkedOnce = true; await CheckAsync(false);
    }
    private async void CheckNow_Click(object sender, RoutedEventArgs e) => await CheckAsync(true);
    private async Task CheckAsync(bool manual)
    {
        if (busy) return; SetBusy(true); if (manual) UpdateStatus.Text = "更新を確認中…";
        try
        {
            var result = await checker.CheckDetailedAsync(current, manual, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (result.Status is UpdateCheckStatus.Available or UpdateCheckStatus.UpToDate)
            {
                update = result.Update; UpdatePanel.Visibility = update == null ? Visibility.Collapsed : Visibility.Visible;
                if (update != null) UpdateMessage.Text = "新しいバージョン v" + update.Version.ToString(3) + " があります";
            }
            UpdateStatus.Text = !manual ? "" : result.Status switch
            {
                UpdateCheckStatus.Available => "最新版をダウンロードして更新できます。",
                UpdateCheckStatus.UpToDate => "現在のバージョンは最新です。",
                _ => "更新を確認できませんでした。再試行するか、Stableを開いてください。"
            };
        }
        finally { if (!lifetime.IsCancellationRequested) SetBusy(false); }
    }
    private void SetBusy(bool value) { busy = value; CheckUpdateButton.IsEnabled = DownloadUpdateButton.IsEnabled = !value; UpdateProgressBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed; UpdateProgressBar.IsIndeterminate = value; }
    private void Stable_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(UpdateChecker.StableUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException) { UpdateStatus.Text = "ブラウザーを開けませんでした。"; }
    }
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (busy || update == null) return; SetBusy(true);
        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                if (lifetime.IsCancellationRequested) return;
                UpdateStatus.Text = p.Message + (p.Bytes > 0 ? $" {p.Bytes / 1048576.0:F1} MiB" : "");
                UpdateProgressBar.IsIndeterminate = p.Total is not > 0;
                if (p.Total is > 0) UpdateProgressBar.Value = Math.Clamp(100.0 * p.Bytes / p.Total.Value, 0, 100);
            });
            await updater.InstallAsync(update, progress, lifetime.Token);
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) UpdateStatus.Text = "更新を中止しました。通信状況を確認して再試行してください。"; }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        { if (!lifetime.IsCancellationRequested) UpdateStatus.Text = "更新できませんでした。" + ex.Message; }
        finally { if (!lifetime.IsCancellationRequested) SetBusy(false); }
    }
    protected override void OnClosed(EventArgs e) { lifetime.Cancel(); base.OnClosed(e); }
}
