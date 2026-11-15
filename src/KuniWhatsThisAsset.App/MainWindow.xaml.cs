using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;

public partial class MainWindow : Window
{
    private PackageReport? report;
    private CancellationTokenSource? scanCancellation;
    private string selectedFolder = "";

    public MainWindow()
    {
        InitializeComponent();
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length == 2) Loaded += async (_, _) => await LoadPackage(args[1]);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Unity Package (*.unitypackage)|*.unitypackage", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await LoadPackage(dialog.FileName);
    }

    private async Task LoadPackage(string path)
    {
        if (scanCancellation != null) return;
        if (!Path.GetExtension(path).Equals(".unitypackage", StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, ".unitypackageを選択してください。", "読み込み対象"); return; }
        using var cts = new CancellationTokenSource();
        scanCancellation = cts;
        report = null;
        selectedFolder = "";
        FilesGrid.ItemsSource = null;
        FindingsGrid.ItemsSource = null;
        PackageTree.Items.Clear();
        Details.Text = "読み込み中…";
        Preview.Text = "";
        Summary.Text = "パッケージを確認しています…";
        Formats.Text = "内容を実行せずに解析しています。";
        FindingsTitle.Text = "検出結果 — 解析中";
        PackagePath.Text = path;
        OpenButton.IsEnabled = OpenMenu.IsEnabled = false;
        CancelButton.IsEnabled = true;
        BusyProgress.IsIndeterminate = true;
        Status.Text = "読み込み・静的解析中…";
        var progress = new Progress<int>(n => { if (scanCancellation == cts) Status.Text = $"読み込み・静的解析中… {n:N0}項目"; });
        try
        {
            report = await Task.Run(() => new PackageReader().Read(path, cts.Token, progress));
            Summary.Text = $"{report.FileCount:N0} ファイル / {report.Items.Count(i => i.IsDirectory):N0} フォルダー / 検出 {report.Findings.Count:N0} 件";
            Formats.Text = string.Join("  ·  ", report.Items.Where(i => !i.IsDirectory).GroupBy(i => i.Format).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key} {g.Count():N0}"));
            FindingsTitle.Text = $"検出結果 — {report.Findings.Count:N0}件（High: 要確認・高 / Warning: 要確認）";
            FindingsGrid.ItemsSource = report.Findings.OrderByDescending(f => f.Severity).ToArray();
            BuildTree();
            ApplyFilter();
            Details.Text = "一覧からファイルを選択してください。";
            Status.Text = $"解析完了 · 展開相当 {report.ExpandedBytes / 1024d / 1024d:N1} MiB · ディスクへの展開なし";
        }
        catch (OperationCanceledException)
        { Summary.Text = "解析を中止しました"; Status.Text = "キャンセルまたは解析時間上限に達しました"; FindingsTitle.Text = "検出結果 — 未完了"; Details.Text = "解析は完了していません。"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            Summary.Text = "パッケージを読み込めませんでした";
            Status.Text = "読み込み拒否 / 解析未完了";
            Formats.Text = "破損・不正構造・容量上限などを確認してください。";
            FindingsTitle.Text = "検出結果 — 未完了";
            Details.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "読み込みを停止しました", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            scanCancellation = null;
            OpenButton.IsEnabled = OpenMenu.IsEnabled = true;
            CancelButton.IsEnabled = false;
            BusyProgress.IsIndeterminate = false;
        }
    }

    private void BuildTree()
    {
        if (report == null) return;
        var root = new TreeViewItem { Header = Path.GetFileName(report.SourcePath), Tag = "", IsExpanded = true };
        PackageTree.Items.Add(root);
        var folders = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        foreach (var item in report.Items)
        {
            string[] parts = item.Path.Split('/');
            string current = "";
            TreeViewItem parent = root;
            for (int i = 0; i < parts.Length; i++)
            {
                current = current.Length == 0 ? parts[i] : current + "/" + parts[i];
                if (i == parts.Length - 1 && !item.IsDirectory)
                { parent.Items.Add(new TreeViewItem { Header = parts[i], Tag = item }); break; }
                if (!folders.TryGetValue(current, out var folder))
                { folder = new TreeViewItem { Header = parts[i], Tag = current }; folders.Add(current, folder); parent.Items.Add(folder); }
                parent = folder;
            }
        }
        root.IsSelected = true;
    }

    private void ApplyFilter()
    {
        if (report == null || SearchBox == null) return;
        string search = SearchBox.Text.Trim();
        FilesGrid.ItemsSource = report.Items.Where(i =>
            (selectedFolder.Length == 0 || i.Path.Equals(selectedFolder, StringComparison.OrdinalIgnoreCase) || i.Path.StartsWith(selectedFolder + "/", StringComparison.OrdinalIgnoreCase)) &&
            (search.Length == 0 || i.Path.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (RiskOnly.IsChecked != true || i.Findings.Count > 0)).ToArray();
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
    private void Tree_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (PackageTree.SelectedItem is not TreeViewItem node) return;
        if (node.Tag is PackageItem item)
        {
            selectedFolder = ""; SearchBox.Text = ""; RiskOnly.IsChecked = false; ApplyFilter();
            FilesGrid.SelectedItem = item; FilesGrid.ScrollIntoView(item);
        }
        else { selectedFolder = node.Tag as string ?? ""; ApplyFilter(); }
    }
    private void File_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not PackageItem item) { Details.Text = "一覧からファイルを選択してください。"; Preview.Text = ""; return; }
        Details.Text = $"{item.Path}\nGUID: {item.Guid}\n形式: {item.Format}    サイズ: {item.SizeLabel}\n検出結果: {item.RiskLabel}\n\n" +
            (item.Findings.Count == 0 ? "この解析範囲で危険要素は検出されませんでした。安全性の保証ではありません。" : string.Join("\n", item.Findings.Select(f => $"[{f.Severity} / {f.Code}] {f.Message}")));
        Preview.Text = item.TextPreview ?? (item.IsDirectory ? "フォルダーです。" : "この形式のプレビューは未対応です。モデルプレビューは次段階で実装予定です。");
    }
    private void Finding_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (report == null || FindingsGrid.SelectedItem is not Finding finding) return;
        var item = report.Items.FirstOrDefault(i => i.Findings.Contains(finding));
        if (item == null) return;
        selectedFolder = ""; SearchBox.Text = ""; RiskOnly.IsChecked = false; ApplyFilter();
        FilesGrid.SelectedItem = item; FilesGrid.ScrollIntoView(item);
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths) await LoadPackage(paths[0]);
    }
    private void OnDragOver(object sender, DragEventArgs e)
    { e.Effects = scanCancellation == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => scanCancellation?.Cancel();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void SearchFocus_Click(object sender, RoutedEventArgs e) => SearchBox.Focus();
    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is PackageItem item)
            try { Clipboard.SetText(item.Path); } catch (System.Runtime.InteropServices.COMException) { Status.Text = "クリップボードを使用できませんでした"; }
    }
    private void Limits_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "初版の固定上限\n\nパッケージ: 512 MiB\n展開相当: 1 GiB\n各メンバー: 256 MiB\nアーカイブ項目: 50,000\nアセット: 10,000\n解析時間: 2分\n\nテキスト解析: 各メンバー先頭2 MiB、全体32 MiBのサンプル予算（超過後は各先頭4 KiB）。表示は先頭32,768文字。\n上限による未解析部分は警告します。プレビュー画像等はコード解析対象外です。\n\nこの版では上限の変更には対応していません。", "読み込み上限・解析範囲");
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "1.「パッケージを開く」またはドロップで読み込みます。\n2. 左側のツリーや検索欄から内容を確認します。\n3. 検出結果を選ぶと対象ファイルの詳細を表示します。\n\n検出はルールに基づく静的解析です。コードやDLLを実行しません。通信や自動実行の記述には正当な用途もあります。未検出は安全性を保証しません。難読化・未知の手法・未対応形式は検出できない場合があります。\n\nテキスト形式はUTF-8 / BOM付きUTF-16を想定しています。ウイルス対策ソフトの代替ではありません。", "使い方・検出の意味");
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "KuniWhatsThisAsset v0.1.0\nUnityPackageの内容物確認・静的な危険要素検出\n\nモデルプレビューは今後の対応予定です。", "バージョン情報");
    protected override void OnClosed(EventArgs e) { scanCancellation?.Cancel(); base.OnClosed(e); }
}
