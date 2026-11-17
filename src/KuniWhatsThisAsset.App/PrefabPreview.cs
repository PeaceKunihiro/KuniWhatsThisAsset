using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public sealed record PrefabPreview(ModelPreviewData Model, string MaterialMetadata);
public static class PrefabPreviewBuilder
{
    private sealed record Address(string Guid, string Id, bool Renderer, string State = "");
    private sealed class Part(string source, PreviewMesh[] meshes)
    {
        public string Source = source;
        public PreviewMesh[] Meshes = meshes;
        public string?[] Materials = new string?[meshes.Length];
        public bool Enabled = true;
        public List<Address> Addresses = [];
        public Dictionary<string, bool> Active = [];
        public bool Visible => Enabled && Active.Values.All(v => v);
        public void Object(string guid, string id, bool active)
        {
            string key = guid + ":" + id;
            Addresses.Add(new(guid, id, false, key)); Active[key] = active;
        }
    }
    public static string InstanceId(string instance, string source) => long.TryParse(instance, out long a) && long.TryParse(source, out long b)
        ? ((a ^ b) & long.MaxValue).ToString(CultureInfo.InvariantCulture) : "";

    public static async Task<PrefabPreview> BuildAsync(PackageReader reader, PackageReport report, PackageItem item, CancellationToken token)
    {
        var items = report.Items.GroupBy(i => i.Guid, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, (ModelPreviewData Data, string Meta, MaterialResolution Resolution)>();
        var prefabs = new Dictionary<string, PrefabAnalysis>();
        var path = new HashSet<string>(); var notes = new HashSet<string>();
        int visited = 0; long prefabBytes = 0;
        // Read each dependency layer as one validated archive pass; unrelated Prefabs are never parsed.
        var pending = new HashSet<string> { item.Guid }; var modelGuids = new HashSet<string>();
        while (pending.Count > 0)
        {
            if (prefabs.Count + pending.Count > 32) throw new InvalidDataException("依存Prefab数の上限（32）を超えています。");
            var contents = await Task.Run(() => reader.ReadAssetSet(report.SourcePath, pending.ToArray(), maxFileBytes: 16 * 1024 * 1024, maxTotalBytes: 64L * 1024 * 1024 - prefabBytes, cancellationToken: token), token);
            var next = new HashSet<string>();
            foreach (string guid in pending)
            {
                if (!contents.TryGetValue(guid, out var bytes)) throw new InvalidDataException("依存Prefabが見つかりません。");
                prefabBytes += bytes.Length;
                var parsed = await Task.Run(() => PrefabAnalysis.Parse(TextDecoder.Decode(bytes, false, out _), token), token); prefabs[guid] = parsed;
                foreach (string dependency in parsed.Instances.Select(i => i.Source.Guid).Concat(parsed.Renderers.Select(r => r.Mesh.Guid)).Distinct())
                {
                    if (!items.TryGetValue(dependency, out var source)) continue;
                    if (source.Format == ".prefab" && !prefabs.ContainsKey(dependency)) next.Add(dependency);
                    else if (source.Format is ".fbx" or ".obj") modelGuids.Add(dependency);
                }
            }
            next.ExceptWith(prefabs.Keys); pending = next;
        }
        if (modelGuids.Count > 16) throw new InvalidDataException("Prefab内モデル数の上限（16）を超えています。");
        var modelBytes = await Task.Run(() => reader.ReadAssetSet(report.SourcePath, modelGuids.ToArray(), maxFileBytes: 256 * 1024 * 1024, maxTotalBytes: 512L * 1024 * 1024, cancellationToken: token), token);
        var modelMeta = await Task.Run(() => reader.ReadAssetSet(report.SourcePath, modelGuids.ToArray(), metadata: true, maxFileBytes: 2 * 1024 * 1024, maxTotalBytes: 32L * 1024 * 1024, cancellationToken: token), token);
        var resolver = new MaterialResolver(reader, report, token);
        async Task<(ModelPreviewData Data, string Meta, MaterialResolution Resolution)?> Model(string guid)
        {
            if (models.TryGetValue(guid, out var cached)) return cached;
            if (!items.TryGetValue(guid, out var source) || source.Format is not (".fbx" or ".obj")) { notes.Add("パッケージ内に存在しないモデル参照があります。"); return null; }
            if (models.Count >= 16) throw new InvalidDataException("Prefab内モデル数の上限（16）を超えています。");
            if (!modelBytes.Remove(guid, out var bytes)) throw new InvalidDataException("依存モデルが見つかりません。");
            var data = await ModelWorker.ImportAsync(new(source.Path, bytes, new()), token);
            string meta = TextDecoder.Decode(modelMeta.GetValueOrDefault(guid) ?? [], false, out _);
            data = ModelMeasurements.ApplyImportSettings(data, meta);
            var resolution = await Task.Run(() => resolver.Resolve(source, meta, data, allowPrefabInference: false), token);
            return models[guid] = (data, meta, resolution);
        }
        var hierarchy = prefabs.ToDictionary(p => p.Key, p =>
        {
            var objects = p.Value.Objects ?? [];
            var transforms = objects.Where(o => o.ClassId is 4 or 224).ToDictionary(o => o.Id);
            var gameObjects = objects.Where(o => o.ClassId == 1).ToDictionary(o => o.Id, o => PrefabAnalysis.Scalar(o.Text, "m_IsActive") != "0");
            var parents = new Dictionary<string, string>(); var transformGo = new Dictionary<string, string>();
            foreach (var transform in transforms.Values) transformGo[transform.Id] = PrefabAnalysis.Reference(transform.Text, "m_GameObject").FileId;
            foreach (var transform in transforms.Values)
            {
                string go = transformGo[transform.Id];
                if (go != "0") parents[go] = transformGo.GetValueOrDefault(PrefabAnalysis.Reference(transform.Text, "m_Father").FileId, "0");
            }
            var stripped = objects.Select(o => (Object: o, Instance: PrefabAnalysis.Reference(o.Text, "m_PrefabInstance").FileId, Source: PrefabAnalysis.Reference(o.Text, "m_CorrespondingSourceObject")))
                .Where(o => o.Instance != "0").GroupBy(o => o.Instance).ToDictionary(g => g.Key, g => g.ToArray());
            return (Active: gameObjects, Parents: parents, TransformGo: transformGo, Stripped: stripped);
        });
        void Parents(Part part, string guid, string go)
        {
            var index = hierarchy[guid]; var seen = new HashSet<string>();
            while (go != "0" && go.Length > 0)
            {
                if (!seen.Add(go) || seen.Count > 128) throw new InvalidDataException("Prefab階層が循環しているか深すぎます。");
                part.Object(guid, go, index.Active.GetValueOrDefault(go, true));
                go = index.Parents.GetValueOrDefault(go, "0");
            }
        }
        void Apply(List<Part> parts, PrefabInstance instance)
        {
            foreach (var change in PrefabAnalysis.Modifications(instance))
            {
                token.ThrowIfCancellationRequested();
                bool material = change.Property.StartsWith("m_Materials.", StringComparison.Ordinal);
                if (!material && change.Property is not ("m_Enabled" or "m_IsActive")) continue;
                var matches = parts.Where(p => p.Addresses.Any(a => a.Guid.Equals(change.Target.Guid, StringComparison.OrdinalIgnoreCase) && a.Id == change.Target.FileId && a.Renderer == (change.Property != "m_IsActive"))).ToArray();
                if (matches.Length == 0 && change.Property == "m_IsActive" && hierarchy.TryGetValue(change.Target.Guid, out var targetHierarchy) && targetHierarchy.Active.ContainsKey(change.Target.FileId)) continue; // No rendered geometry below this known object (e.g. particles).
                if (matches.Length == 0 || (material && matches.Length != 1)) { notes.Add($"対象IDを一意に解決できないPrefab差分：{change.Target.Guid}/{change.Target.FileId} {(material ? "m_Materials" : change.Property)}。"); continue; }
                foreach (var part in matches)
                {
                    if (change.Property == "m_Enabled" && change.Value is "0" or "1") part.Enabled = change.Value == "1";
                    else if (change.Property == "m_IsActive" && change.Value is "0" or "1")
                        foreach (var address in part.Addresses.Where(a => !a.Renderer && a.Guid.Equals(change.Target.Guid, StringComparison.OrdinalIgnoreCase) && a.Id == change.Target.FileId)) part.Active[address.State] = change.Value == "1";
                    else if (change.Property == "m_Materials.Array.size" && int.TryParse(change.Value, out int count) && count >= 0)
                    { for (int i = count; i < part.Materials.Length; i++) part.Materials[i] = ""; }
                    else if (Regex.Match(change.Property, @"^m_Materials\.Array\.data\[(\d+)\]$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) is { Success: true } slot && int.TryParse(slot.Groups[1].Value, out int index))
                    {
                        if (index < part.Materials.Length) part.Materials[index] = change.Reference.FileId == "0" ? "" : change.Reference.Guid;
                        else notes.Add("モデルの材質スロット範囲外のPrefab差分があります。");
                    }
                }
            }
        }
        async Task<List<Part>> Visit(PackageItem prefab, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 8 || ++visited > 64 || !path.Add(prefab.Guid)) throw new InvalidDataException("Prefab参照が循環しているか、階層・件数上限を超えています。");
            try
            {
                var parsed = prefabs[prefab.Guid];
                var parts = new List<Part>();
                foreach (var renderer in parsed.Renderers.Where(r => r.Mesh.Guid.Length == 32))
                {
                    var source = await Model(renderer.Mesh.Guid); if (source == null) continue;
                    string name = MaterialResolver.MeshNodeName(source.Value.Meta, renderer);
                    var selected = source.Value.Data.Meshes.Where(m => UnityModelIds.Mesh(m) == renderer.Mesh.FileId).ToArray();
                    if (selected.Length == 0) selected = source.Value.Data.Meshes.Where(m => m.NodeName == name).ToArray();
                    if (selected.Length == 0 || selected.Select(m => m.NodePath).Distinct().Count() != 1) { notes.Add($"メッシュID・名前を一意に解決できないRendererを省略：{renderer.Name} ({renderer.Mesh.Guid}/{renderer.Mesh.FileId})。"); continue; }
                    var part = new Part(renderer.Mesh.Guid, selected);
                    var obj = (parsed.Objects ?? []).First(o => o.Id == renderer.Id);
                    part.Enabled = PrefabAnalysis.Scalar(obj.Text, "m_Enabled") != "0";
                    part.Addresses.Add(new(prefab.Guid, renderer.Id, true)); Parents(part, prefab.Guid, renderer.GameObjectId);
                    for (int i = 0; i < selected.Length; i++) part.Materials[i] = i < renderer.Materials.Length ? renderer.Materials[i] : "";
                    parts.Add(part);
                }
                foreach (var nested in parsed.Instances)
                {
                    if (!items.TryGetValue(nested.Source.Guid, out var dependency)) { notes.Add("パッケージ内に存在しないPrefab参照があります。"); continue; }
                    List<Part> children;
                    if (dependency.Format == ".prefab") children = await Visit(dependency, depth + 1);
                    else if (dependency.Format is ".fbx" or ".obj")
                    {
                        var source = await Model(dependency.Guid); if (source == null) continue;
                        children = [];
                        foreach (var group in source.Value.Data.Meshes.GroupBy(m => m.NodePath))
                        {
                            var part = new Part(dependency.Guid, group.ToArray());
                            var mesh = part.Meshes[0] with { Skinned = part.Meshes.Any(m => m.Skinned) };
                            part.Addresses.Add(new(dependency.Guid, UnityModelIds.Renderer(mesh), true));
                            string nodePath = mesh.NodePath;
                            while (true)
                            {
                                part.Object(dependency.Guid, UnityModelIds.GameObject(mesh with { NodePath = nodePath }), true);
                                if (nodePath.Length == 0) break;
                                int slash = nodePath.LastIndexOf('/'); nodePath = slash < 0 ? "" : nodePath[..slash];
                            }
                            // Legacy ModelImporter identifiers are supplied explicitly by the model's meta file.
                            foreach (var entry in PrefabAnalysis.Matches(source.Value.Meta, @"^    (\d+): ([^\r\n]+)").Where(m => m.Groups[2].Value.Trim() == mesh.NodeName))
                            {
                                string id = entry.Groups[1].Value;
                                if (id.StartsWith("137", StringComparison.Ordinal) || id.StartsWith("23", StringComparison.Ordinal)) part.Addresses.Add(new(dependency.Guid, id, true));
                                else if (id.StartsWith("1", StringComparison.Ordinal)) part.Object(dependency.Guid, id, true);
                            }
                            children.Add(part);
                        }
                    }
                    else { notes.Add("未対応のPrefab参照形式があります。"); continue; }
                    Apply(children, nested);
                    foreach (var part in children)
                    {
                        foreach (var address in part.Addresses.Where(a => a.Guid.Equals(dependency.Guid, StringComparison.OrdinalIgnoreCase)).ToArray())
                        {
                            string id = InstanceId(nested.Id, address.Id);
                            if (id.Length > 0) part.Addresses.Add(address with { Guid = prefab.Guid, Id = id });
                        }
                        foreach (var stripped in hierarchy[prefab.Guid].Stripped.GetValueOrDefault(nested.Id) ?? [])
                        {
                            var original = stripped.Source;
                            foreach (var address in part.Addresses.Where(a => a.Guid.Equals(original.Guid, StringComparison.OrdinalIgnoreCase) && a.Id == original.FileId).ToArray())
                                part.Addresses.Add(address with { Guid = prefab.Guid, Id = stripped.Object.Id });
                        }
                        string parent = PrefabAnalysis.ParseReference(PrefabAnalysis.Matches(nested.Text, @"^    m_TransformParent: \{([^}]+)\}").FirstOrDefault()?.Groups[1].Value ?? "").FileId;
                        string parentGo = hierarchy[prefab.Guid].TransformGo.GetValueOrDefault(parent, "0");
                        Parents(part, prefab.Guid, parentGo);
                    }
                    parts.AddRange(children);
                    if (parts.Count > 10000) throw new InvalidDataException("PrefabのRenderer数が多すぎます。");
                }
                return parts;
            }
            finally { path.Remove(prefab.Guid); }
        }
        var final = await Visit(item, 0); var meshes = new List<PreviewMesh>(); var bindings = new Dictionary<string, string>();
        long vertices = 0, triangles = 0;
        foreach (var part in final.Where(p => p.Visible))
        for (int i = 0; i < part.Meshes.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var mesh = part.Meshes[i]; string? material = part.Materials[i];
            if (material == null)
            {
                var resolution = models[part.Source].Resolution;
                if (resolution.Selected.TryGetValue(mesh.MaterialName, out var mat)) material = mat.Guid;
                else if (resolution.Issues.TryGetValue(mesh.MaterialName, out string? issue)) notes.Add(issue);
            }
            string key = material?.Length == 32 ? "material_" + material : part.Source + "_" + mesh.MaterialName;
            if (material?.Length == 32) bindings[key] = material;
            meshes.Add(mesh with { MaterialName = key });
            vertices += mesh.Positions.Length / 3; triangles += mesh.Indices.Length / 3;
            if (meshes.Count > 10000 || vertices > 2_000_000 || triangles > 2_000_000) throw new InvalidDataException("Prefabの合計形状がプレビュー上限を超えています。");
        }
        if (meshes.Count == 0) throw new InvalidDataException("表示可能なモデル参照がありません（非表示・参照不足・未対応の内蔵Mesh等）。");
        notes.Add("Prefab静止近似：材質・表示状態の差分を反映。配置変更・Bone姿勢・BlendShape・Scriptは未反映。");
        var stats = new ModelStatistics(vertices, triangles, meshes.Count, meshes.Select(m => m.MaterialName).Distinct().Count(), models.Values.Sum(m => m.Data.Statistics.BoneCount), models.Values.Any(m => m.Data.Statistics.HasSkinning));
        var data = new ModelPreviewData(stats, meshes.ToArray(), notes.Take(32).ToArray()) { MeasurementNote = "Prefab静止近似の外形。Prefabの配置・Scale差分、Bone姿勢、BlendShapeは未反映。" }; data.Validate();
        var metadata = new StringBuilder("ModelImporter:\n  externalObjects:\n");
        foreach (var (name, guid) in bindings) metadata.Append($"  - first:\n      name: {name}\n    second: {{fileID: 2100000, guid: {guid}, type: 2}}\n");
        return new(data, metadata.ToString());
    }
}
