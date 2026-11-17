using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
namespace KuniWhatsThisAsset.Core;

internal sealed record ArchiveMember(string Name, long Length, bool Directory, Stream? DataStream);
// One interpretation of TAR headers/extension records is shared by scanning and repackaging.
internal sealed class UnityPackageArchive : IDisposable
{
    private const int MetadataLimit = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly FileStream file;
    private readonly GZipStream gzip;
    private readonly CheckedStream input;
    private readonly ScanLimits limits;
    private readonly CancellationToken token;
    private readonly uint expectedCrc, expectedSize;
    private readonly Dictionary<string, string> global = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> local = new(StringComparer.Ordinal);
    private string? longName;
    private MemberStream? current;
    private long padding, metadataBytes;
    private int entries;
    private bool finished, pendingLocal;
    public long BytesRead => input.BytesRead;
    public UnityPackageArchive(string path, ScanLimits limits, CancellationToken token)
    {
        this.limits = limits; this.token = token;
        file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!limits.IgnoreReadLimits && file.Length > limits.MaxPackageBytes) throw new ReadLimitExceededException(ReadLimitKind.PackageBytes, "パッケージの容量上限を超えています。");
            if (file.Length < 18) throw new InvalidDataException("GZipファイルが短すぎます。");
            Span<byte> footer = stackalloc byte[8]; file.Seek(-8, SeekOrigin.End); file.ReadExactly(footer);
            expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer); expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]); file.Position = 0;
            gzip = new(file, CompressionMode.Decompress, leaveOpen: true); input = new(gzip, limits, token);
        }
        catch { file.Dispose(); throw; }
    }
    public ArchiveMember? Next()
    {
        if (finished) return null;
        current?.Drain(); current = null; Skip(padding); padding = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested(); byte[] header = new byte[512]; input.ReadExactly(header);
            if (header.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                byte[] second = new byte[512]; input.ReadExactly(second);
                if (second.AsSpan().IndexOfAnyExcept((byte)0) >= 0 || pendingLocal || longName != null) throw new InvalidDataException("TAR終端または拡張ヘッダーの対応が不正です。");
                var buffer = new byte[65536]; int n;
                while ((n = input.Read(buffer)) > 0) if (buffer.AsSpan(0, n).IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("TAR終端の後に別のデータがあります。");
                if (input.BytesRead % 512 != 0 || input.Crc32 != expectedCrc || (uint)input.BytesRead != expectedSize) throw new InvalidDataException("GZipのCRC・サイズまたはTAR終端が一致しません。");
                finished = true; return null;
            }
            if (++entries > limits.MaxEntries && !limits.IgnoreReadLimits) throw new ReadLimitExceededException(ReadLimitKind.Entries, "アーカイブ項目数の上限を超えています。");
            long checksum = Number(header.AsSpan(148, 8)), unsigned = 0, signed = 0;
            for (int i = 0; i < 512; i++) { unsigned += i is >= 148 and < 156 ? 32 : header[i]; signed += i is >= 148 and < 156 ? 32 : unchecked((sbyte)header[i]); }
            if (checksum != unsigned && checksum != signed) throw new InvalidDataException("TARヘッダーのチェックサムが一致しません。");
            byte type = header[156]; long size = Number(header.AsSpan(124, 12));
            if (type is not (0 or (byte)'0' or (byte)'5' or (byte)'L' or (byte)'x' or (byte)'g')) throw new InvalidDataException("TARのリンク・特殊ファイルは読み込めません。");
            if (type is (byte)'L' or (byte)'x' or (byte)'g')
            {
                // PAX size overrides never apply to intermediary metadata records.
                if (size > MetadataLimit) throw new InvalidDataException("TAR拡張ヘッダーの容量上限を超えています。");
                metadataBytes += size;
                if (metadataBytes > 64L * MetadataLimit) throw new InvalidDataException("TAR拡張ヘッダーの総容量上限を超えています。");
                byte[] data = new byte[(int)size]; input.ReadExactly(data); Skip(Pad(size));
                if (type == 'L')
                {
                    if (longName != null) throw new InvalidDataException("GNU LongLinkが重複しています。");
                    int zero = Array.IndexOf(data, (byte)0);
                    if (zero >= 0 && data.AsSpan(zero).IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("GNU LongLinkの終端が不正です。");
                    longName = Decode(data.AsSpan(0, zero < 0 ? data.Length : zero));
                    ValidateArchiveName(longName);
                }
                else
                {
                    var target = type == 'g' ? global : local;
                    foreach (var (key, value) in Pax(data)) { if (type == 'g' && value.Length == 0) target.Remove(key); else target[key] = value; }
                    if (target.Count > 4096) throw new InvalidDataException("PAX属性数の上限を超えています。");
                    if (type == 'x') pendingLocal = true;
                }
                continue;
            }
            string? Attribute(string key) => local.TryGetValue(key, out string? value) ? (value.Length == 0 ? null : value) : global.GetValueOrDefault(key);
            if (Attribute("hdrcharset") is { } charset && charset != "ISO-IR 10646 2000 UTF-8") throw new InvalidDataException("PAXの文字コードが未対応です。");
            string? name = Attribute("path") ?? longName;
            if (name == null)
            {
                name = Text(header.AsSpan(0, 100));
                if (header.AsSpan(257, 6).SequenceEqual("ustar\0"u8)) { string prefix = Text(header.AsSpan(345, 155)); if (prefix.Length > 0) name = prefix + "/" + name; }
            }
            if (Attribute("size") is { } declared)
            {
                if (declared.Length == 0 || declared.Any(c => c is < '0' or > '9') || !long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out size)) throw new InvalidDataException("PAX sizeが不正です。");
            }
            local.Clear(); pendingLocal = false; longName = null;
            if (!limits.IgnoreReadLimits && size > limits.MaxMemberBytes) throw new ReadLimitExceededException(ReadLimitKind.MemberBytes, "TARメンバーの容量上限を超えています。");
            if (type == '5' && size != 0) throw new InvalidDataException("ディレクトリのサイズが不正です。");
            name = NormalizeName(name);
            if (type == '5' && name is "" or ".") continue;
            PackageReader.ValidatePath(name.TrimEnd('/'));
            padding = Pad(size); current = new(input, size, token);
            return new(name, size, type == '5', current);
        }
    }
    private static Dictionary<string, string> Pax(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal); int position = 0, records = 0;
        while (position < data.Length)
        {
            int space = Array.IndexOf(data, (byte)' ', position);
            if (++records > 4096 || space <= position || space - position > 10 || data.AsSpan(position, space - position).IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0
                || !int.TryParse(Encoding.ASCII.GetString(data, position, space - position), out int length) || length <= space - position + 2 || length > data.Length - position || data[position + length - 1] != '\n') throw new InvalidDataException("PAXレコード長が不正です。");
            string record = Decode(data.AsSpan(space + 1, position + length - space - 2)); int equals = record.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException("PAXレコードが不正です。");
            string key = record[..equals], value = record[(equals + 1)..];
            if (key.StartsWith("GNU.sparse", StringComparison.Ordinal) || key is "SCHILY.realsize" or "linkpath") throw new InvalidDataException("PAXの疎ファイル・リンク属性は読み込めません。");
            if (key == "path" && value.Length > 0) ValidateArchiveName(value);
            result[key] = value; position += length;
        }
        return result;
    }
    private static string Decode(ReadOnlySpan<byte> bytes) { try { return Utf8.GetString(bytes); } catch (DecoderFallbackException ex) { throw new InvalidDataException("TARの名前・属性がUTF-8ではありません。", ex); } }
    private static string NormalizeName(string name) { while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..]; return name; }
    private static void ValidateArchiveName(string name)
    {
        string normalized = NormalizeName(name);
        if (normalized == "." || (normalized.Length == 0 && name.StartsWith("./", StringComparison.Ordinal))) return;
        PackageReader.ValidatePath(normalized.TrimEnd('/'));
    }
    private static string Text(ReadOnlySpan<byte> bytes) { int zero = bytes.IndexOf((byte)0); return Decode(zero < 0 ? bytes : bytes[..zero]); }
    private static long Number(ReadOnlySpan<byte> field)
    {
        try
        {
            long result = 0;
            if ((field[0] & 0x80) != 0)
            {
                if ((field[0] & 0x40) != 0) throw new InvalidDataException("負のTARサイズは読み込めません。");
                result = field[0] & 0x7f; foreach (byte b in field[1..]) result = checked(result * 256 + b); return result;
            }
            bool ended = false, started = false;
            foreach (byte b in field) { if (b is 0 or 32) { if (started) ended = true; continue; } if (ended || b < '0' || b > '7') throw new InvalidDataException("TARの数値が不正です。"); started = true; result = checked(result * 8 + b - '0'); }
            return result;
        }
        catch (OverflowException ex) { throw new InvalidDataException("TARサイズが大きすぎます。", ex); }
    }
    private static long Pad(long size) => (512 - size % 512) % 512;
    private void Skip(long size) { var bytes = new byte[512]; while (size > 0) { int n = (int)Math.Min(size, bytes.Length); input.ReadExactly(bytes.AsSpan(0, n)); size -= n; } }
    public void Dispose() { input.Dispose(); gzip.Dispose(); file.Dispose(); }
    private sealed class MemberStream(Stream source, long length, CancellationToken token) : Stream
    {
        private long remaining = length;
        private readonly long totalLength = length;
        public void Drain() { var buffer = new byte[65536]; while (Read(buffer) > 0) { } }
        public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); int n = source.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]); if (n == 0 && remaining > 0 && buffer.Length > 0) throw new EndOfStreamException("TARメンバーが途中で切れています。"); remaining -= n; return n; }
        public override int Read(byte[] b, int o, int c) => Read(b.AsSpan(o, c));
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => totalLength; public override long Position { get => totalLength - remaining; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
    private sealed class CheckedStream(Stream source, ScanLimits limits, CancellationToken token) : Stream
    {
        public long BytesRead { get; private set; } private uint crc = 0xffffffff; public uint Crc32 => ~crc;
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i => { uint n = (uint)i; for (int j = 0; j < 8; j++) n = (n & 1) == 0 ? n >> 1 : (n >> 1) ^ 0xedb88320; return n; }).ToArray();
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested(); int n = source.Read(limits.IgnoreReadLimits ? buffer : buffer[..(int)Math.Min(buffer.Length, Math.Max(1, limits.MaxExpandedBytes - BytesRead + 1))]); BytesRead += n;
            if (!limits.IgnoreReadLimits && BytesRead > limits.MaxExpandedBytes) throw new ReadLimitExceededException(ReadLimitKind.ExpandedBytes, "展開後の総容量上限を超えています。");
            foreach (byte b in buffer[..n]) crc = Table[(crc ^ b) & 0xff] ^ (crc >> 8); return n;
        }
        public override int Read(byte[] b, int o, int c) => Read(b.AsSpan(o, c));
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false; public override long Length => throw new NotSupportedException(); public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
