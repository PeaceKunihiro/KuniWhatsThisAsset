using System.Text.RegularExpressions;

namespace KuniWhatsThisAsset.Core;
public sealed record UnityReference(string FileId, string Guid);
public sealed record PrefabObject(int ClassId, string Id, string Text);
public sealed record PrefabRenderer(string Name, UnityReference Mesh, string[] Materials, bool Enabled, string Id = "", string GameObjectId = "");
public sealed record PrefabInstance(UnityReference Source, string Text, string Id = "");
public sealed record PrefabModification(UnityReference Target, string Property, string Value, UnityReference Reference);
public sealed record PrefabAnalysis(PrefabRenderer[] Renderers, PrefabInstance[] Instances, string[] RootIds, bool RootActive, int ScriptCount, PrefabObject[]? Objects = null)
{
    public static string DecodeName(string value)
    {
        if (value.StartsWith('"')) { try { return System.Text.Json.JsonSerializer.Deserialize<string>(value) ?? value; } catch (System.Text.Json.JsonException) { return value; } }
        return value.StartsWith('\'') && value.EndsWith('\'') ? value[1..^1].Replace("''", "'") : value;
    }
    public static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    public static string Scalar(string text, string field) => Matches(text, @"^  " + Regex.Escape(field) + @": ([^\r\n]*)").Cast<Match>().FirstOrDefault()?.Groups[1].Value.Trim() ?? "";
    public static UnityReference Reference(string text, string field)
    {
        string value = Matches(text, @"^  " + Regex.Escape(field) + @": \{([^}]+)\}").Cast<Match>().FirstOrDefault()?.Groups[1].Value ?? "";
        return ParseReference(value);
    }
    public static UnityReference ParseReference(string text) => new(
        Matches(text, @"\bfileID: (-?\d+)").Cast<Match>().FirstOrDefault()?.Groups[1].Value ?? "0",
        Matches(text, @"\bguid: ([0-9a-fA-F]{32})").Cast<Match>().FirstOrDefault()?.Groups[1].Value.ToLowerInvariant() ?? "");
    public static PrefabAnalysis Parse(string text, CancellationToken token = default)
    {
        if (text.Length > 16 * 1024 * 1024 || !text.StartsWith("%YAML", StringComparison.Ordinal)) throw new InvalidDataException("テキスト形式のUnity Prefab（16 MiB以内）のみ表示できます。");
        var documents = Matches(text, @"^--- !u!(\d+) &(-?\d+)[^\r\n]*\r?\n([\s\S]*?)(?=^--- |\z)")
            .Select(m => new PrefabObject(int.TryParse(m.Groups[1].Value, out int id) ? id : -1, m.Groups[2].Value, m.Groups[3].Value)).ToArray();
        if (documents.Length > 50000 || documents.Select(d => d.Id).Distinct().Count() != documents.Length) throw new InvalidDataException("Prefabのオブジェクト数またはIDが不正です。");
        var objects = documents.Where(d => d.ClassId == 1).ToDictionary(d => d.Id);
        var transforms = documents.Where(d => d.ClassId is 4 or 224 && Reference(d.Text, "m_GameObject").FileId != "0").ToDictionary(d => Reference(d.Text, "m_GameObject").FileId);
        var byTransform = transforms.Values.ToDictionary(d => d.Id);
        var filters = documents.Where(d => d.ClassId == 33).GroupBy(d => Reference(d.Text, "m_GameObject").FileId).ToDictionary(g => g.Key, g => g.First());
        bool Active(string go)
        {
            var visited = new HashSet<string>();
            while (go != "0")
            {
                if (!visited.Add(go) || visited.Count > 128) throw new InvalidDataException("Prefab階層が循環しているか深すぎます。");
                if (objects.TryGetValue(go, out var obj) && Scalar(obj.Text, "m_IsActive") == "0") return false;
                if (!transforms.TryGetValue(go, out var transform) || !byTransform.TryGetValue(Reference(transform.Text, "m_Father").FileId, out var parent)) break;
                go = Reference(parent.Text, "m_GameObject").FileId;
            }
            return true;
        }
        var renderers = new List<PrefabRenderer>();
        foreach (var renderer in documents.Where(d => d.ClassId is 23 or 137))
        {
            token.ThrowIfCancellationRequested();
            if (renderers.Count >= 10000) throw new InvalidDataException("PrefabのRendererが多すぎます。");
            string go = Reference(renderer.Text, "m_GameObject").FileId;
            var mesh = renderer.ClassId == 137 ? Reference(renderer.Text, "m_Mesh") : Reference(filters.GetValueOrDefault(go)?.Text ?? "", "m_Mesh");
            string materials = Matches(renderer.Text, @"^  m_Materials:\s*\r?\n((?:  - [^\r\n]*\r?\n)*)").Cast<Match>().FirstOrDefault()?.Groups[1].Value ?? "";
            string name = objects.TryGetValue(go, out var obj) ? DecodeName(Scalar(obj.Text, "m_Name")) : "";
            renderers.Add(new(name, mesh, Matches(materials, @"^  - \{([^}]+)\}").Select(m => ParseReference(m.Groups[1].Value).Guid).ToArray(), Active(go) && Scalar(renderer.Text, "m_Enabled") != "0", renderer.Id, go));
        }
        var roots = transforms.Where(t => Reference(t.Value.Text, "m_Father").FileId == "0").Select(t => t.Key).ToArray();
        return new(renderers.ToArray(), documents.Where(d => d.ClassId == 1001).Select(d => new PrefabInstance(Reference(d.Text, "m_SourcePrefab"), d.Text, d.Id)).Where(i => i.Source.Guid.Length > 0).ToArray(), roots, roots.Length == 0 || roots.Any(Active), documents.Count(d => d.ClassId == 114), documents);
    }
    public static PrefabModification[] Modifications(PrefabInstance instance) => Matches(instance.Text,
        @"^[ \t]*- target: \{([^}]+)\}[ \t]*\r?\n[ \t]+propertyPath: ([^\r\n]+)\r?\n[ \t]+value:([^\r\n]*)\r?\n[ \t]+objectReference: \{([^}]+)\}")
        .Select(m => new PrefabModification(ParseReference(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim(), ParseReference(m.Groups[4].Value))).ToArray();
    public static bool? RootActiveOverride(PrefabInstance instance, string[] rootIds)
    {
        bool? active = null;
        foreach (Match m in Matches(instance.Text, @"^    - target: \{([^}]+)\}\s*\r?\n      propertyPath: m_IsActive\r?\n      value: ([01])"))
            if (rootIds.Contains(ParseReference(m.Groups[1].Value).FileId)) active = m.Groups[2].Value == "1";
        return active;
    }
}
