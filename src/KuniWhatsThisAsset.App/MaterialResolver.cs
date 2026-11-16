using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;

public sealed record MaterialResolution(Dictionary<string, PackageItem> Selected, Dictionary<string, string> Issues);

// Resolves package-local Unity material assets. No native texture path is opened.
public sealed class MaterialResolver(PackageReader reader, PackageReport report, CancellationToken token)
{
    private readonly PackageItem[] materials = report.Items.Where(i => i.Format == ".mat").ToArray();
    private Dictionary<string, PrefabAnalysis>? prefabs;
    private string? prefabIssue;
    private static string Name(string text)
    {
        text = text.Trim();
        if (text.StartsWith('"')) { try { text = JsonSerializer.Deserialize<string>(text) ?? text; } catch (JsonException) { } }
        else if (text.StartsWith('\'') && text.EndsWith('\'')) text = text[1..^1].Replace("''", "'");
        return text.Normalize(NormalizationForm.FormC);
    }
    private static string DirectoryOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    private static string FileName(string path) => Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
    public static string MeshNodeName(string metadata, PrefabRenderer renderer) => PrefabAnalysis.Matches(metadata,
        @"^    " + Regex.Escape(renderer.Mesh.FileId) + @": ([^\r\n]+)").FirstOrDefault() is { } match ? Name(match.Groups[1].Value) : renderer.Name;

