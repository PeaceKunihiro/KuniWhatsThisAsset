namespace KuniWhatsThisAsset.Core;

public enum ReadLimitKind { PackageBytes, ExpandedBytes, MemberBytes, Entries, Assets, Timeout, PathBytes }
public sealed class ReadLimitExceededException(ReadLimitKind kind, string message) : IOException(message)
{
    public ReadLimitKind Kind { get; } = kind;
}
public static class ScanLimitExtensions
{
    public static ScanLimits Increase(this ScanLimits limits, ReadLimitKind kind) => kind switch
    {
        ReadLimitKind.PackageBytes => limits with { MaxPackageBytes = checked(limits.MaxPackageBytes * 2) },
        ReadLimitKind.ExpandedBytes => limits with { MaxExpandedBytes = checked(limits.MaxExpandedBytes * 2) },
        ReadLimitKind.MemberBytes => limits with { MaxMemberBytes = checked(limits.MaxMemberBytes * 2) },
        ReadLimitKind.PathBytes => limits with { MaxPathBytes = checked(limits.MaxPathBytes * 2) },
        ReadLimitKind.Entries => limits with { MaxEntries = checked(limits.MaxEntries * 2) },
        ReadLimitKind.Assets => limits with { MaxAssets = checked(limits.MaxAssets * 2) },
        ReadLimitKind.Timeout => limits with { Timeout = limits.Timeout * 2 },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
