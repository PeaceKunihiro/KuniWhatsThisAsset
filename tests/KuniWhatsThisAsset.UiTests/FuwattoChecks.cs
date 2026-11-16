using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;

internal static class FuwattoChecks
{
    public static int Run()
    {
        var app = new App(); app.InitializeComponent();
        string directory = Path.GetFullPath("artifacts/fuwatto-analysis"); Directory.CreateDirectory(directory);
        var main = new MainWindow(Path.Combine(directory, "profile-" + Guid.NewGuid().ToString("N"))) { Width = 1280, Height = 900 };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(main, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(main)!;
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        void Capture(string filename)
        {
            main.UpdateLayout();
            var image = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(main);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(directory, filename)); encoder.Save(stream);
        }
        try
        {
            main.Show();
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(main.Dispatcher));
            var read = Task.Run(() => new PackageReader().Read("R:/餅亭 FUWATTOSYUGAENZYERU all v26.unitypackage")); FeatureChecks.Pump(read);
            var report = read.Result;
            var item = report.Items.Single(i => i.Guid == "3ad7259f317033142871f1c47fa4332d");
            typeof(MainWindow).GetField("report", flags)!.SetValue(main, report);
            Field<TextBox>("PackagePath").Text = report.SourcePath; Call("BuildTree"); Call("Notify", (object)report.Findings);
            var show = (Task)Call("ShowModelAsync", item, report, new ScanLimits(), new Func<ModelStatistics, bool>(_ => true))!; FeatureChecks.Pump(show, 180);
            var view = Field<ModelViewport>("ModelView"); var info = (TextBlock)typeof(ModelViewport).GetField("info", flags)!.GetValue(view)!;
            Check(view.Visibility == Visibility.Visible, "Target prefab failed to render: " + Field<TextBlock>("PreviewHint").Text);
            Check(info.Text.Split('\n').Length == 3 && !info.Text.Contains("材質「"), "Errors still cover the model");
            var findings = Field<DataGrid>("FindingsGrid").Items.Cast<Finding>().ToArray();
            Capture("v0.10.1-diagnostic.png");
            Check(!findings.Any(f => f.Code == "PREVIEW_REFERENCE" && (f.Message.Contains("解析件数上限") || f.Message.Contains("メッシュID") || f.Message.Contains("材質「"))), "Unresolved references remain: " + string.Join(";", findings.Where(f => f.Code == "PREVIEW_REFERENCE").Select(f => f.Message)));
            Check(!Field<Expander>("SecurityNotice").IsExpanded, "Warning panel expanded automatically");
            Check(report.Findings.All(findings.Contains), "Package security warnings were lost");
            Capture("v0.10.1-preview.png");
            Call("StopModelPreview");
            FeatureChecks.Pump((Task)Call("ShowModelAsync", item, report, new ScanLimits(), null)!);
            Check(Field<TextBlock>("Status").Text.Contains("レンダリングキャッシュ") && info.Text.Split('\n').Length == 2, "Cached preview regression");
            Check(Field<DataGrid>("FindingsGrid").Items.Count == findings.Length, "Cached diagnostics were lost");
            Capture("v0.10.1-cached.png");
            Field<Expander>("SecurityNotice").IsExpanded = true; FeatureChecks.Pump(Task.Delay(200)); Capture("v0.10.1-warnings.png");
            Console.WriteLine("Warning grid widths: " + Field<DataGrid>("FindingsGrid").ActualWidth + " / " + string.Join(",", Field<DataGrid>("FindingsGrid").Columns.Select(c => c.ActualWidth)));
            File.WriteAllText(Path.Combine(directory, "runtime-findings.json"), JsonSerializer.Serialize(new { PackageFindings = report.Findings.Count, PreviewFindings = findings.Where(f => f.Code.StartsWith("PREVIEW_")), Target = item.Path }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS real FUWATTO: {report.Findings.Count} package findings + {findings.Count(f => f.Code.StartsWith("PREVIEW_"))} preview diagnostics; live/cache preview stays clear");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { main.Close(); app.Shutdown(); }
    }
}
