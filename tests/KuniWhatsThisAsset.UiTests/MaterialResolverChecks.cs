using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;

internal static class MaterialResolverChecks
{
    public static void Run()
    {
        string source = new('a', 32), main = new('b', 32), quest = new('c', 32), near = new('d', 32);
        var entries = new List<(string Guid, string Path, string Text)>();
        string file = Path.GetFullPath("artifacts/test-packages/material-resolver.unitypackage");
        string metadata = "ModelImporter:\n  fileIDToRecycleName:\n    4300000: Body\n  materials:\n    materialSearch: 1\n    materialName: 1\n";
        var data = new ModelPreviewData(new(3, 1, 1, 1, 0, false), [new([0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 1, 2], [1, 1, 1, 1], MaterialName: "Main", NodeName: "Body", TexturePath: "Textures/Atlas.png")], []);
        string Prefab(string mat) => $"%YAML 1.1\n--- !u!1 &1\nGameObject:\n  m_Name: Body\n  m_IsActive: 1\n--- !u!137 &2\nSkinnedMeshRenderer:\n  m_GameObject: {{fileID: 1}}\n  m_Enabled: 1\n  m_Mesh: {{fileID: 4300000, guid: {source}, type: 3}}\n  m_Materials:\n  - {{fileID: 2100000, guid: {mat}, type: 2}}\n";
        MaterialResolution Resolve(string meta, ModelPreviewData? model = null)
        {
            using (var gzip = new GZipStream(File.Create(file), CompressionLevel.Fastest))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar))
            {
                foreach (var (guid, path, text) in entries.Prepend((source, "Assets/Avatar/FBX/model.fbx", "model")))
                    foreach (var (member, content) in new[] { ("pathname", path), ("asset", text), ("asset.meta", guid == source ? meta : "fileFormatVersion: 2\n") })
                    { using var input = new MemoryStream(Encoding.UTF8.GetBytes(content)); tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, guid + "/" + member) { DataStream = input }); }
            }
            var reader = new PackageReader(); var report = reader.Read(file);
            return new MaterialResolver(reader, report, CancellationToken.None).Resolve(report.Items.Single(i => i.Guid == source), meta, model ?? data);
        }
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        entries.Add((main, "Assets/Avatar/Materials/Main.mat", "Material:\n"));
        entries.Add((quest, "Assets/Avatar/Materials/Main_Quest.mat", "Material:\n"));
        Check(Resolve(metadata).Selected["Main"].Guid == main, "Sibling Materials was not found");
        var questData = data with { Meshes = [data.Meshes[0] with { MaterialName = "Main_Quest" }] };
        Check(Resolve(metadata, questData).Selected["Main_Quest"].Guid == quest, "PC/Quest material was mixed up");
        Check(Resolve(metadata.Replace("materialSearch: 1", "materialSearch: 0")).Selected.Count == 0, "Local search escaped into parent directory");
        entries.Add((near, "Assets/Avatar/FBX/Materials/Main.mat", "Material:\n"));
        Check(Resolve(metadata).Selected["Main"].Guid == near, "Nearest Materials did not take priority");
        string remap = metadata + $"  externalObjects:\n  - first:\n      name: Main\n    second: {{fileID: 2100000, guid: {main}, type: 2}}\n";
        Check(Resolve(remap).Selected["Main"].Guid == main, "Explicit remap did not take priority");
        var missing = Resolve(remap.Replace(main, new string('f', 32)));
        Check(missing.Selected.Count == 0 && missing.Issues["Main"].Contains("参照先"), "Missing explicit reference was guessed");
        entries.Clear(); entries.Add((main, "Assets/Avatar/Materials/Atlas.mat", "Material:\n"));
        Check(Resolve(metadata.Replace("materialName: 1", "materialName: 0")).Selected["Main"].Guid == main, "Texture-based naming not applied");
        entries.Clear(); entries.Add((main, "Assets/Avatar/Materials/model-Main.mat", "Material:\n"));
        Check(Resolve(metadata.Replace("materialName: 1", "materialName: 2")).Selected["Main"].Guid == main, "Model/material naming not applied");
        entries.Clear(); entries.Add((main, "Assets/Avatar/Materials/Renamed.mat", "Material:\n"));
        entries.Add((new string('1', 32), "Assets/Avatar/Base.prefab", Prefab(main)));
        var renamed = data with { Meshes = [data.Meshes[0] with { MaterialName = "low" }] };
        Check(Resolve(metadata, renamed).Selected["low"].Guid == main, "Unique prefab material slot not inferred");
        entries.Add((quest, "Assets/Avatar/Materials/Variant.mat", "Material:\n"));
        entries.Add((new string('2', 32), "Assets/Avatar/Variant.prefab", Prefab(quest)));
        var ambiguous = Resolve(metadata, renamed);
        Check(ambiguous.Selected.Count == 0 && ambiguous.Issues["low"].Contains("割り当てが異なり"), "Conflicting prefab variants were guessed");
        entries.RemoveAt(entries.Count - 1);
        entries.Add((new string('2', 32), "Assets/Avatar/Variant.prefab", $"%YAML 1.1\n--- !u!1001 &10\nPrefabInstance:\n  m_Modification:\n    m_Modifications:\n    - target: {{fileID: 2, guid: {new string('1', 32)}, type: 3}}\n      propertyPath: m_Materials.Array.data[0]\n      value: \n      objectReference: {{fileID: 2100000, guid: {quest}, type: 2}}\n  m_SourcePrefab: {{fileID: 100100000, guid: {new string('1', 32)}, type: 3}}\n"));
        var variant = Resolve(metadata, renamed);
        Check(variant.Selected.Count == 0 && variant.Issues["low"].Contains("Prefab差分"), "Nested material overrides were ignored during inference");
        Console.WriteLine("PASS parent/nearest/local Materials search, explicit GUID priority, PC/Quest, naming rules, prefab inference and ambiguity refusal");
    }
}
