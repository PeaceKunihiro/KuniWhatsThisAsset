using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;

internal static class FeatureChecks
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    public static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) }; timer.Tick += (_, _) => { if (task.IsCompleted || clock.Elapsed.TotalSeconds > 50) frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!task.IsCompleted) throw new Exception("Feature timed out"); task.GetAwaiter().GetResult();
    }
    public static void Run(MainWindow main)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        string file = Path.GetFullPath("artifacts/test-packages/prefab.unitypackage");
        string modelGuid = new('1', 32), prefabGuid = new('2', 32), wrapperGuid = new('3', 32), cycleGuid = new('4', 32);
        string prefab = $"%YAML 1.1\n--- !u!1 &1\nGameObject:\n  m_Name: Visible\n  m_IsActive: 1\n--- !u!4 &2\nTransform:\n  m_GameObject: {{fileID: 1}}\n  m_Father: {{fileID: 0}}\n--- !u!137 &3\nSkinnedMeshRenderer:\n  m_GameObject: {{fileID: 1}}\n  m_Enabled: 1\n  m_Materials: []\n  m_Mesh: {{fileID: 4300000, guid: {modelGuid}, type: 3}}\n--- !u!1 &4\nGameObject:\n  m_Name: Hidden\n  m_IsActive: 0\n--- !u!137 &5\nSkinnedMeshRenderer:\n  m_GameObject: {{fileID: 4}}\n  m_Enabled: 1\n  m_Mesh: {{fileID: 4300002, guid: {modelGuid}, type: 3}}\n";
        string Wrapper(string target) => $"%YAML 1.1\n--- !u!1001 &100\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 100100000, guid: {target}, type: 3}}\n";
        using (var gzip = new GZipStream(File.Create(file), CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar))
        {
            void Add(string guid, string path, string text, string meta = "fileFormatVersion: 2\n")
            {
                foreach (var (name, value) in new[] { ("asset", text), ("pathname", path), ("asset.meta", meta) })
                { using var input = new MemoryStream(Encoding.UTF8.GetBytes(value)); tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, guid + "/" + name) { DataStream = input }); }
            }
            Add(modelGuid, "Assets/scene.obj", "o Visible\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\no Hidden\nv 0 0 1\nv 1 0 1\nv 0 1 1\nf 4 5 6\n", "fileFormatVersion: 2\nModelImporter:\n  fileIDToRecycleName:\n    4300000: Visible\n    4300002: Hidden\n");
            Add(prefabGuid, "Assets/Base.prefab", prefab); Add(wrapperGuid, "Assets/Nested.prefab", Wrapper(prefabGuid)); Add(cycleGuid, "Assets/Cycle.prefab", Wrapper(cycleGuid));
        }
        var reader = new PackageReader(); var report = reader.Read(file); var item = report.Items.Single(i => i.Guid == wrapperGuid);
        var build = PrefabPreviewBuilder.BuildAsync(reader, report, item, CancellationToken.None); Pump(build);
        Check(build.Result.Model.Statistics.TriangleCount == 1, "Prefab hidden renderer or mesh ID mapping incorrect");
        bool rejected = false;
        try { Pump(PrefabPreviewBuilder.BuildAsync(reader, report, report.Items.Single(i => i.Guid == cycleGuid), CancellationToken.None)); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Circular prefab accepted");
        var view = new ModelViewport(); view.SetModel(build.Result.Model);
        var photos = view.CaptureViews();
        for (int i = 0; i < 3; i++)
        {
            var bitmap = ImagePreviewDecoder.Decode(photos[i], "photo.png"); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Check(bitmap.PixelWidth == 1024 && pixels[3] == 0, "Cache photo is not transparent 1024px");
            if (i == 0) Check(Enumerable.Range(0, pixels.Length / 4).Any(p => pixels[p * 4 + 3] > 0), "Front cache photo is blank");
        }
        var cache = new RenderCache(Path.GetFullPath("artifacts/ui-cache-" + Guid.NewGuid().ToString("N")));
        string key = RenderCache.KeyAsync(file, wrapperGuid, CancellationToken.None).GetAwaiter().GetResult();
        Check(cache.Write(key, new(build.Result.Model.Statistics, photos, []), new()), "Cache write failed");
        var cached = cache.Read(key)!; view.ShowCached(cached, () => { });
        Check(cached.Pictures.Length == 3, "Cached views missing");
        typeof(MainWindow).GetMethod("StopModelPreview", Flags)!.Invoke(main, null);
        var render = (Task)typeof(MainWindow).GetMethod("ShowModelAsync", Flags)!.Invoke(main, [item, report, new ScanLimits(), new Func<ModelStatistics, bool>(_ => throw new Exception("Small prefab prompted"))])!; Pump(render);
        typeof(MainWindow).GetMethod("StopModelPreview", Flags)!.Invoke(main, null);
        var again = (Task)typeof(MainWindow).GetMethod("ShowModelAsync", Flags)!.Invoke(main, [item, report, new ScanLimits(), null])!; Pump(again);
        var status = (TextBlock)typeof(MainWindow).GetField("Status", Flags)!.GetValue(main)!; Check(status.Text.Contains("キャッシュ"), "Model selection did not reuse cached photos");
        Console.WriteLine("PASS direct/nested Prefab, disabled renderer, cycle refusal, transparent orthographic views and cached selection");
        Pump((Task)typeof(MainWindow).GetMethod("LoadPackage", Flags)!.Invoke(main, [file])!);
        typeof(MainWindow).GetMethod("Bookmark_Click", Flags)!.Invoke(main, [main, new RoutedEventArgs()]);
        Pump(Task.Delay(1000));
        var preferences = (UserPreferences)typeof(MainWindow).GetField("preferences", Flags)!.GetValue(main)!;
        Check(preferences.Bookmarks.Contains(Path.GetDirectoryName(file)!, StringComparer.OrdinalIgnoreCase), "Single bookmark click not registered");
        typeof(MainWindow).GetMethod("Bookmark_Opening", Flags)!.Invoke(main, [main, null]);
        var star = (Button)typeof(MainWindow).GetField("BookmarkButton", Flags)!.GetValue(main)!; Check(star.ContextMenu.Items.Count == 1, "Bookmark menu empty");
        typeof(MainWindow).GetMethod("ChangeBookmark", Flags)!.Invoke(main, [false, null]);
        Check(((UserPreferences)typeof(MainWindow).GetField("preferences", Flags)!.GetValue(main)!).Bookmarks.Count == 0, "Bookmark delete action failed");
        typeof(MainWindow).GetMethod("Address_Click", Flags)!.Invoke(main, [main, null]);
        var address = (TextBox)typeof(MainWindow).GetField("PackagePath", Flags)!.GetValue(main)!; Check(address.ContextMenu.Items.Count >= 1 && address.ContextMenu.Items.Count <= 10, "Recent address menu missing"); address.ContextMenu.IsOpen = false;
        IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match; foreach (var descendant in Children<T>(child)) yield return descendant; }
        }
        var settingsWindow = new LoadSettingsWindow(new()) { Owner = main };
        settingsWindow.Loaded += (_, _) =>
        {
            settingsWindow.UpdateLayout();
            var capture = new RenderTargetBitmap((int)settingsWindow.ActualWidth, (int)settingsWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32); capture.Render(settingsWindow);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture)); using (var stream = File.Create("artifacts/load-settings-check.png")) encoder.Save(stream);
            var boxes = Children<TextBox>(settingsWindow).ToArray(); var checks = Children<CheckBox>(settingsWindow).ToArray(); var save = Children<Button>(settingsWindow).Single(b => Equals(b.Content, "保存"));
            boxes[0].Text = "0"; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(settingsWindow.DialogResult != true, "Invalid setting closed dialog");
            boxes[0].Text = "64"; boxes[1].Text = "128"; checks[1].IsChecked = true; Check(!boxes[1].IsEnabled, "Unlimited did not disable numeric input"); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        Check(settingsWindow.ShowDialog() == true && settingsWindow.Result.PackageLimitMiB == 64 && settingsWindow.Result.CacheLimitMiB == 128 && settingsWindow.Result.CacheUnlimited, "Settings dialog did not accept limits");
        Console.WriteLine("PASS address history menu, single-click directory bookmark, removal/list and numeric/unlimited settings dialog");
    }
}
