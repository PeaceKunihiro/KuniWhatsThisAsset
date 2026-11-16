using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;

internal static class ReferenceChecks
{
    public static int Actual()
    {
        try
        {
            var reader = new PackageReader(); var report = reader.Read("R:/餅亭 FUWATTOSYUGAENZYERU all v26.unitypackage");
            foreach (string guid in new[] { "3ad7259f317033142871f1c47fa4332d", "ca0c83d08641b7c43b106b968ea51eea" })
            {
                var item = report.Items.Single(i=>i.Guid==guid);
                var result = Task.Run(()=>PrefabPreviewBuilder.BuildAsync(reader,report,item,CancellationToken.None)).GetAwaiter().GetResult();
                File.WriteAllText("artifacts/fuwatto-analysis/bindings-"+guid+".json",System.Text.Json.JsonSerializer.Serialize(new { item.Path,result.Model.Statistics, result.Model.Notes, Meshes=result.Model.Meshes.Select(m=>new {m.NodePath,m.MaterialName})},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                var resolution = new MaterialResolver(reader,report,CancellationToken.None).Resolve(item,result.MaterialMetadata,result.Model);
                if (resolution.Issues.Count!=0 || result.Model.Meshes.Any(m=>!resolution.Selected.ContainsKey(m.MaterialName))) throw new Exception("Unresolved materials: "+item.Path);
                string expected=guid.StartsWith("3ad")?"013007931a6af3e4c9b2d5730e47138a":"c592bef9ce8593c40b40a04333023784";
                if (!result.Model.Meshes.Any(m=>m.MaterialName=="material_"+expected)) throw new Exception("Variant override not applied: "+item.Path);
                Console.WriteLine("PASS FUWATTO material bindings: "+item.Path+" / "+result.Model.Statistics+" / "+string.Join(";",result.Model.Notes));
            }
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    public static void Run()
    {
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        Check(PrefabAnalysis.DecodeName("\"\\u76EE\"") == "目", "Unity YAML Unicode name");
        Check(UnityModelIds.Id("GameObject", "//RootNode/root") == "919132149155446097", "Unity root ID vector");
        Check(UnityModelIds.Id("Transform", "//RootNode/root/Transform") == "-8679921383154817045", "Unity transform ID vector");
        Check(UnityModelIds.Id("SkinnedMeshRenderer", "//RootNode/root/靴.012/SkinnedMeshRenderer") == "-8747551652930279044", "Unicode renderer ID vector");
        Check(PrefabPreviewBuilder.InstanceId("4118895666319243673", "4073970060232334371") == "117348567359890874", "Nested ID vector");
        string model = new('a',32), baseGuid = new('b',32), variant = new('c',32), hidden = new('d',32), broken = new('e',32), first = new('1',32), second = new('2',32);
        string Renderer(string name) => UnityModelIds.Id("MeshRenderer", "//RootNode/root/" + name + "/MeshRenderer");
        string Change(string guid, string id, string property, string value = "", string reference = "") => $"    - target: {{fileID: {id}, guid: {guid}, type: 3}}\n      propertyPath: {property}\n      value: {value}\n      objectReference: {{fileID: {(reference.Length == 0 ? "0" : "2100000")}, guid: {(reference.Length == 0 ? new string('0',32) : reference)}, type: 2}}\n";
        string Wrapper(string guid, string id, string changes) => $"%YAML 1.1\n--- !u!1001 &{id}\nPrefabInstance:\n  m_Modification:\n    m_TransformParent: {{fileID: 0}}\n    m_Modifications:\n{changes}  m_SourcePrefab: {{fileID: 100100000, guid: {guid}, type: 3}}\n";
        string file = Path.GetFullPath("artifacts/test-packages/references.unitypackage");
        using (var gzip = new GZipStream(File.Create(file), CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar))
        {
            void Add(string guid, string path, string text)
            {
                foreach (var (name,value) in new[] {("asset",text),("pathname",path),("asset.meta","fileFormatVersion: 2\n")})
                { using var input = new MemoryStream(Encoding.UTF8.GetBytes(value)); tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile,guid+"/"+name){DataStream=input}); }
            }
            Add(model,"Assets/mesh.obj","o Visible\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\no Hidden\nv 0 0 1\nv 1 0 1\nv 0 1 1\nf 4 5 6\n");
            Add(baseGuid,"Assets/Base.prefab",Wrapper(model,"100",Change(model,Renderer("Visible"),"m_Materials.Array.data[0]",reference:first)+Change(model,Renderer("Hidden"),"m_Enabled","0")));
            Add(variant,"Assets/Variant.prefab",Wrapper(baseGuid,"200",Change(baseGuid,PrefabPreviewBuilder.InstanceId("100",Renderer("Visible")),"m_Materials.Array.data[0]",reference:second)));
            Add(hidden,"Assets/Hidden.prefab",Wrapper(variant,"300",Change(variant,PrefabPreviewBuilder.InstanceId("200",PrefabPreviewBuilder.InstanceId("100",UnityModelIds.Id("GameObject","//RootNode/root/Visible"))),"m_IsActive","0")));
            Add(broken,"Assets/Broken.prefab",Wrapper(baseGuid,"400",Change(baseGuid,"999999","m_Materials.Array.data[0]",reference:second)));
        }
        var reader = new PackageReader(); var report = reader.Read(file);
        PrefabPreview Build(string guid) { var task=PrefabPreviewBuilder.BuildAsync(reader,report,report.Items.Single(i=>i.Guid==guid),CancellationToken.None); FeatureChecks.Pump(task); return task.Result; }
        var result=Build(variant);
        Check(result.Model.Statistics.TriangleCount==1 && result.Model.Meshes.Single().MaterialName=="material_"+second,"Nested variant material/renderer override");
        bool refused=false; try { Build(hidden); } catch (InvalidDataException) { refused=true; } Check(refused,"Inherited GameObject inactive override");
        var unresolved=Build(broken); Check(unresolved.Model.Meshes.Single().MaterialName=="material_"+first && unresolved.Model.Notes.Any(n=>n.Contains("対象ID")),"Unknown target must preserve binding and warn");
        Console.WriteLine("PASS Unity ID vectors, nested variants, renderer/object visibility and unresolved target refusal");
    }
}
