using System.IO;
using System.Text.Json;
using System.Windows;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;
internal static class RealModelChecks
{
    public static int Run()
    {
        var app = new App(); app.InitializeComponent();
        try
        {
            var inputs = new[] {
                ("Fuzzy", "E:/GPT_WORKS/KWT_TEST/Fuzzy_ver.2.00/Fuzzy.unitypackage", "Assets/Avatar_Fuzzy/Fuzzy_Model/Fuzzy.fbx"),
                ("Luchika", "E:/GPT_WORKS/KWT_TEST/Luchika_ver.1.00/Luchika.unitypackage", "Assets/KOYORI LABO/Avatar_Luchika/Model/Luchika.fbx") };
            foreach (var (name, file, modelPath) in inputs)
            {
                var reader = new PackageReader(new ScanLimits { IgnoreReadLimits = true });
                var report = reader.Read(file);
                var item = report.Items.Single(i => i.Path == modelPath);
                var bytes = reader.ReadAssetBytes(file, item.Guid, 256 * 1024 * 1024);
                var data = ModelWorker.ImportAsync(new(modelPath, bytes, new()), CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText("artifacts/" + name + "-model-info.json", JsonSerializer.Serialize(new { data.Statistics, Meshes = data.Meshes.Select(m => new { m.MaterialName, m.NodeName, m.TexturePath, Vertices = m.Positions.Length / 3, Triangles = m.Indices.Length / 3 }) }, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllBytes("artifacts/" + name + "-model-meta.txt", reader.ReadAssetMetaBytes(file, item.Guid));
                var matBytes = reader.ReadAssetSet(file, report.Items.Where(i => i.Format == ".mat" && i.Path.Contains("/Model/") || i.Format == ".mat" && i.Path.Contains("/Fuzzy_Model/")).Select(i => i.Guid).ToArray(), maxFileBytes: 2 * 1024 * 1024);
                foreach (var (guid, content) in matBytes) File.WriteAllBytes("artifacts/" + name + "-" + guid + ".mat", content);
                var materials = ModelMaterials.Load(reader, report, item, File.ReadAllText("artifacts/" + name + "-model-meta.txt"), data, CancellationToken.None);
                Console.WriteLine(name + " materials: " + string.Join(", ", materials.Keys));
                var view = new ModelViewport(); view.SetModel(data, materials);
                var window = new Window { Content = view, Width = 900, Height = 1000 }; window.Show(); window.UpdateLayout();
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
                var capture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); capture.Render(view);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(capture));
                using (var output = File.Create("artifacts/" + name + "-render.png")) encoder.Save(output);
                window.Close();
                Console.WriteLine(name + ": " + JsonSerializer.Serialize(data.Statistics));
                string prefabPath = name == "Fuzzy" ? "Assets/Avatar_Fuzzy/Fuzzy.prefab" : "Assets/KOYORI LABO/Avatar_Luchika/Luchika.prefab";
                var prefabItem = report.Items.Single(i => i.Path == prefabPath);
                var prefabTask = Task.Run(() => PrefabPreviewBuilder.BuildAsync(reader, report, prefabItem, CancellationToken.None)); FeatureChecks.Pump(prefabTask);
                var prefab = prefabTask.Result;
                var prefabMaterials = ModelMaterials.Load(reader, report, prefabItem, prefab.MaterialMetadata, prefab.Model, CancellationToken.None);
                view.SetModel(prefab.Model, prefabMaterials);
                var photos = view.CaptureViews();
                for (int index = 0; index < 3; index++) File.WriteAllBytes("artifacts/" + name + "-prefab-" + RenderCache.Views[index] + ".png", photos[index]);
                File.WriteAllText("artifacts/" + name + "-prefab-info.json", JsonSerializer.Serialize(new { prefab.Model.Statistics, prefab.Model.Notes }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(name + " Prefab: " + JsonSerializer.Serialize(prefab.Model.Statistics) + " textures=" + prefabMaterials.Count);
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); }
    }
}
