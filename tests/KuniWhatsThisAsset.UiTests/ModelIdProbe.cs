using System.IO;
using System.Text.Json;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;
internal static class ModelIdProbe
{
    public static int Run()
    {
        try
        {
            var reader = new PackageReader(); const string file = "R:/餅亭 FUWATTOSYUGAENZYERU all v26.unitypackage";
            var report = reader.Read(file); var result = new List<object>();
            foreach (string guid in new[] { "10738785bd852e74b9ac2ad1ba3e2089", "d4fb5c0ed75bb6c4d99ecc5a5dd2b2c5", "521c8a37afc055d4cb578db0ff8c9a05" })
            {
                var item = report.Items.Single(i => i.Guid == guid); var model = ModelWorker.ImportAsync(new(item.Path, reader.ReadAssetBytes(file, guid), new()), CancellationToken.None).GetAwaiter().GetResult();
                result.Add(new { item.Path, Guid = guid, Meshes = model.Meshes.Select(m => new { m.NodeName, m.NodePath, m.MeshName, m.Skinned, m.MaterialName, RendererId = UnityModelIds.Renderer(m), ObjectId = UnityModelIds.GameObject(m), MeshId = UnityModelIds.Mesh(m) }) });
            }
            File.WriteAllText("artifacts/fuwatto-analysis/model-ids.json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
