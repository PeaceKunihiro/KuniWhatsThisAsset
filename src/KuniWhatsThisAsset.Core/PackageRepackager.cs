using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
namespace KuniWhatsThisAsset.Core;

public sealed record RepackageSource(PackageReport Report, string Sha256, ScanLimits Limits);
public sealed record RepackagePlan(RepackageSource Source, string FolderName, IReadOnlyDictionary<string,string> Paths, string? AddedFolderGuid, string[] SpecialPaths);
public sealed record RepackageProgress(string Message, long Members = 0);
public sealed record RepackageResult(string OutputPath, int Assets, string Sha256);
public static class PackageRepackager
{
    public static readonly string[] FixedRootFolders = ["StreamingAssets", "Gizmos", "Editor Default Resources", "Standard Assets", "Pro Standard Assets", "Plugins"];
    public static RepackageSource Analyze(string path, ScanLimits limits, CancellationToken token = default)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string before = Digest(input, token); var report = new PackageReader(limits).Read(path, token);
        input.Position = 0; if (before != Digest(input, token)) throw new IOException("解析中に元パッケージが変更されました。読み込み直してください。");
        if (report.Items.Any(i => i.Path != "Assets" && !i.Path.StartsWith("Assets/", StringComparison.Ordinal))) throw new InvalidDataException("リパッケージはAssets配下のみ対応しています。元の内容を省略しないため処理を中止しました。");
        return new(report, before, limits);
    }
    public static string ValidateFolderName(string name)
    {
        name = name.Trim(); PackageReader.ValidatePath("Assets/" + name);
        if (name.Length == 0 || name.Contains('/')) throw new InvalidDataException("まとめ先には1つのフォルダー名を指定してください。");
        if (FixedRootFolders.Contains(name, StringComparer.OrdinalIgnoreCase) || name.Equals("Editor", StringComparison.OrdinalIgnoreCase) || name.Equals("Resources", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("まとめ先には特殊フォルダー以外の名前を指定してください。");
        return name;
    }
    public static bool IsSpecial(string path) => FixedRootFolders.Any(f => path.Equals("Assets/" + f, StringComparison.OrdinalIgnoreCase) || path.StartsWith("Assets/" + f + "/", StringComparison.OrdinalIgnoreCase));
    public static string TargetPath(string original, string folder, bool keepSpecial) => keepSpecial && IsSpecial(original) ? original : "Assets/" + folder + (original == "Assets" ? "" : original[6..]);
    public static RepackagePlan Plan(RepackageSource source, string folderName, bool keepSpecialFolders = true)
    {
        string folder = ValidateFolderName(folderName);
        var special = source.Report.Items.Where(i => IsSpecial(i.Path)).Select(i => "Assets/" + i.Path.Split('/')[1]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (special.Length > 0 && !keepSpecialFolders) throw new InvalidDataException("特殊フォルダーを一律移動すると動作が変わるため、元の配置を保持する指定が必要です。");
        if (FixedRootFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("まとめ先には特殊フォルダー以外の名前を指定してください。");
        var paths = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source.Report.Items)
        {
            string target = TargetPath(item.Path, folder, keepSpecialFolders); PackageReader.ValidatePath(target);
            if (Encoding.UTF8.GetByteCount(target) > 131072 || !outputs.Add(target)) throw new InvalidDataException("変換後のパスが長すぎるか、配置先が重複しています。");
            paths.Add(item.Guid, target);
        }
        var root = source.Report.Items.FirstOrDefault(i => i.Path == "Assets");
        if (root != null && !root.IsDirectory) throw new InvalidDataException("Assets自身がフォルダーではありません。");
        string? added = null;
        if (root == null) { do { added = Guid.NewGuid().ToString("N"); } while (paths.ContainsKey(added)); }
        // No file may become the ancestor of another output asset.
        var files = source.Report.Items.Where(i => !i.IsDirectory).Select(i => paths[i.Guid]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string target in paths.Values)
        {
            int slash = target.LastIndexOf('/');
            while (slash > 0) { string parent = target[..slash]; if (files.Contains(parent)) throw new InvalidDataException("変換後のファイルとフォルダーが衝突しています。"); slash = parent.LastIndexOf('/'); }
        }
        return new(source, folder, paths, added, special);
    }
    public static RepackageResult Save(RepackagePlan plan, string destination, IProgress<RepackageProgress>? progress = null, CancellationToken token = default)
    {
        string source = plan.Source.Report.SourcePath, target = Path.GetFullPath(destination);
        if (!Path.GetExtension(target).Equals(".unitypackage", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("保存先は.unitypackageを指定してください。");
        if (target.Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase) || File.Exists(target)) throw new IOException("元ファイルや既存ファイルは上書きしません。新しい保存名を指定してください。");
        string folder = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("保存先フォルダーがありません。");
        string temporary = Path.Combine(folder, ".kwta-repackage-" + Guid.NewGuid().ToString("N") + ".partial");
        using var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        progress?.Report(new("元パッケージを照合中…"));
        if (Digest(locked, token) != plan.Source.Sha256) throw new IOException("元パッケージが変更されています。解析し直してください。");
        // Rebuild the plan instead of trusting caller-supplied mutable dictionaries.
        var checkedPlan = Plan(plan.Source, plan.FolderName, true);
        var paths = checkedPlan.Paths; string? added = plan.AddedFolderGuid;
        if ((added == null) != (checkedPlan.AddedFolderGuid == null) || (added != null && (added.Length != 32 || added.Any(c => !Uri.IsHexDigit(c)) || paths.ContainsKey(added)))) throw new InvalidDataException("追加フォルダーのGUIDが不正です。");
        var expected = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); long members = 0;
        try
        {
            progress?.Report(new("リパッケージ中…"));
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    var writer = new PackageTarWriter(gzip);
                    using var archive = new UnityPackageArchive(source, plan.Source.Limits, token); ArchiveMember? member;
                    while ((member = archive.Next()) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        string name = member.Name.TrimEnd('/'); PackageReader.ValidatePath(name);
                        if (member.Directory) { writer.Write(member.Name, 0, null, true, token); continue; }
                        string[] components = name.Split('/');
                        if (components.Length != 2 || !paths.TryGetValue(components[0], out string? mapped)) throw new InvalidDataException("元パッケージの構造が解析時と一致しません。");
                        if (components[1] == "pathname")
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(mapped); writer.WriteBytes(name, bytes, token); expected.Add(name, Hash(bytes));
                        }
                        else
                        {
                            using var hashing = new HashStream(member.DataStream!); writer.Write(name, member.Length, hashing, false, token); expected.Add(name, hashing.Digest());
                        }
                        if (++members % 100 == 0) progress?.Report(new($"リパッケージ中… {members:N0}メンバー", members));
                    }
                    if (added != null)
                    {
                        byte[] path = Encoding.UTF8.GetBytes("Assets/" + plan.FolderName), meta = Encoding.UTF8.GetBytes($"fileFormatVersion: 2\nguid: {added}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n");
                        writer.WriteBytes(added + "/pathname", path, token); expected.Add(added + "/pathname", Hash(path));
                        writer.WriteBytes(added + "/asset.meta", meta, token); expected.Add(added + "/asset.meta", Hash(meta));
                    }
                    writer.Complete();
                }
                output.Flush(flushToDisk: true);
            }
            locked.Position = 0; if (Digest(locked, token) != plan.Source.Sha256) throw new IOException("処理中に元パッケージが変更されました。");
            progress?.Report(new("保存結果を読み戻して検証中…"));
            var verificationLimits = plan.Source.Limits with { IgnoreReadLimits = true };
            var report = new PackageReader(verificationLimits).Read(temporary, token);
            if (report.Items.Count != paths.Count + (added == null ? 0 : 1) || report.Items.Any(i => i.Guid != added && (!paths.TryGetValue(i.Guid, out string? p) || i.Path != p))) throw new InvalidDataException("保存結果のGUID・配置先が一致しません。");
            using (var archive = new UnityPackageArchive(temporary, verificationLimits, token))
            {
                ArchiveMember? member;
                while ((member = archive.Next()) != null)
                {
                    if (member.Directory) continue;
                    using var stream = new HashStream(member.DataStream!); var buffer = new byte[65536]; while (stream.Read(buffer) > 0) { token.ThrowIfCancellationRequested(); }
                    if (!expected.Remove(member.Name, out string? hash) || hash != stream.Digest()) throw new InvalidDataException("保存結果のアセット・metaの内容が一致しません。");
                }
            }
            if (expected.Count != 0) throw new InvalidDataException("保存結果に欠落メンバーがあります。");
            token.ThrowIfCancellationRequested(); using var complete = File.OpenRead(temporary); string resultHash = Digest(complete, token); complete.Dispose();
            File.Move(temporary, target, overwrite: false);
            return new(target, report.Items.Count, resultHash);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static string Digest(Stream stream, CancellationToken token) { using var hash = new HashStream(stream); var bytes = new byte[65536]; while (hash.Read(bytes) > 0) token.ThrowIfCancellationRequested(); return hash.Digest(); }
    private sealed class HashStream(Stream source) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public string Digest() => Convert.ToHexString(hash.GetCurrentHash());
        public override int Read(Span<byte> buffer) { int n = source.Read(buffer); hash.AppendData(buffer[..n]); return n; }
        public override int Read(byte[] b, int o, int c) => Read(b.AsSpan(o, c));
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => source.Length; public override long Position { get => source.Position; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
    }
}