    public MaterialResolution Resolve(PackageItem modelItem, string metadata, ModelPreviewData model, bool allowPrefabInference = true)
    {
        var selected = new Dictionary<string, PackageItem>(StringComparer.Ordinal);
        var issues = new Dictionary<string, string>(StringComparer.Ordinal);
        var remaps = PrefabAnalysis.Matches(metadata, @"^[ \t]+name: ([^\r\n]+)\r?\n[ \t]+second: \{[^}]*?guid: ([0-9a-fA-F]{32})")
            .GroupBy(m => Name(m.Groups[1].Value)).ToDictionary(g => g.Key, g => g.Select(m => m.Groups[2].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        int Setting(string key, int fallback) => int.TryParse(PrefabAnalysis.Matches(metadata, @"^[ \t]+" + Regex.Escape(key) + @": (\d+)\s*$").FirstOrDefault()?.Groups[1].Value, out int value) ? value : fallback;
        int search = Setting("materialSearch", 1), naming = Setting("materialName", 1);
        foreach (var group in model.Meshes.GroupBy(m => m.MaterialName, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested(); string original = group.Key, name = Name(original);
            void Issue(string reason) => issues[original] = $"材質「{original}」: {reason}（基本色で表示）。";
            if (remaps.TryGetValue(name, out var explicitGuids))
            {
                if (explicitGuids.Length != 1) Issue("明示割り当てが複数あり特定できません");
                else if (materials.FirstOrDefault(i => i.Guid.Equals(explicitGuids[0], StringComparison.OrdinalIgnoreCase)) is { } item) selected[original] = item;
                else Issue("明示参照先の.matがパッケージ内にありません");
                continue;
            }
            var names = naming switch
            {
                0 => group.Select(m => Name(FileName(m.TexturePath))).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                2 => [Name(FileName(modelItem.Path)) + "-" + name],
                _ => new[] { name }
            };
            if (names.Length == 0) names = [name];
            // Texture-name importers sometimes leave no usable filename; keep the legacy native-name fallback.
            var nameSets = naming == 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase) ? new[] { names, new[] { name } } : new[] { names };
            PackageItem[] candidates = [];
            foreach (var nameSet in nameSets)
            {
                var matching = materials.Where(i => nameSet.Contains(Name(FileName(i.Path)), StringComparer.OrdinalIgnoreCase)).ToArray();
                string directory = DirectoryOf(modelItem.Path);
                if (search == 2) candidates = matching;
                else
                {
                    while (directory.Length > 0)
                    {
                        var folders = new[] { directory + "/Materials", directory + "/Material", directory + "/Mat" };
                        candidates = matching.Where(i => folders.Contains(DirectoryOf(i.Path), StringComparer.OrdinalIgnoreCase)).ToArray();
                        if (candidates.Length > 0 || search != 1 || directory.Equals("Assets", StringComparison.OrdinalIgnoreCase)) break;
                        directory = DirectoryOf(directory);
                    }
                    // Retain support for established packages placing a unique material within the model directory tree.
                    if (candidates.Length == 0)
                    {
                        string folder = DirectoryOf(modelItem.Path) + "/";
                        candidates = matching.Where(i => i.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).ToArray();
                    }
                }
                if (candidates.Length > 0) break;
            }
            if (candidates.Length == 1) { selected[original] = candidates[0]; continue; }
            if (candidates.Length > 1) { Issue("同じ探索範囲に候補が複数あります"); continue; }
            if (allowPrefabInference && modelItem.Format is (".fbx" or ".obj"))
            {
                var inferred = FromPrefabs(modelItem, metadata, model, original, out string? inferenceIssue);
                if (inferred.Length == 1 && materials.FirstOrDefault(i => i.Guid.Equals(inferred[0], StringComparison.OrdinalIgnoreCase)) is { } item) { selected[original] = item; continue; }
                if (inferred.Length > 1) { Issue("Prefab間で割り当てが異なります。表示するPrefabを選択してください"); continue; }
                if (inferred.Length == 1) { Issue("Prefabの参照先.matがパッケージ内にありません"); continue; }
                if (inferenceIssue != null) { Issue(inferenceIssue); continue; }
            }
            Issue("対応する.matを解決できません");
        }
        return new(selected, issues);
    }
    private string[] FromPrefabs(PackageItem source, string metadata, ModelPreviewData model, string name, out string? issue)
    {
        issue = null;
        if (prefabs == null)
        {
            prefabs = new(StringComparer.OrdinalIgnoreCase);
            var files = report.Items.Where(i => i.Format == ".prefab").ToArray();
            // Inspect every prefab or decline inference; truncating the set could hide conflicting variants.
            if (files.Length > 32) { issue = prefabIssue = "Prefab補完の解析件数上限を超えています"; return []; }
            try
            {
                var bytes = reader.ReadAssetSet(report.SourcePath, files.Select(i => i.Guid).ToArray(), maxFileBytes: 16 * 1024 * 1024, maxTotalBytes: 64L * 1024 * 1024, cancellationToken: token);
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!bytes.TryGetValue(file.Guid, out var content)) throw new InvalidDataException("Prefab not found");
                    prefabs[file.Guid] = PrefabAnalysis.Parse(TextDecoder.Decode(content, false, out _), token);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or RegexMatchTimeoutException)
            { prefabs.Clear(); prefabIssue = "一部のPrefabを解析できないため割り当て補完を省略しました"; }
        }
        if (prefabIssue != null) { issue = prefabIssue; return []; }
        bool ReferencesModel(string guid, HashSet<string> visited)
        {
            if (guid.Equals(source.Guid, StringComparison.OrdinalIgnoreCase)) return true;
            if (!prefabs.TryGetValue(guid, out var prefab)) return false;
            if (visited.Count >= 8 || !visited.Add(guid)) return true; // An unknown cyclic/deep branch cannot establish uniqueness.
            return prefab.Renderers.Any(r => r.Mesh.Guid.Equals(source.Guid, StringComparison.OrdinalIgnoreCase)) || prefab.Instances.Any(i => ReferencesModel(i.Source.Guid, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase)));
        }
        foreach (var instance in prefabs.Values.SelectMany(p => p.Instances))
            if (PrefabAnalysis.Matches(instance.Text, @"^[ \t]+propertyPath: m_Materials(?:\.|\s|$)").Count > 0 && ReferencesModel(instance.Source.Guid, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            { issue = "Prefab差分に材質変更があり一意性を確認できません。表示するPrefabを選択してください"; return []; }
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefab in prefabs.Values)
            foreach (var renderer in prefab.Renderers.Where(r => r.Mesh.Guid.Equals(source.Guid, StringComparison.OrdinalIgnoreCase)))
            {
                string node = MeshNodeName(metadata, renderer);
                var meshes = model.Meshes.Where(m => m.NodeName == node).ToArray();
                for (int i = 0; i < meshes.Length; i++)
                    if (meshes[i].MaterialName == name && i < renderer.Materials.Length && renderer.Materials[i].Length == 32) candidates.Add(renderer.Materials[i]);
            }
        return candidates.ToArray();
    }
}
