using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public partial class MainWindow
{
    private readonly PreferenceStore preferenceStore;
    private UserPreferences preferences;
    private readonly DispatcherTimer bookmarkTimer = new();
    private string? pendingBookmarkDirectory;
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    public MainWindow(string profileDirectory)
    {
        preferenceStore = new(profileDirectory); preferences = preferenceStore.Read();
        InitializeComponent();
        Closed += (_, _) => { scanCancellation?.Cancel(); StopImagePreview(); StopModelPreview(); bookmarkTimer.Stop(); };
        bookmarkTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        bookmarkTimer.Tick += (_, _) => { bookmarkTimer.Stop(); ChangeBookmark(true, pendingBookmarkDirectory); pendingBookmarkDirectory = null; };
        var args = Environment.GetCommandLineArgs();
        if (args.Length == 2) Loaded += async (_, _) => await LoadPackage(args[1]);
    }
    private void PreferenceFailure(Exception ex) => Status.Text = "設定を保存できません: " + ex.Message;
    private void RememberPackage(string path)
    {
        try { preferences = preferenceStore.RecordPackage(preferences, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { PreferenceFailure(ex); }
        UpdateBookmark();
    }
    private string? CurrentDirectory => report == null ? null : Path.GetDirectoryName(Path.GetFullPath(report.SourcePath));
    private void UpdateBookmark()
    {
        bool saved = CurrentDirectory is { } folder && preferences.Bookmarks.Contains(folder, StringComparer.OrdinalIgnoreCase);
        BookmarkButton.Foreground = saved ? System.Windows.Media.Brushes.DarkGoldenrod : Foreground;
        BookmarkButton.ToolTip = "シングルクリック: 現在のフォルダーを追加\nダブルクリック: 削除\n右クリック: 一覧から開く";
    }
    private void Bookmark_Click(object sender, RoutedEventArgs e) { bookmarkTimer.Stop(); pendingBookmarkDirectory = CurrentDirectory; bookmarkTimer.Start(); }
    private void Bookmark_Double(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        bookmarkTimer.Stop(); pendingBookmarkDirectory = null; ChangeBookmark(false); e.Handled = true;
    }
    private void ChangeBookmark(bool add, string? targetDirectory = null)
    {
        if ((targetDirectory ?? CurrentDirectory) is not { } folder) { Status.Text = "パッケージを開くと、そのフォルダーを登録できます。"; return; }
        try { preferences = preferenceStore.Bookmark(preferences, folder, add); UpdateBookmark(); Status.Text = add ? "ブックマークに追加しました" : "ブックマークから削除しました"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { PreferenceFailure(ex); }
    }
    private void Bookmark_Opening(object sender, ContextMenuEventArgs e)
    {
        var menu = BookmarkButton.ContextMenu; menu.Items.Clear();
        if (preferences.Bookmarks.Count == 0) menu.Items.Add(new MenuItem { Header = "ブックマークはありません", IsEnabled = false });
        foreach (string folder in preferences.Bookmarks)
        {
            var item = new MenuItem { Header = folder.Replace("_", "__") };
            item.Click += async (_, _) => await OpenDirectory(folder); menu.Items.Add(item);
        }
    }
    private async Task OpenDirectory(string folder)
    {
        if (scanCancellation != null) return;
        if (!Directory.Exists(folder)) { Status.Text = "フォルダーが見つかりません: " + folder; return; }
        var dialog = new OpenFileDialog { Filter = "Unity Package (*.unitypackage)|*.unitypackage", InitialDirectory = folder, Title = "ブックマークからパッケージを開く" };
        if (dialog.ShowDialog(this) == true) await LoadPackage(dialog.FileName);
    }
    private void Address_Click(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PackagePath, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        PackagePath.ContextMenu = menu;
        if (preferences.RecentPackages.Count == 0) menu.Items.Add(new MenuItem { Header = "履歴はありません", IsEnabled = false });
        foreach (string path in preferences.RecentPackages.Take(10))
        {
            var item = new MenuItem { Header = path.Replace("_", "__") };
            item.Click += async (_, _) => await LoadPackage(path); menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new LoadSettingsWindow(preferences) { Owner = this };
        if (window.ShowDialog() != true) return;
        try
        {
            var next = preferences with { PackageLimitMiB = window.Result.PackageLimitMiB, PackageUnlimited = window.Result.PackageUnlimited, CacheLimitMiB = window.Result.CacheLimitMiB, CacheUnlimited = window.Result.CacheUnlimited };
            preferenceStore.Write(next); preferences = next; new RenderCache(Path.Combine(preferenceStore.DirectoryPath, "RenderCache")).Trim(preferences); Status.Text = "読み込み設定を保存しました";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { PreferenceFailure(ex); }
    }
}
