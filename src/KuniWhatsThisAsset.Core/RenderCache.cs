using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KuniWhatsThisAsset.Core;
public sealed record CachedRender(ModelStatistics Statistics, byte[][] Pictures, string[] Notes);
public sealed class RenderCache(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public static readonly string[] Views = ["front", "side", "top"];
    private sealed record Manifest(ModelStatistics Statistics, string[] Hashes, string[] Notes);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static async Task<string> KeyAsync(string packagePath, string assetGuid, CancellationToken token)
    {
        await using var stream = File.OpenRead(packagePath);
        var digest = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Hash(Encoding.UTF8.GetBytes("kwta-render-v0.9.0-1:" + Convert.ToHexString(digest) + ":" + assetGuid));
    }
    private string PathFor(string key, string suffix)
    {
        if (!Regex.IsMatch(key, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid cache key");
        return Path.Combine(DirectoryPath, key + suffix);
    }
    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, "KWTA.RenderCache." + Hash(Encoding.UTF8.GetBytes(DirectoryPath.ToUpperInvariant())));
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("キャッシュを使用中です。");
        try { return action(); } finally { mutex.ReleaseMutex(); }
    }
    public CachedRender? Read(string key) => Locked(() =>
    {
        string path = PathFor(key, ".json");
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) return null;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
            if (manifest == null || manifest.Statistics == null || manifest.Hashes == null || manifest.Hashes.Length != 3 || manifest.Notes == null || manifest.Notes.Length > 100 || manifest.Notes.Any(n => n == null || n.Length > 4096)) return null;
            var stats = manifest.Statistics;
            if (stats.TriangleCount is < 1 or > 2_000_000 || stats.VertexCount is < 1 or > 2_000_000 || stats.MeshCount is < 1 or > 10000 || stats.MaterialCount is < 1 or > 10000 || stats.BoneCount is < 0 or > 2_000_000) return null;
            var pictures = Views.Select(v => PathFor(key, "." + v + ".png")).Select(p =>
            {
                if (new FileInfo(p).Length > 8 * 1024 * 1024) throw new InvalidDataException("Cache photo too large"); return File.ReadAllBytes(p);
            }).ToArray();
            if (pictures.Where((p, i) => Hash(p) != manifest.Hashes[i]).Any()) return null;
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return new CachedRender(manifest.Statistics, pictures, manifest.Notes);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NullReferenceException) { return null; }
    });
    public bool Write(string key, CachedRender entry, UserPreferences settings) => Locked(() =>
    {
        if (entry.Pictures.Length != 3 || entry.Pictures.Any(p => p.Length > 8 * 1024 * 1024)) throw new InvalidDataException("キャッシュ写真が不正です。");
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new Manifest(entry.Statistics, entry.Pictures.Select(Hash).ToArray(), entry.Notes));
        long size = metadata.Length + entry.Pictures.Sum(p => (long)p.Length);
        long limit = settings.CacheUnlimited ? long.MaxValue : checked(settings.CacheLimitMiB * 1024 * 1024);
        if (size > limit) return false;
        Directory.CreateDirectory(DirectoryPath);
        try
        {
            for (int i = 0; i < 3; i++) File.WriteAllBytes(PathFor(key, "." + Views[i] + ".png"), entry.Pictures[i]);
            // Manifest is the completion marker. All readers/writers share the same mutex.
            File.WriteAllBytes(PathFor(key, ".json"), metadata);
        }
        catch { DeleteEntry(key); throw; }
        TrimInternal(limit); return true;
    });
    public void Trim(UserPreferences settings) => Locked(() => { TrimInternal(settings.CacheUnlimited ? long.MaxValue : checked(settings.CacheLimitMiB * 1024 * 1024)); return true; });
    private void TrimInternal(long limit)
    {
        if (!Directory.Exists(DirectoryPath)) return;
        var groups = new DirectoryInfo(DirectoryPath).EnumerateFiles().Where(f => Regex.IsMatch(f.Name, "\\A[0-9a-f]{64}(?:\\.json|\\.(?:front|side|top)\\.png)\\z", RegexOptions.CultureInvariant))
            .GroupBy(f => f.Name[..64]).Select(g => new { Key = g.Key, Size = g.Sum(f => f.Length), Time = g.FirstOrDefault(f => f.Extension == ".json")?.LastWriteTimeUtc ?? DateTime.MinValue }).OrderBy(g => g.Time).ToArray();
        long total = groups.Sum(g => g.Size);
        foreach (var group in groups) { if (total <= limit) break; DeleteEntry(group.Key); total -= group.Size; }
    }
    private void DeleteEntry(string key)
    {
        foreach (string suffix in Views.Select(v => "." + v + ".png").Append(".json")) File.Delete(PathFor(key, suffix));
    }
}
