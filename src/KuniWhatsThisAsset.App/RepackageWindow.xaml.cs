using System.IO;
using System.Windows;
using KuniWhatsThisAsset.Core;
using Microsoft.Win32;
namespace KuniWhatsThisAsset.App;
public partial class RepackageWindow : Window
{
    private readonly string sourcePath;
    private ScanLimits limits;
    private RepackageSource? source;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy = true;
    public string? SavedPath { get; private set; }
    public RepackageWindow(string sourcePath, ScanLimits limits)
    {
        this.sourcePath = sourcePath; this.limits = limits;
        InitializeComponent(); FolderName.Text = Path.GetFileNameWithoutExtension(sourcePath); Loaded += LoadSource;
    }
    private async void LoadSource(object sender, RoutedEventArgs e)
    {
        SaveProgress.Visibility = Visibility.Visible; ResultMessage.Text = "元パッケージを検証中…";
        try
        {
            while (true)
            {
                try { source = await Task.Run(() => PackageRepackager.Analyze(sourcePath, limits, lifetime.Token), lifetime.Token); break; }
                catch (ReadLimitExceededException ex)
                {
                    if (lifetime.IsCancellationRequested) return;
                    if (MessageBox.Show(this, ex.Message + "\n\nこのパッケージの読み込み上限を解除して続行しますか？", "読み込み継続確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { Close(); return; }
                    limits = limits with { IgnoreReadLimits = true };
                }
            }
            if (lifetime.IsCancellationRequested) return;
            SourceSummary.Text = $"{source.Report.Items.Count:N0}アセットを保持します。元: {Path.GetFileName(sourcePath)}";
            string[] special = source.Report.Items.Where(i => PackageRepackager.IsSpecial(i.Path)).Select(i => "Assets/" + i.Path.Split('/')[1]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            SpecialPanel.Visibility = special.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            SpecialMessage.Text = "配置場所に意味があるため、次のフォルダーは集約対象から除外します：\n" + string.Join("、", special);
            ResultMessage.Text = "元パッケージの検証が完了しました。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (Expected(ex)) { if (!lifetime.IsCancellationRequested) ResultMessage.Text = "解析できません: " + ex.Message; }
        finally { if (!lifetime.IsCancellationRequested) { busy = false; SaveProgress.Visibility = Visibility.Collapsed; RefreshForm(); } }
    }
    private void Folder_Changed(object sender, RoutedEventArgs e) { if (IsInitialized) RefreshForm(); }
    private void RefreshForm()
    {
        if (SaveButton == null) return;
        SaveButton.IsEnabled = false;
        try
        {
            string folder = PackageRepackager.ValidateFolderName(FolderName.Text);
            if (source == null) return;
            PathExamples.Text = string.Join("\n\n", source.Report.Items.Take(5).Select(i => i.Path + "\n → " + PackageRepackager.TargetPath(i.Path, folder, true)));
            SaveButton.IsEnabled = !busy && (SpecialPanel.Visibility != Visibility.Visible || KeepSpecial.IsChecked == true);
        }
        catch (InvalidDataException ex) { PathExamples.Text = ex.Message; }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (busy || source == null) return;
        var dialog = new SaveFileDialog { Title = "リパッケージを別名保存", Filter = "Unity Package (*.unitypackage)|*.unitypackage", DefaultExt = ".unitypackage", AddExtension = true, OverwritePrompt = false, FileName = Path.GetFileNameWithoutExtension(sourcePath) + "_Grouped.unitypackage" };
        if (dialog.ShowDialog(this) != true) return;
        await SaveToAsync(dialog.FileName);
    }
    public async Task SaveToAsync(string destination)
    {
        if (busy || source == null) return;
        busy = true; RefreshForm(); FolderName.IsEnabled = KeepSpecial.IsEnabled = false; SaveProgress.Visibility = Visibility.Visible;
        try
        {
            var snapshot = source; string folder = FolderName.Text; bool keep = SpecialPanel.Visibility != Visibility.Visible || KeepSpecial.IsChecked == true;
            var progress = new Progress<RepackageProgress>(p => { if (!lifetime.IsCancellationRequested) ResultMessage.Text = p.Message; });
            var result = await Task.Run(() => PackageRepackager.Save(PackageRepackager.Plan(snapshot, folder, keep), destination, progress, lifetime.Token), lifetime.Token);
            if (!lifetime.IsCancellationRequested) { SavedPath = result.OutputPath; DialogResult = true; }
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) ResultMessage.Text = "保存を中止しました。"; }
        catch (Exception ex) when (Expected(ex)) { if (!lifetime.IsCancellationRequested) ResultMessage.Text = "保存できません: " + ex.Message; }
        finally { if (!lifetime.IsCancellationRequested) { busy = false; FolderName.IsEnabled = KeepSpecial.IsEnabled = true; SaveProgress.Visibility = Visibility.Collapsed; RefreshForm(); } }
    }
    private static bool Expected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException;
    protected override void OnClosed(EventArgs e) { lifetime.Cancel(); base.OnClosed(e); }
}
