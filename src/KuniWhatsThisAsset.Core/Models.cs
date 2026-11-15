namespace KuniWhatsThisAsset.Core;

public enum Severity { Info, Warning, High }
public sealed record Finding(Severity Severity, string Code, string Path, string Message);
public sealed record PackageItem(string Guid, string Path, long Size, bool IsDirectory,
    IReadOnlyList<Finding> Findings, string? TextPreview)
{
    public string Name => Path.Split('/')[^1];
    public string Format => IsDirectory ? "Folder" : System.IO.Path.GetExtension(Path).ToLowerInvariant() is { Length: > 0 } ext ? ext : "(拡張子なし)";
    public string SizeLabel => IsDirectory ? "—" : $"{Size:N0} B";
    public string RiskLabel => Findings.Any(f => f.Severity == Severity.High) ? "要確認（高）" : Findings.Count > 0 ? "要確認" : "検出なし";
}
public sealed record PackageReport(string SourcePath, IReadOnlyList<PackageItem> Items,
    IReadOnlyList<Finding> Findings, long ExpandedBytes)
{
    public int FileCount => Items.Count(i => !i.IsDirectory);
}
public sealed record ScanLimits
{
    public long MaxPackageBytes { get; init; } = 512L * 1024 * 1024;
    public long MaxExpandedBytes { get; init; } = 1024L * 1024 * 1024;
    public long MaxMemberBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxEntries { get; init; } = 50_000;
    public int MaxAssets { get; init; } = 10_000;
    public int MaxTextBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxTotalTextBytes { get; init; } = 32 * 1024 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}
