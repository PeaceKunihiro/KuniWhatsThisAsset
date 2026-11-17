using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public partial class MainWindow
{
    private const string OpenFilter = "対応ファイル (*.unitypackage;*.fbx;*.obj)|*.unitypackage;*.fbx;*.obj|Unity Package (*.unitypackage)|*.unitypackage|FBX (*.fbx)|*.fbx|OBJ (*.obj)|*.obj";
    private LocalModelDocument? localDocument;
    private bool rebuildingLocalTree;
    private sealed record LocalTreeFile(string Path, bool Model);
    private void BuildLocalTree(LocalModelDocument document)
    {
        rebuildingLocalTree = true;
        try
        {
        PackageTree.Items.Clear();
        var root = new TreeViewItem { Header = Path.GetFileName(document.SourcePath), Tag = new LocalTreeFile(Path.GetFileName(document.SourcePath), true), IsExpanded = true };
        foreach (string file in document.Files.Keys.Where(p => !p.Equals(Path.GetFileName(document.SourcePath), StringComparison.OrdinalIgnoreCase))) root.Items.Add(new TreeViewItem { Header = file, Tag = new LocalTreeFile(file, false) });
        PackageTree.Items.Add(root);
        }
        finally { rebuildingLocalTree = false; }
    }
    private async void ShowLocalTreeFile(LocalTreeFile file)
    {
        if (localDocument == null) return;
        if (file.Model) { if (scanCancellation == null) _ = LoadLocalModelAsync(localDocument.SourcePath); return; }
        StopModelPreview(); StopImagePreview(); ClearPreviewFindings(); Preview.Visibility = Visibility.Collapsed; PreviewHint.Text = "";
        if (!localDocument.Files.TryGetValue(file.Path, out var bytes)) return;
        using var cts = new CancellationTokenSource(); imageCancellation = cts;
        try
        {
            if (ImagePreviewDecoder.Supports(file.Path))
            { var image = await Task.Run(() => ImagePreviewDecoder.Decode(bytes, file.Path), cts.Token); if (cts.IsCancellationRequested) return; ImagePreview.Source = image; ImagePreview.Visibility = Visibility.Visible; }
            else { Preview.Text = TextDecoder.Decode(bytes, bytes.Length > 32768, out _); if (Preview.Text.Length > 32768) Preview.Text = Preview.Text[..32768] + "\n…（表示省略）"; Preview.Visibility = Visibility.Visible; }
            Status.Text = file.Path + " · " + bytes.Length.ToString("N0") + " B";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        { if (!cts.IsCancellationRequested) AddPreviewFailure(new("", file.Path, bytes.Length, false, [], null), "LOCAL_PREVIEW", ex.Message); }
        finally { if (ReferenceEquals(imageCancellation, cts)) imageCancellation = null; }
    }
    private async Task LoadLocalModelAsync(string path, Func<ModelStatistics, bool>? confirm = null, bool useCache = true)
    {
        if (scanCancellation != null) return;
        using var cts = new CancellationTokenSource(); scanCancellation = cts;
        StopModelPreview(); StopImagePreview();
        report = null; localDocument = null; RepackageMenu.IsEnabled = false; PackageTree.Items.Clear(); fileNodes.Clear(); history.Clear(); historyIndex = -1; UpdateNavigation();
        modelCancellation = cts;
        ClearPreviewFindings(); Notify([]); Preview.Visibility = Visibility.Collapsed; PackagePath.Text = path; PreviewHint.Text = "モデルを解析中…";
        OpenMenu.IsEnabled = ReloadButton.IsEnabled = false; CancelButton.Visibility = BusyProgress.Visibility = Visibility.Visible; BusyProgress.IsIndeterminate = true;
        var item = new PackageItem("local", Path.GetFileName(path), 0, false, [], null);
        try
        {
            var document = await Task.Run(() => LocalModelDocument.Read(path, cts.Token), cts.Token);
            localDocument = document; Notify(document.Findings); BuildLocalTree(document); RememberPackage(document.SourcePath);
            var model = await ModelWorker.ImportAsync(document.Input, cts.Token);
            model = ModelMeasurements.ApplyImportSettings(model, document.MetadataText);
            await Task.Run(() => document.ReadTextures(model, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested(); Notify(document.Findings); BuildLocalTree(document);
            var cache = new RenderCache(Path.Combine(preferenceStore.DirectoryPath, "RenderCache")); string key = await Task.Run(document.CacheKey, cts.Token);
            CachedRender? cached = null;
            if (useCache)
            {
                try { cached = await Task.Run(() => cache.Read(key), cts.Token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException) { document.Notes.Add("キャッシュを読み込めないため、再描画します: " + ex.Message); }
            }
            cts.Token.ThrowIfCancellationRequested();
            if (cached != null)
            {
                ModelView.ShowCached(cached, () => { _ = LoadLocalModelAsync(path, confirm, false); });
                ModelView.Visibility = Visibility.Visible; PreviewHint.Text = ""; SetPreviewNotes(item, cached.Notes); Status.Text = item.Name + " · レンダリングキャッシュ"; return;
            }
            SetPreviewNotes(item, LocalNotes(model.Notes.Concat(document.Notes)));
            if (!ModelRenderPolicy.Approve(model.Statistics, confirm ?? ConfirmLocalModel)) { PreviewHint.Text = "モデルのレンダリングを中止しました。"; Status.Text = item.Name + " · 描画中止"; return; }
            cts.Token.ThrowIfCancellationRequested(); PreviewHint.Text = "テクスチャを読み込み中…";
            var materials = await Task.Run(() => document.Materials(model, cts.Token), cts.Token);
            model = model with { Notes = LocalNotes(model.Notes.Concat(document.Notes).Concat(materials.Notes)) };
            cts.Token.ThrowIfCancellationRequested(); ModelView.SetModel(model, materials.Materials); ModelView.Visibility = Visibility.Visible; PreviewHint.Text = ""; SetPreviewNotes(item, model.Notes);
            Status.Text = $"{item.Name} · {model.Statistics.TriangleCount:N0}ポリゴン（三角形） · Material {model.Statistics.MaterialCount:N0}";
            try
            {
                var entry = new CachedRender(model.Statistics, ModelView.CaptureViews(), model.Notes) { Measurement = ModelMeasurements.Calculate(model) };
                if (!await Task.Run(() => { cts.Token.ThrowIfCancellationRequested(); return cache.Write(key, entry, preferences); }, cts.Token)) AddPreviewFailure(item, "PREVIEW_CACHE", "写真一式がキャッシュ容量の上限を超えるため、保存を省略しました。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { if (!cts.IsCancellationRequested) AddPreviewFailure(item, "PREVIEW_CACHE", "キャッシュを保存できません: " + ex.Message); }
        }
        catch (OperationCanceledException) { if (ReferenceEquals(modelCancellation, cts)) { PreviewHint.Text = "モデルの読み込みを中止しました。"; Status.Text = "読み込み中止"; } }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or System.Text.RegularExpressions.RegexMatchTimeoutException)
        { if (!cts.IsCancellationRequested) { AddPreviewFailure(item, "LOCAL_MODEL_FAILED", ex.Message); PreviewHint.Text = "モデルを表示できません。警告欄を確認してください。"; Status.Text = "モデルの読み込み未完了"; } }
        finally
        {
            if (ReferenceEquals(modelCancellation, cts)) modelCancellation = null;
            scanCancellation = null; OpenMenu.IsEnabled = ReloadButton.IsEnabled = true; CancelButton.Visibility = BusyProgress.Visibility = Visibility.Collapsed; BusyProgress.IsIndeterminate = false;
        }
    }
    private static string[] LocalNotes(IEnumerable<string> notes) => notes.Distinct().Take(100).Select(n => n.Length > 4096 ? n[..4096] : n).ToArray();
    private bool ConfirmLocalModel(ModelStatistics stats) => MessageBox.Show(this, $"このモデルのレンダリングは高負荷になる可能性があります。\n\nポリゴン数（三角形換算）: {stats.TriangleCount:N0}\n依存マテリアル数: {stats.MaterialCount:N0}\n\n警告条件：100,000ポリゴン以上、またはマテリアル10以上。\nレンダリングを開始しますか？", "モデル描画の負荷確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
