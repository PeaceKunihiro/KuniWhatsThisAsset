using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;

internal static class TextureModelChecks
{
    public static int Run()
    {
        var app = new App(); app.InitializeComponent();
        try
        {
            string directory = Path.GetFullPath("artifacts/texture-fixed-v0.9.1"); Directory.CreateDirectory(directory);
            var inputs = new[] {
                ("Necomaid", "E:/GPT_WORKS/KWT_TEST/5000porynecomaid/5000porynecomaid1.1.unitypackage"),
                ("Akyo", "E:/GPT_WORKS/KWT_TEST/Akyo_KTNTK_v1.01/KTNTK_Akyo_v1.01.unitypackage") };
            foreach (var (name, file) in inputs)
            {
                var reader = new PackageReader(); var report = reader.Read(file);
                foreach (var item in report.Items.Where(i => i.Format is ".fbx" or ".prefab"))
                {
                    ModelPreviewData model; string meta;
                    if (item.Format == ".fbx")
                    {
                        model = ModelWorker.ImportAsync(new(item.Path, reader.ReadAssetBytes(file, item.Guid), new()), CancellationToken.None).GetAwaiter().GetResult();
                        meta = TextDecoder.Decode(reader.ReadAssetMetaBytes(file, item.Guid), false, out _);
                    }
                    else
                    {
                        var prefab = Task.Run(() => PrefabPreviewBuilder.BuildAsync(reader, report, item, CancellationToken.None)).GetAwaiter().GetResult(); model = prefab.Model; meta = prefab.MaterialMetadata;
                    }
                    // These are the production resolver/decoder; no diagnostic remap is supplied.
                    var resolution = new MaterialResolver(reader, report, CancellationToken.None).Resolve(item, meta, model);
                    string expected = name == "Necomaid" ? "4e655fc59f4bbe641b13eae812b4f7d6" : item.Name.Contains("Quest") ? "bd03da30c3279d445b862ff699f38898" : "ee4ee302e7bbc02498fb45bfa3dea393";
                    var result = ModelMaterials.LoadDetailed(reader, report, item, meta, model, CancellationToken.None);
                    int textured = result.Materials.Values.OfType<MaterialGroup>().Count(m => m.Children.OfType<DiffuseMaterial>().Any(d => d.Brush is ImageBrush));
                    if (resolution.Selected.Count != 1 || resolution.Selected.Values.Single().Guid != expected || textured != 1 || result.Notes.Length != 0) throw new Exception("Material/texture resolution failed: " + item.Path + " " + string.Join(";", result.Notes));
                    var view = new ModelViewport(); view.SetModel(model, result.Materials);
                    string label = name + "-" + Path.GetFileNameWithoutExtension(item.Path) + "-" + item.Format.TrimStart('.');
                    File.WriteAllBytes(Path.Combine(directory, label + ".png"), view.CaptureViews()[0]);
                    File.WriteAllText(Path.Combine(directory, label + ".json"), JsonSerializer.Serialize(new { item.Path, model.Statistics, MaterialGuids = resolution.Selected.Values.Select(i => i.Guid), TexturedCount = textured, result.Notes }, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine("PASS real texture: " + item.Path + " -> " + expected);
                }
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); }
    }
}
