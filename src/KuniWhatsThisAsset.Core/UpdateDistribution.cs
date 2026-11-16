using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace KuniWhatsThisAsset.Core;

public sealed record DistributionFile(string Path, long Length, string Sha256);
public sealed record DistributionManifest(int Schema, string Version, DistributionFile[] Files);
public static class UpdateDistribution
{
    public const string Executable = "KuniWhatsThisAsset.App.exe";
    public const string ManifestPath = "Libs/distribution.json";
    public static bool Allowed(string relative)
    {
        try { PackageReader.ValidatePath(relative); } catch (InvalidDataException) { return false; }
        if (relative is Executable or "README.txt" or "THIRD-PARTY-NOTICES.md") return true;
        if (!relative.StartsWith("Libs/", StringComparison.Ordinal) || relative.Contains('\\')) return false;
        var parts = relative.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || !Regex.IsMatch(p, @"\A[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))) return false;
        return relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || (relative.StartsWith("Libs/licenses/", StringComparison.Ordinal) && relative.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }
    public static DistributionManifest Parse(byte[] json, Version? expected = null)
    {
        if (json.Length > 256 * 1024) throw new InvalidDataException("更新ファイル一覧が大きすぎます。");
        var manifest = JsonSerializer.Deserialize<DistributionManifest>(json) ?? throw new InvalidDataException("更新ファイル一覧が不正です。");
        if (manifest.Schema != 1 || manifest.Version == null || !Regex.IsMatch(manifest.Version, @"\A\d+\.\d+\.\d+\z") || !Version.TryParse(manifest.Version, out var version) || (expected != null && expected != version)
            || manifest.Files == null || manifest.Files.Length is < 1 or > 512) throw new InvalidDataException("更新形式・バージョンが一致しません。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file == null || file.Path == null || !Allowed(file.Path) || !names.Add(file.Path) || file.Length is < 0 or > 512L * 1024 * 1024 || file.Sha256 == null || !Regex.IsMatch(file.Sha256, @"\A[0-9a-fA-F]{64}\z")) throw new InvalidDataException("更新対象に許可されないファイルがあります。");
            total += file.Length;
        }
        if (total > 1024L * 1024 * 1024 || !names.Contains(Executable)) throw new InvalidDataException("更新内容の容量・本体ファイルが不正です。");
        return manifest;
    }
    public static string FullPath(string directory, string relative)
    {
        if (relative != ManifestPath && !Allowed(relative)) throw new InvalidDataException("更新対象パスが不正です。");
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新対象がフォルダー外です。");
        NoLinks(root.TrimEnd(Path.DirectorySeparatorChar));
        string? cursor = full;
        while (cursor != null && cursor.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { NoLinks(cursor); cursor = Path.GetDirectoryName(cursor); }
        return full;
    }
    private static void NoLinks(string path)
    {
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("リンクされた更新先には書き込めません。"); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    public static bool Matches(string path, DistributionFile file)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Length) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    public static DistributionManifest Verify(string directory, Version? expected = null)
    {
        string manifestPath = FullPath(directory, ManifestPath);
        if (new FileInfo(manifestPath).Length > 256 * 1024) throw new InvalidDataException("更新ファイル一覧が大きすぎます。");
        var manifest = Parse(File.ReadAllBytes(manifestPath), expected);
        foreach (var file in manifest.Files) if (!Matches(FullPath(directory, file.Path), file)) throw new InvalidDataException("更新ファイルの検証に失敗しました: " + file.Path);
        return manifest;
    }
    public static void Extract(string zipPath, string directory, Version expected, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > 1024) throw new InvalidDataException("更新ZIPのファイル数が多すぎます。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested(); string name = entry.FullName;
            if (name.EndsWith('/') && entry.Length == 0)
            {
                if (name != "Libs/" && name != "Libs/licenses/") throw new InvalidDataException("更新ZIPのフォルダーが不正です。");
                continue;
            }
            int mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((mode != 0 && mode != 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || (name != ManifestPath && !Allowed(name)) || !entries.TryAdd(name, entry)) throw new InvalidDataException("更新ZIPのパス・リンク・重複が不正です。");
        }
        if (!entries.TryGetValue(ManifestPath, out var manifestEntry) || manifestEntry.Length > 256 * 1024) throw new InvalidDataException("更新ファイル一覧がありません。");
        byte[] manifestBytes;
        using (var stream = manifestEntry.Open()) { using var memory = new MemoryStream(); Copy(stream, memory, 256 * 1024, token); manifestBytes = memory.ToArray(); }
        var manifest = Parse(manifestBytes, expected);
        if (entries.Count != manifest.Files.Length + 1) throw new InvalidDataException("更新ZIPとファイル一覧が一致しません。");
        Directory.CreateDirectory(directory);
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(file.Path, out var entry) || entry.Length != file.Length) throw new InvalidDataException("更新ZIPの内容が一致しません。");
            string output = FullPath(directory, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using (var source = entry.Open()) using (var target = new FileStream(output, FileMode.CreateNew)) Copy(source, target, file.Length, token);
            if (!Matches(output, file)) throw new InvalidDataException("更新ファイルのハッシュが一致しません。");
        }
        File.WriteAllBytes(FullPath(directory, ManifestPath), manifestBytes);
    }
    private static void Copy(Stream source, Stream target, long max, CancellationToken token)
    {
        var buffer = new byte[65536]; long total = 0; int count;
        while ((count = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); total += count; if (total > max) throw new InvalidDataException("更新ファイルが容量上限を超えました。"); target.Write(buffer, 0, count); }
    }
}
