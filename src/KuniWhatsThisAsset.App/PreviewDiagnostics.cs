using System.Text.RegularExpressions;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public static class PreviewDiagnostics
{
    public static Finding[] FromNotes(string path, IEnumerable<string> notes)
    {
        var values = notes.Distinct().ToArray();
        var output = new List<Finding>();
        foreach (string note in values)
        {
            // Old cache entries can contain a second generic error for the same failed source material.
            var legacy = Regex.Match(note, @"^材質「[0-9a-fA-F]{32}_(.+?)」:", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (legacy.Success && values.Any(n => n.StartsWith("材質「" + legacy.Groups[1].Value + "」:", StringComparison.Ordinal))) continue;
            bool limitation = note.StartsWith("静止形状・", StringComparison.Ordinal) || note.StartsWith("Prefab静止近似：", StringComparison.Ordinal) || note.StartsWith("FBXインスタンスは元モデルの初期形状", StringComparison.Ordinal);
            output.Add(new(limitation ? Severity.Info : Severity.Warning, limitation ? "PREVIEW_LIMITATION" : "PREVIEW_REFERENCE", path, note));
        }
        return output.Distinct().ToArray();
    }
}
