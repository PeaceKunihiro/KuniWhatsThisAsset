using System.Text.Json;

namespace KuniWhatsThisAsset.Core;

public sealed record UserPreferences
{
    public long PackageLimitMiB { get; init; } = 500;
    public bool PackageUnlimited { get; init; }
    public long CacheLimitMiB { get; init; } = 500;
    public bool CacheUnlimited { get; init; }
    public List<string> RecentPackages { get; init; } = [];
    public List<string> Bookmarks { get; init; } = [];
    public static long MaxMiB => long.MaxValue / (1024 * 1024);
    public void Validate()
    {
        if (PackageLimitMiB < 1 || PackageLimitMiB > MaxMiB || CacheLimitMiB < 1 || CacheLimitMiB > MaxMiB) throw new ArgumentException("上限には1以上の整数MiBを入力してください。");
        if (RecentPackages == null || Bookmarks == null || RecentPackages.Count > 10 || Bookmarks.Count > 1000 || RecentPackages.Concat(Bookmarks).Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 32767)) throw new ArgumentException("履歴・ブックマークが不正です。");
    }
    public ScanLimits ScanLimits() => new() { MaxPackageBytes = PackageUnlimited ? long.MaxValue : checked(PackageLimitMiB * 1024 * 1024) };
}

public sealed class PreferenceStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private string FilePath => Path.Combine(DirectoryPath, "preferences.json");
    public UserPreferences Read()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 1024 * 1024) return new();
            var value = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(FilePath)) ?? new(); value.Validate(); return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }
    public void Write(UserPreferences preferences)
    {
        preferences.Validate(); Directory.CreateDirectory(DirectoryPath);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(preferences, new JsonSerializerOptions { WriteIndented = true });
            if (bytes.Length > 1024 * 1024) throw new IOException("設定・履歴ファイルの容量上限を超えています。");
            File.WriteAllBytes(temporary, bytes); File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public UserPreferences RecordPackage(UserPreferences state, string path)
    {
        path = Path.GetFullPath(path);
        var next = state with { RecentPackages = new[] { path }.Concat(state.RecentPackages.Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase))).Take(10).ToList() }; Write(next); return next;
    }
    public UserPreferences Bookmark(UserPreferences state, string directory, bool add)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var list = state.Bookmarks.Where(p => !p.Equals(directory, StringComparison.OrdinalIgnoreCase)).ToList();
        if (add) { if (list.Count >= 1000) throw new ArgumentException("ブックマークの上限（1,000件）に達しました。"); list.Add(directory); }
        var next = state with { Bookmarks = list }; Write(next); return next;
    }
}
