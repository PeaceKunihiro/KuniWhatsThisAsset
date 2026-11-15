using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Buffers.Binary;

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
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!limits.IgnoreReadLimits && input.Length > limits.MaxPackageBytes) throw new ReadLimitExceededException(ReadLimitKind.PackageBytes, "パッケージの容量上限を超えています。");
        if (input.Length < 18) throw new InvalidDataException("GZipファイルが短すぎます。");
        Span<byte> footer = stackalloc byte[8];
        input.Seek(-8, SeekOrigin.End);
        input.ReadExactly(footer);
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        uint expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]);
        input.Position = 0;
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var bounded = new BudgetStream(gzip, limits, token);
        using var tar = new TarReader(bounded, leaveOpen: true);
        var assets = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var findings = new List<Finding>();
        int count = 0, sampleBudget = limits.MaxTotalTextBytes;
        TarEntry? entry;
        long endHeaderOffset;
        while (true)
        {
            // The reader may still need to consume padding from the previous member.
            endHeaderOffset = (bounded.BytesRead + 511) / 512 * 512;
            entry = tar.GetNextEntry(copyData: false);
            if (entry == null) break;
            token.ThrowIfCancellationRequested();
            if (++count > limits.MaxEntries && !limits.IgnoreReadLimits) throw new ReadLimitExceededException(ReadLimitKind.Entries, "アーカイブ内の項目数が上限を超えています。");
            string name = entry.Name.TrimEnd('/');
            ValidatePath(name);
            if (!names.Add(name)) throw new InvalidDataException($"アーカイブ内に重複した項目があります: {name}");
            if (entry.EntryType is TarEntryType.Directory)
            {
                if (!Regex.IsMatch(name, @"\A[0-9a-fA-F]{32}\z")) throw new InvalidDataException("不正なパッケージディレクトリです。");
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException($"リンク・特殊ファイルは読み込めません: {name}");
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
                    if (entry.Length > 4096) throw new InvalidDataException("pathnameが長すぎます。");
                    byte[] pathBytes = ReadSample(entry, 4096, token);
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
        // Consume the trailer as well: enforce the decompression limit and validate GZip integrity.
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = bounded.Read(buffer, 0, buffer.Length)) > 0)
            if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidDataException("TAR終端の後に不明なデータがあります。");
        if (bounded.Crc32 != expectedCrc || (uint)bounded.BytesRead != expectedSize)
            throw new InvalidDataException("GZipのCRCまたは展開サイズが一致しません。破損・切り詰め・複数ストリームの可能性があります。");
        if (bounded.BytesRead % 512 != 0 || bounded.TrailingZeros < 1024 || bounded.BytesRead - endHeaderOffset < 1024)
            throw new InvalidDataException("TAR終端のゼロブロックが不足しています。");
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
        return new(System.IO.Path.GetFullPath(file), items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToArray(), findings, bounded.BytesRead);
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Contains('\\') || path.StartsWith('/') || path.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)))
            throw new InvalidDataException("絶対パス・制御文字・Windowsで不正な文字を含むパスです。");
        foreach (string part in path.Split('/'))
        {
            string stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Length > 255 ||
                Regex.IsMatch(stem, @"\A(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("パストラバーサルまたはWindowsで不正なパスを検出しました。");
        }
    }

    private static byte[] ReadSample(TarEntry entry, int capacity, CancellationToken token, AssetCapture? capture = null)
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

    private sealed class BudgetStream(Stream inner, ScanLimits limits, CancellationToken token) : Stream
    {
        public long BytesRead { get; private set; }
        public long TrailingZeros { get; private set; }
        private uint crc = 0xffffffff;
        public uint Crc32 => ~crc;
        private static readonly uint[] CrcTable = BuildCrcTable();
        private readonly byte[] header = new byte[512];
        private int headerBytes, physicalEntries, endBlocks;
        private long dataRemaining;

        // Guard physical TAR headers before TarReader interprets extension records.
        // Unity's short GUID/member names do not require PAX/GNU extension payloads.
        private void GuardHeaders(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                if (dataRemaining > 0)
                {
                    int skip = (int)Math.Min(dataRemaining, bytes.Length);
                    dataRemaining -= skip; bytes = bytes[skip..]; continue;
                }
                int take = Math.Min(512 - headerBytes, bytes.Length);
                bytes[..take].CopyTo(header.AsSpan(headerBytes));
                headerBytes += take; bytes = bytes[take..];
                if (headerBytes != 512) continue;
                headerBytes = 0;
                if (header.AsSpan().IndexOfAnyExcept((byte)0) < 0) { endBlocks++; continue; }
                if (endBlocks > 0) throw new InvalidDataException("TAR終端の後に別の項目があります。");
                if (++physicalEntries > limits.MaxEntries && !limits.IgnoreReadLimits) throw new ReadLimitExceededException(ReadLimitKind.Entries, "アーカイブ項目数の上限を超えています。");
                if (header[156] is not (0 or (byte)'0' or (byte)'5'))
                    throw new InvalidDataException("TARのリンク・特殊ファイル・PAX/GNU拡張レコードは読み込めません。");
                long size = Octal(header.AsSpan(124, 12));
                if (!limits.IgnoreReadLimits && size > limits.MaxMemberBytes) throw new ReadLimitExceededException(ReadLimitKind.MemberBytes, "TARメンバーの容量上限を超えています。");
                if (header[156] == '5' && size != 0) throw new InvalidDataException("ディレクトリのサイズが不正です。");
                long checksum = 0;
                for (int i = 0; i < 512; i++) checksum += i is >= 148 and < 156 ? 32 : header[i];
                if (checksum != Octal(header.AsSpan(148, 8))) throw new InvalidDataException("TARヘッダーのチェックサムが一致しません。");
                dataRemaining = (size + 511) / 512 * 512;
            }
        }
        private static long Octal(ReadOnlySpan<byte> field)
        {
            long result = 0;
            bool ended = false, started = false;
            foreach (byte b in field)
            {
                if (b is 0 or 32) { if (started) ended = true; continue; }
                if (ended || b < '0' || b > '7') throw new InvalidDataException("TARヘッダーの数値表現が未対応または不正です。");
                started = true;
                result = checked(result * 8 + b - '0');
            }
            return result;
        }
        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
                table[i] = value;
            }
            return table;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            int n = inner.Read(limits.IgnoreReadLimits ? buffer : buffer[..(int)Math.Min(buffer.Length, Math.Max(1, limits.MaxExpandedBytes - BytesRead + 1))]);
            BytesRead += n;
            if (!limits.IgnoreReadLimits && BytesRead > limits.MaxExpandedBytes) throw new ReadLimitExceededException(ReadLimitKind.ExpandedBytes, "展開後の総容量上限を超えています。");
            GuardHeaders(buffer[..n]);
            foreach (byte value in buffer[..n])
            {
                crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
                TrailingZeros = value == 0 ? TrailingZeros + 1 : 0;
            }
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}








