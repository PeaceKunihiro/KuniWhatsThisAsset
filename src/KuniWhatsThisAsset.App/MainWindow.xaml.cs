using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public partial class MainWindow : Window
{
    private PackageReport? report;
    private CancellationTokenSource? scanCancellation;
    private readonly Dictionary<string, TreeViewItem> fileNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PackageItem> history = [];
    private int historyIndex = -1;
    private bool navigating;
    public MainWindow()
    {
        InitializeComponent();
        var args = Environment.GetCommandLineArgs();
        if (args.Length == 2) Loaded += async (_, _) => await LoadPackage(args[1]);
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Unity Package (*.unitypackage)|*.unitypackage" };
        if (dialog.ShowDialog(this) == true) await LoadPackage(dialog.FileName);
    }
    private void Notify(IReadOnlyList<Finding> findings)
    {
        FindingsGrid.ItemsSource = findings.OrderByDescending(f => f.Severity).ToArray();
        NoticeTitle.Text = $"⚠ 要確認 {findings.Count:N0}件 — クリックして確認";
        SecurityNotice.IsExpanded = false;
        SecurityNotice.Visibility = findings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Warning_Collapsed(this, new RoutedEventArgs());
        if (warningWindow != null) { if (findings.Count == 0) warningWindow.Hide(); else warningWindow.Show(); }
    }
    private async Task LoadPackage(string path)
    {
        if (scanCancellation != null) return;
        if (!Path.GetExtension(path).Equals(".unitypackage", StringComparison.OrdinalIgnoreCase))
        { Notify([new(Severity.Warning, "INPUT", Path.GetFileName(path), ".unitypackageを選択してください。")]); return; }
        using var cts = new CancellationTokenSource();
        scanCancellation = cts;
        report = null;
        history.Clear(); historyIndex = -1; UpdateNavigation();
        PackageTree.Items.Clear(); fileNodes.Clear();
        Preview.Visibility = Visibility.Collapsed; Preview.Text = ""; PreviewHint.Text = "";
        Notify([]);
        PackagePath.Text = path;
        OpenMenu.IsEnabled = ReloadButton.IsEnabled = false;
        CancelButton.Visibility = BusyProgress.Visibility = Visibility.Visible;
        BusyProgress.IsIndeterminate = true;
        Status.Text = "読み込み中…";
        try
        {
            var limits = new ScanLimits();
            while (true)
            {
                try { report = await Task.Run(() => new PackageReader(limits).Read(path, cts.Token)); break; }
                catch (ReadLimitExceededException ex)
                {
                    if (cts.IsCancellationRequested) throw new OperationCanceledException(cts.Token);
                    var choice = MessageBox.Show(this,
                        ex.Message + "\n\n今回の読み込みでは容量・件数・時間の上限をすべて解除して、先頭から読み込みを再試行しますか？\n処理時間やメモリ使用量が増加する場合があります。\n次のパッケージには通常の上限を適用します。",
                        "読み込み継続確認", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                    if (choice != MessageBoxResult.Yes) { Status.Text = "上限に達したため読み込みを中止しました"; return; }
                    limits = limits with { IgnoreReadLimits = true };
                    Status.Text = "上限を解除して再読み込み中…";
                }
            }
            BuildTree(); Notify(report.Findings);
            Status.Text = $"{report.FileCount:N0} ファイル · " + string.Join(" / ", report.Items.Where(i => !i.IsDirectory).GroupBy(i => i.Format).Select(g => $"{g.Key} {g.Count()}"));
        }
        catch (OperationCanceledException) { Status.Text = "解析中止（キャンセルまたは時間上限）"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            Status.Text = "読み込み拒否 / 解析未完了";
            Notify([new(Severity.High, "READ_FAILED", Path.GetFileName(path), ex.Message)]);
        }
        finally
        {
            scanCancellation = null;
            OpenMenu.IsEnabled = ReloadButton.IsEnabled = true;
            CancelButton.Visibility = BusyProgress.Visibility = Visibility.Collapsed;
            BusyProgress.IsIndeterminate = false;
        }
    }
    private void BuildTree()
    {
        if (report == null) return;
        var root = new TreeViewItem { Header = Path.GetFileName(report.SourcePath), IsExpanded = true };
        PackageTree.Items.Add(root);
        var folders = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        foreach (var item in report.Items)
        {
            string[] parts = item.Path.Split('/'); string current = "";
            var parent = root;
            for (int i = 0; i < parts.Length; i++)
            {
                current = current.Length == 0 ? parts[i] : current + "/" + parts[i];
                if (i == parts.Length - 1 && !item.IsDirectory)
                {
                    var leaf = new TreeViewItem { Header = parts[i], Tag = item };
                    parent.Items.Add(leaf); fileNodes[item.Path] = leaf; break;
                }
                if (!folders.TryGetValue(current, out var folder))
                {
                    folder = new TreeViewItem { Header = parts[i], IsExpanded = parts.Length > 1 && i == 0 };
                    folders.Add(current, folder); parent.Items.Add(folder);
                }
                parent = folder;
            }
        }
    }
    private static void SetBranch(TreeViewItem node, bool expanded)
    {
        var pending = new Stack<TreeViewItem>(); pending.Push(node);
        while (pending.TryPop(out var current))
        {
            if (current.Items.Count > 0) current.IsExpanded = expanded;
            foreach (TreeViewItem child in current.Items) pending.Push(child);
        }
    }
    private void Tree_AltClick(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source != null && source is not TreeViewItem)
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        if (source is not TreeViewItem node || node.Items.Count == 0) return;
        bool expanded = !node.IsExpanded;
        node.IsSelected = true; SetBranch(node, expanded); e.Handled = true;
    }
    private void Expand_Click(object sender, RoutedEventArgs e) { if (PackageTree.SelectedItem is TreeViewItem node) SetBranch(node, true); }
    private void Collapse_Click(object sender, RoutedEventArgs e) { if (PackageTree.SelectedItem is TreeViewItem node) SetBranch(node, false); }
    private void Tree_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (PackageTree.SelectedItem is not TreeViewItem { Tag: PackageItem item })
        { Preview.Visibility = Visibility.Collapsed; PreviewHint.Text = ""; return; }
        ShowItem(item);
    }
    private void ShowItem(PackageItem item)
    {
        if (!navigating && (historyIndex < 0 || history[historyIndex] != item))
        {
            history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            history.Add(item); historyIndex = history.Count - 1;
        }
        Preview.Text = item.TextPreview ?? "";
        Preview.Visibility = item.TextPreview == null ? Visibility.Collapsed : Visibility.Visible;
        PreviewHint.Text = item.TextPreview == null ? "この形式のプレビューは未対応です。\nモデルプレビューは次段階で対応予定です。" : "";
        Status.Text = $"{item.Name} · {item.Format} · {item.SizeLabel}";
        UpdateNavigation();
    }
    private void UpdateNavigation() { BackButton.IsEnabled = historyIndex > 0; ForwardButton.IsEnabled = historyIndex >= 0 && historyIndex < history.Count - 1; }
    private void Navigate(int direction)
    {
        int index = historyIndex + direction;
        if (index < 0 || index >= history.Count) return;
        historyIndex = index; navigating = true;
        try { SelectItem(history[index]); } finally { navigating = false; }
        UpdateNavigation();
    }
    private void SelectItem(PackageItem item)
    {
        if (!fileNodes.TryGetValue(item.Path, out var node)) return;
        for (var parent = ItemsControl.ItemsControlFromItemContainer(node) as TreeViewItem; parent != null; parent = ItemsControl.ItemsControlFromItemContainer(parent) as TreeViewItem) parent.IsExpanded = true;
        node.IsSelected = true; node.BringIntoView(); ShowItem(item);
    }
    private void Back_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void Forward_Click(object sender, RoutedEventArgs e) => Navigate(1);
    private async void Reload_Click(object sender, RoutedEventArgs e) { if (File.Exists(PackagePath.Text)) await LoadPackage(PackagePath.Text); }
    private void Finding_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (report == null || FindingsGrid.SelectedItem is not Finding finding) return;
        var item = report.Items.FirstOrDefault(i => i.Findings.Contains(finding));
        if (item != null) SelectItem(item);
    }
    private async void OnDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths) await LoadPackage(paths[0]); }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = scanCancellation == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => scanCancellation?.Cancel();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Limits_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "初期上限：パッケージ512 MiB / 展開相当1 GiB / メンバー256 MiB\n項目50,000 / アセット10,000 / 解析2分\nテキスト先頭2 MiB、全体32 MiB予算。超過後は先頭4 KiB。\nテキスト表示32,768文字。未解析部分は警告します。\n読み込み容量・件数・時間の上限に達した場合は継続を確認します。\n承認すると容量・件数・時間の上限を今回だけすべて解除し、先頭から再試行します。", "読み込み上限");
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "Fileから開くか、パッケージをドロップします。\nAlt＋左クリックで枝以下を一括展開・格納します。\n右側はプレビュー専用です。モデル描画は未対応です。\n異常検出時だけ下部に通知します。クリックで詳細を開きます。\n検出は静的ルールによるものです。未検出は安全性を保証しません。", "使い方");
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
    protected override void OnClosed(EventArgs e) { scanCancellation?.Cancel(); warningWindow?.Close(); base.OnClosed(e); }
}




