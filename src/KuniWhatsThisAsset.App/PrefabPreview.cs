using System.IO;
using System.Text;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public sealed record PrefabPreview(ModelPreviewData Model, string MaterialMetadata);
public static class PrefabPreviewBuilder
{
    public static async Task<PrefabPreview> BuildAsync(PackageReader reader, PackageReport report, PackageItem item, CancellationToken token)
    {
        var meshes = new List<PreviewMesh>(); var notes = new HashSet<string>();
        var models = new Dictionary<string, (PackageItem Item, ModelPreviewData Data, string Meta)>(StringComparer.OrdinalIgnoreCase);
        var bindings = new Dictionary<string, string>(); var path = new HashSet<string>(); int visited = 0, unmatched = 0;
        var referencedMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task<(PackageItem Item, ModelPreviewData Data, string Meta)?> Model(string guid)
        {
            if (guid.Length != 32) { notes.Add("継承Rendererや内蔵Meshなど、モデル参照を直接解決できない項目があります。"); return null; }
            if (models.TryGetValue(guid, out var cached)) return cached;
            var source = report.Items.FirstOrDefault(i => i.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase) && i.Format is ".fbx" or ".obj");
            if (source == null) { notes.Add("パッケージ内に存在しないモデル参照があります。"); return null; }
            if (models.Count >= 16) throw new InvalidDataException("Prefab内モデル数の上限（16）を超えています。");
            var bytes = await Task.Run(() => reader.ReadAssetBytes(report.SourcePath, guid, 256 * 1024 * 1024, token), token);
            var data = await ModelWorker.ImportAsync(new(source.Path, bytes, new()), token);
            string meta = TextDecoder.Decode(await Task.Run(() => reader.ReadAssetMetaBytes(report.SourcePath, guid, token), token), false, out _);
            var result = (source, data, meta); models[guid] = result; return result;
        }
        void Add(PreviewMesh mesh, string sourceGuid, string materialGuid, (PackageItem Item, ModelPreviewData Data, string Meta) source)
        {
            token.ThrowIfCancellationRequested();
            if (materialGuid.Length == 0)
            {
                var remap = PrefabAnalysis.Matches(source.Meta, @"^\s+name: ([^\r\n]+)\r?\n\s+second: \{fileID: 2100000, guid: ([0-9a-fA-F]{32})").FirstOrDefault(m => m.Groups[1].Value.Trim() == mesh.MaterialName);
                materialGuid = remap?.Groups[2].Value ?? "";
                if (materialGuid.Length == 0)
                {
                    string folder = source.Item.Path[..Math.Max(0, source.Item.Path.LastIndexOf('/'))] + "/";
                    var candidates = report.Items.Where(i => i.Format == ".mat" && i.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(i.Path) == mesh.MaterialName).ToArray();
                    if (candidates.Length == 1) materialGuid = candidates[0].Guid;
                }
            }
            string key = materialGuid.Length == 32 ? "material_" + materialGuid : sourceGuid + "_" + mesh.MaterialName;
            if (materialGuid.Length == 32) bindings[key] = materialGuid;
            meshes.Add(mesh with { MaterialName = key });
            if (meshes.Count > 10000 || meshes.Sum(m => (long)m.Positions.Length / 3) > 2_000_000 || meshes.Sum(m => (long)m.Indices.Length / 3) > 2_000_000) throw new InvalidDataException("Prefabの合計形状がプレビュー上限を超えています。");
        }
        async Task Visit(PackageItem prefab, int depth, PrefabInstance? instance = null)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 8 || ++visited > 32 || !path.Add(prefab.Guid)) throw new InvalidDataException("Prefab参照が循環しているか、階層・件数上限を超えています。");
            try
            {
                var bytes = await Task.Run(() => reader.ReadAssetBytes(report.SourcePath, prefab.Guid, 16 * 1024 * 1024, token), token);
                var parsed = await Task.Run(() => PrefabAnalysis.Parse(TextDecoder.Decode(bytes, false, out _), token), token);
                if (!(instance != null ? PrefabAnalysis.RootActiveOverride(instance, parsed.RootIds) ?? parsed.RootActive : parsed.RootActive)) return;
                foreach (var renderer in parsed.Renderers.Where(r => r.Enabled))
                {
                    foreach (string guid in renderer.Materials.Where(g => g.Length == 32)) referencedMaterials.Add(guid);
                    var source = await Model(renderer.Mesh.Guid); if (source == null) continue;
                    string name = PrefabAnalysis.Matches(source.Value.Meta, @"^    " + System.Text.RegularExpressions.Regex.Escape(renderer.Mesh.FileId) + @": ([^\r\n]+)").FirstOrDefault()?.Groups[1].Value.Trim() ?? renderer.Name;
                    var selected = source.Value.Data.Meshes.Where(m => m.NodeName == name).ToArray();
                    if (selected.Length == 0) { unmatched++; continue; }
                    for (int i = 0; i < selected.Length; i++) Add(selected[i], renderer.Mesh.Guid, i < renderer.Materials.Length ? renderer.Materials[i] : "", source.Value);
                }
                foreach (var nested in parsed.Instances)
                {
                    var dependency = report.Items.FirstOrDefault(i => i.Guid.Equals(nested.Source.Guid, StringComparison.OrdinalIgnoreCase));
                    if (dependency?.Format == ".prefab") await Visit(dependency, depth + 1, nested);
                    else if (dependency?.Format is ".fbx" or ".obj")
                    {
                        var source = await Model(dependency.Guid); if (source == null) continue;
                        foreach (var mesh in source.Value.Data.Meshes) Add(mesh, dependency.Guid, "", source.Value);
                        notes.Add("FBXインスタンスは元モデルの初期形状で表示（Prefab差分は未反映）。");
                    }
                    else notes.Add("未解決または未対応のPrefab参照があります。");
                }
            }
            finally { path.Remove(prefab.Guid); }
        }
        await Visit(item, 0);
        if (meshes.Count == 0) throw new InvalidDataException("表示可能なモデル参照がありません（非表示・参照不足・未対応の内蔵Mesh等）。");
        if (unmatched > 0) notes.Add($"メッシュ名を解決できないRenderer {unmatched}件を省略しました。");
        notes.Add("Prefab静止近似：有効Renderer・参照材質を使用。配置変更・Bone姿勢・BlendShape・Scriptは未反映。");
        var stats = new ModelStatistics(meshes.Sum(m => (long)m.Positions.Length / 3), meshes.Sum(m => (long)m.Indices.Length / 3), meshes.Count, Math.Max(referencedMaterials.Concat(bindings.Values).Distinct(StringComparer.OrdinalIgnoreCase).Count(), meshes.Select(m => m.MaterialName).Distinct().Count()), models.Values.Sum(m => m.Data.Statistics.BoneCount), models.Values.Any(m => m.Data.Statistics.HasSkinning));
        var data = new ModelPreviewData(stats, meshes.ToArray(), notes.ToArray()); data.Validate();
        var metadata = new StringBuilder("ModelImporter:\n  externalObjects:\n");
        foreach (var (name, guid) in bindings) metadata.Append($"  - first:\n      name: {name}\n    second: {{fileID: 2100000, guid: {guid}, type: 2}}\n");
        return new(data, metadata.ToString());
    }
}
