using System.Text;
using System.Text.RegularExpressions;

namespace KuniWhatsThisAsset.Core;

public sealed class PackageReader
{
    private readonly ScanLimits limits;
    public PackageReader(ScanLimits? limits = null) => this.limits = limits ?? new();

    private sealed class CaptureBudget(long max) { public long Remaining = max; }
    public Dictionary<string, byte[]> ReadAssetSet(string file, IReadOnlyCollection<string> guids, bool metadata = false, int maxFileBytes = 64 * 1024 * 1024, long maxTotalBytes = 256L * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        if (guids.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        if (guids.Count > 512) throw new InvalidDataException("関連アセット数の上限を超えています。");
        var budget = new CaptureBudget(maxTotalBytes);
        string member = metadata ? "asset.meta" : "asset";
        var captures = guids.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(g => g + "/" + member, g => new AssetCapture(g, maxFileBytes, member, budget), StringComparer.OrdinalIgnoreCase);
        try { ReadInternal(file, cancellationToken, null, captures: captures); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ReadLimitExceededException(ReadLimitKind.Timeout, "関連アセットの読み込み時間の上限に達しました。"); }
        return captures.Values.Where(c => c.Bytes != null).ToDictionary(c => c.Guid, c => c.Bytes!, StringComparer.OrdinalIgnoreCase);
    }    private sealed class AssetCapture(string guid, int maxBytes, string member = "asset", CaptureBudget? budget = null)
    {
        public string Guid { get; } = guid;
        public string Member { get; } = member;
        public int MaxBytes { get; } = maxBytes;
        public byte[]? Bytes;
        public CaptureBudget? Budget { get; } = budget;
    }
    public byte[] ReadAssetBytes(string file, string guid, int maxBytes = 64 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        var capture = new AssetCapture(guid, maxBytes);
        try { ReadInternal(file, cancellationToken, null, capture); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ReadLimitExceededException(ReadLimitKind.Timeout, "プレビュー用アセットの読み込み時間の上限に達しました。"); }
        return capture.Bytes ?? throw new InvalidDataException("選択したアセットが見つかりません。パッケージを再読み込みしてください。");
    }
    public byte[] ReadAssetMetaBytes(string file, string guid, CancellationToken cancellationToken = default)
    {
        var capture = new AssetCapture(guid, 2 * 1024 * 1024, "asset.meta");
        try { ReadInternal(file, cancellationToken, null, capture); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ReadLimitExceededException(ReadLimitKind.Timeout, "プレビュー用メタデータの読み込み時間の上限に達しました。"); }
        return capture.Bytes ?? [];
    }    private sealed class Asset
    {
        public string? Path;
        public long? Size;
        public byte[]? Sample;
        public byte[]? Meta;
        public long MetaSize;
    }

    public PackageReport Read(string file, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
    {
        try { return ReadInternal(file, cancellationToken, progress); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ReadLimitExceededException(ReadLimitKind.Timeout, "解析時間の上限に達しました。"); }
    }

    private PackageReport ReadInternal(string file, CancellationToken cancellationToken, IProgress<int>? progress, AssetCapture? capture = null, Dictionary<string, AssetCapture>? captures = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!limits.IgnoreReadLimits) deadline.CancelAfter(limits.Timeout);
        var token = deadline.Token;
        using var archive = new UnityPackageArchive(file, limits, token);
        var assets = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var findings = new List<Finding>();
        int count = 0, sampleBudget = limits.MaxTotalTextBytes;
        long pathBytesTotal = 0;
        ArchiveMember? entry;
        while (true)
        {
            entry = archive.Next();
            if (entry == null) break;
            token.ThrowIfCancellationRequested();
            if (++count > limits.MaxEntries && !limits.IgnoreReadLimits) throw new ReadLimitExceededException(ReadLimitKind.Entries, "アーカイブ内の項目数が上限を超えています。");
            string name = entry.Name.TrimEnd('/');
            ValidatePath(name);
            if (!names.Add(name)) throw new InvalidDataException($"アーカイブ内に重複した項目があります: {name}");
            if (entry.Directory)
            {
                if (!Regex.IsMatch(name, @"\A[0-9a-fA-F]{32}\z")) throw new InvalidDataException("不正なパッケージディレクトリです。");
                continue;
            }
            if (entry.Length < 0 || (!limits.IgnoreReadLimits && entry.Length > limits.MaxMemberBytes)) throw new ReadLimitExceededException(ReadLimitKind.MemberBytes, $"ファイルの容量上限を超えています: {name}");
            string[] parts = name.Split('/');
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], @"\A[0-9a-fA-F]{32}\z"))
                throw new InvalidDataException($"UnityPackageのGUID/ファイル構造ではありません: {name}");
            if (!assets.TryGetValue(parts[0], out var asset))
            {
                if (!limits.IgnoreReadLimits && assets.Count >= limits.MaxAssets) throw new ReadLimitExceededException(ReadLimitKind.Assets, "アセット数が上限を超えています。");
                assets.Add(parts[0], asset = new());
            }
            switch (parts[1])
            {
                case "pathname":
                    if (entry.Length > 131072) throw new InvalidDataException("pathnameが長すぎます。");
                    pathBytesTotal += entry.Length;
                    if (!limits.IgnoreReadLimits && pathBytesTotal > limits.MaxPathBytes) throw new ReadLimitExceededException(ReadLimitKind.PathBytes, "パッケージ内パス情報の総容量上限（初期64 MiB）を超えています。");
                    byte[] pathBytes = ReadSample(entry, 131072, token);
                    string path;
                    try { path = new UTF8Encoding(false, true).GetString(pathBytes).TrimEnd('\r', '\n'); }
                    catch (DecoderFallbackException ex) { throw new InvalidDataException("pathnameがUTF-8ではありません。", ex); }
                    ValidatePath(path);
                    asset.Path = path;
                    break;
                case "asset":
                case "asset.meta":
                    // pathname may follow asset, so sample first and classify after reading the archive.
                    int capacity = Math.Min(limits.MaxTextBytes, sampleBudget);
                    byte[] sample = ReadSample(entry, Math.Max(4096, capacity), token,
                        captures != null && captures.TryGetValue(name, out var requested) ? requested :
                        parts[1] == capture?.Member && capture.Guid.Equals(parts[0], StringComparison.OrdinalIgnoreCase) ? capture : null);
                    sampleBudget -= Math.Min(sample.Length, capacity);
                    if (parts[1] == "asset") { asset.Size = entry.Length; asset.Sample = sample; }
                    else { asset.Meta = sample; asset.MetaSize = entry.Length; }
                    break;
                default:
                    int extraCapacity = Math.Min(limits.MaxTextBytes, sampleBudget);
                    byte[] extra = ReadSample(entry, Math.Max(4096, extraCapacity), token);
                    sampleBudget -= Math.Min(extra.Length, extraCapacity);
                    findings.AddRange(SecurityScanner.Scan(name, extra, entry.Length).Findings);
                    if (parts[1] != "preview.png") findings.Add(new(Severity.Warning, "UNKNOWN_MEMBER", name, "標準外のパッケージ内メンバーです。シグネチャと対応テキスト形式のみを検査しました。"));
                    break;
            }
            if (count == 1 || count % 100 == 0) progress?.Report(count);
        }
        if (assets.Count == 0) throw new InvalidDataException("UnityPackageのアセットが見つかりません。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<PackageItem>();
        foreach (var (guid, asset) in assets)
        {
            token.ThrowIfCancellationRequested();
            if (asset.Path == null) throw new InvalidDataException($"pathnameが欠落しています: {guid}");
            if (!paths.Add(asset.Path)) throw new InvalidDataException($"パッケージ内パスが重複しています: {asset.Path}");
            var itemFindings = new List<Finding>();
            string? text = null;
            if (asset.Size != null)
            {
                var scan = SecurityScanner.Scan(asset.Path, asset.Sample ?? [], asset.Size.Value);
                itemFindings.AddRange(scan.Findings);
                text = scan.Text;
            }
            else
            {
                string meta = asset.Meta == null ? "" : Encoding.UTF8.GetString(asset.Meta);
                if (!Regex.IsMatch(meta, @"(?m)^folderAsset:\s*yes\s*$"))
                    throw new InvalidDataException($"assetが欠落しており、フォルダーとも確認できません: {asset.Path}");
            }
            if (asset.Meta == null) itemFindings.Add(new(Severity.Warning, "META_MISSING", asset.Path, "asset.metaがありません。"));
            else itemFindings.AddRange(SecurityScanner.Scan(asset.Path + ".meta", asset.Meta, asset.MetaSize).Findings);
            items.Add(new(guid, asset.Path, asset.Size ?? 0, asset.Size == null, itemFindings, text));
            findings.AddRange(itemFindings);
        }
        return new(System.IO.Path.GetFullPath(file), items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToArray(), findings, archive.BytesRead);
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32700 || path.Contains('\\') || path.StartsWith('/') || path.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)))
            throw new InvalidDataException("絶対パス・制御文字・Windowsで不正な文字を含むパスです。");
        foreach (string part in path.Split('/'))
        {
            string stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Length > 255 ||
                Regex.IsMatch(stem, @"\A(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("パストラバーサルまたはWindowsで不正なパスを検出しました。");
        }
    }

    private static byte[] ReadSample(ArchiveMember entry, int capacity, CancellationToken token, AssetCapture? capture = null)
    {
        if (capture != null && entry.Length > capture.MaxBytes) throw new InvalidDataException($"プレビュー用アセットの入力上限（{capture.MaxBytes / 1024 / 1024} MiB）を超えています。");
        if (capture?.Budget != null) { if (entry.Length > capture.Budget.Remaining) throw new InvalidDataException("関連アセットの総容量上限を超えています。"); capture.Budget.Remaining -= entry.Length; }
        using var captured = capture == null ? null : new MemoryStream();
        using var memory = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        if (entry.DataStream != null)
        {
            while ((read = entry.DataStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                int keep = (int)Math.Min(read, Math.Max(0, capacity - memory.Length));
                if (keep > 0) memory.Write(buffer, 0, keep);
                captured?.Write(buffer, 0, read);
                total += read;
            }
        }
        if (total != entry.Length) throw new InvalidDataException("アーカイブ内のファイルが途中で切れています。");
        if (capture != null) capture.Bytes = captured!.ToArray();
        return memory.ToArray();
    }

}
