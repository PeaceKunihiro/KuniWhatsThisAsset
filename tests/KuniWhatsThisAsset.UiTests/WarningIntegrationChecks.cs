using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;

internal static class WarningIntegrationChecks
{
    public static void Run(MainWindow main)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(main, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(main)!;
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        var grid = Field<DataGrid>("FindingsGrid"); var notice = Field<Expander>("SecurityNotice");
        Call("ClearPreviewFindings"); Call("Notify", (object)Array.Empty<Finding>());
        var reader = new PackageReader(); var report = reader.Read(Path.GetFullPath("artifacts/test-packages/prefab.unitypackage"));
        var item = report.Items.Single(i => i.Path == "Assets/Nested.prefab");
        Call("SetPreviewNotes", item, (object)new[] { "Prefab静止近似：表示の制限。" });
        Check(notice.Visibility == Visibility.Collapsed, "Normal limitation info exposed a warning panel");
        var baseline = new Finding(Severity.High, "SECURITY_TEST", "Assets/test.cs", "Existing security finding");
        Call("Notify", (object)new[] { baseline });
        var notes = Enumerable.Range(0, 80).Select(i => $"材質「m{i}」: 参照を解決できません（基本色で表示）。")
            .Concat(new[] { "材質「low」: Prefab補完の解析件数上限を超えています（基本色で表示）。", "材質「" + new string('a', 32) + "_low」: 対応する.matを解決できません（基本色で表示）。", "Prefab静止近似：表示の制限。" }).ToArray();
        var model = new ModelPreviewData(new(3, 1, 1, 1, 0, false), [new([0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 1, 2], [1, 1, 1, 1])], notes);
        var view = Field<ModelViewport>("ModelView"); view.SetModel(model);
        var info = (TextBlock)typeof(ModelViewport).GetField("info", flags)!.GetValue(view)!;
        Check(!info.Text.Contains("材質") && info.Text.Split('\n').Length == 3, "Diagnostics filled live preview");
        var photos = view.CaptureViews();
        var store = Field<PreferenceStore>("preferenceStore"); var cache = new RenderCache(Path.Combine(store.DirectoryPath, "RenderCache"));
        string key = RenderCache.KeyAsync(report.SourcePath, item.Guid, CancellationToken.None).GetAwaiter().GetResult();
        cache.Write(key, new(model.Statistics, photos, notes), new());
        Call("StopModelPreview"); FeatureChecks.Pump((Task)Call("ShowModelAsync", item, report, new ScanLimits(), null)!);
        Check(!info.Text.Contains("材質") && info.Text.Split('\n').Length == 2, "Old cache notes filled preview");
        Check(grid.Items.Cast<Finding>().Contains(baseline) && grid.Items.Count == 83, "Warning merge or legacy duplicate suppression failed");
        Check(grid.Items.Cast<Finding>().First() == baseline, "Preview warnings displaced a high-severity security finding");
        Check(!notice.IsExpanded && notice.Visibility == Visibility.Visible, "Diagnostics did not remain passive");
        notice.IsExpanded = true;
        Call("SetPreviewNotes", item, (object)new[] { "材質「new」: 未解決。" });
        Check(notice.IsExpanded && grid.Items.Count == 2, "Updating preview diagnostics collapsed panel or lost security finding");
        main.UpdateLayout(); FeatureChecks.Pump(Task.Delay(150));
        Console.WriteLine("Warning grid widths: " + grid.ActualWidth + " / " + string.Join(",", grid.Columns.Select(c => c.ActualWidth)));
        Call("DetachWarnings_Click", main, new RoutedEventArgs(Button.ClickEvent));
        var detached = Field<Window>("warningWindow");
        Call("SetPreviewNotes", item, (object)new[] { "材質「other」: 未解決。" });
        Check(detached.IsVisible && grid.Items.Count == 2, "Detached warning contents were not shared");
        Call("ClearPreviewFindings"); Check(grid.Items.Count == 1 && grid.Items.Cast<Finding>().Contains(baseline), "Selection clear removed package findings");
        Call("Notify", (object)Array.Empty<Finding>()); detached.Close();
        Call("StopModelPreview");
        var missing = new PackageItem(new string('0', 32), "Assets/missing.fbx", 0, false, [], null);
        FeatureChecks.Pump((Task)Call("ShowModelAsync", missing, report, new ScanLimits(), null)!);
        Check(grid.Items.Cast<Finding>().Any(f => f.Code == "PREVIEW_MODEL_FAILED") && Field<TextBlock>("PreviewHint").Text == "モデルを表示できません。警告欄を確認してください。", "Model error details did not route to warnings");
        var image = missing with { Path = "Assets/missing.png" };
        FeatureChecks.Pump((Task)Call("ShowImageAsync", image, report, new ScanLimits())!);
        Check(grid.Items.Cast<Finding>().Single().Code == "PREVIEW_IMAGE_FAILED" && Field<TextBlock>("PreviewHint").Text == "画像を表示できません。警告欄を確認してください。", "Image error details did not route to warnings");
        Call("ClearPreviewFindings");
        Check(notice.Visibility == Visibility.Collapsed, "Clearing preview left empty warning space");
        Console.WriteLine("PASS 80 live/cached diagnostics in shared passive warning panel, security preservation, detach, lifecycle and model/image failures");
    }
}
