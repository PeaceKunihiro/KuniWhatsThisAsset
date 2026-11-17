using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;

// Only explicit references below the user-selected model's directory are read.
public sealed class LocalModelDocument
{
    public string SourcePath { get; }
    public ModelInput Input { get; private set; } = null!;
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Finding> Findings { get; } = [];
    public List<string> Notes { get; } = [];
    private readonly Dictionary<string, string?> textureByMaterial = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> textureFiles = new(StringComparer.Ordinal);
    private readonly string root;
    private long textureBytes;
    private LocalModelDocument(string path) { SourcePath = Path.GetFullPath(path); root = Path.GetDirectoryName(SourcePath)!; }
    public static LocalModelDocument Read(string path, CancellationToken token)
    {
        var document = new LocalModelDocument(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".fbx" or ".obj")) throw new InvalidDataException("FBXまたはOBJを選択してください。");
        // A neutral virtual name avoids imposing Unity pathname restrictions on an OS filename.
        byte[] bytes = document.ReadFile(Path.GetFileName(path), 256 * 1024 * 1024, token);
        var dependencies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (ext == ".obj")
        {
            string text = TextDecoder.Decode(bytes, false, out _);
            foreach (Match match in Matches(text, @"(?m)^\s*mtllib\s+([^\r\n]+)"))
            {
                string reference = match.Groups[1].Value.Trim();
                string[] candidates = document.Exists(reference) ? [reference] : Tokens(reference);
                foreach (string name in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string? relative = document.Resolve("", name);
                    if (relative == null || !relative.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase)) { document.Notes.Add("MTL参照を拒否しました: " + name); continue; }
                    if (dependencies.ContainsKey(relative)) continue;
                    try
                    {
                        if (dependencies.Count >= 32) throw new InvalidDataException("MTLは32ファイルまでです。");
                        byte[] mtl = document.ReadFile(relative, 4 * 1024 * 1024, token);
                        if (dependencies.Values.Sum(b => (long)b.Length) + mtl.Length > 32 * 1024 * 1024) throw new InvalidDataException("MTLの合計上限32 MiBを超えています。");
                        dependencies.Add(relative, mtl); document.ReadMtl(relative, mtl);
                    }
                    catch (Exception ex) when (FileFailure(ex)) { document.Notes.Add("MTL「" + name + "」を読み込めません: " + ex.Message); }
                }
            }
            // Rewrite only mtllib into canonical virtual paths; handle quoted Unicode filenames too.
            text = Regex.Replace(text, @"(?m)^\s*mtllib\s+([^\r\n]+)", m =>
            {
                string reference = m.Groups[1].Value.Trim();
                string[] names = document.Exists(reference) ? [reference] : Tokens(reference);
                return "mtllib " + string.Join(" ", names.Select(n => document.Resolve("", n)).Where(n => n != null && dependencies.ContainsKey(n)).Select(n => '"' + n + '"'));
            }, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            // VirtualModelIO understands whole names containing spaces; multiple MTL aliases are
            // resolved below by its token parser.
            bytes = Encoding.UTF8.GetBytes(text);
        }
        document.Input = new("model" + ext, bytes, dependencies);
        return document;
    }
    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static string[] Tokens(string text) => Matches(text, "\"([^\"]+)\"|'([^']+)'|([^\\s]+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToArray();
    private bool Exists(string reference) { string? p = Resolve("", reference.Trim().Trim('"')); return p != null && File.Exists(Path.Combine(root, p)); }
    private string? Resolve(string directory, string reference)
    {
        reference = reference.Trim().Trim('"').Replace('\\', '/');
        string? relative = VirtualModelIO.Resolve(directory, reference);
        if (relative == null) return null;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        string current = root;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return null;
        foreach (string part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
        }
        return relative;
    }
    private byte[] ReadFile(string reference, int maximum, CancellationToken token)
    {
        string relative = Resolve("", reference) ?? throw new InvalidDataException("フォルダー外またはリンクの参照は読み込みません。");
        if (Files.TryGetValue(relative, out var existing)) return existing;
        using var stream = new FileStream(Path.Combine(root, relative), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException($"ファイルの読み込み上限（{maximum / 1024 / 1024} MiB）を超えています。");
        byte[] bytes = new byte[(int)stream.Length];
        for (int offset = 0; offset < bytes.Length; ) { token.ThrowIfCancellationRequested(); int n = stream.Read(bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset))); if (n == 0) throw new EndOfStreamException(); offset += n; }
        Files.Add(relative, bytes);
        var scan = SecurityScanner.Scan(relative, bytes[..Math.Min(bytes.Length, 2 * 1024 * 1024)], bytes.Length); Findings.AddRange(scan.Findings);
        if (Path.GetExtension(relative).ToLowerInvariant() is ".obj" or ".mtl" || (Path.GetExtension(relative).Equals(".fbx", StringComparison.OrdinalIgnoreCase) && !bytes.AsSpan().StartsWith("Kaydara FBX Binary"u8)))
            Findings.AddRange(SecurityScanner.Scan(relative + ".txt", bytes[..Math.Min(bytes.Length, 2 * 1024 * 1024)], bytes.Length).Findings.Select(f => f with { Path = relative }));
        return bytes;
    }
    private void ReadMtl(string path, byte[] bytes)
    {
        string? material = null; string directory = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        foreach (string line in TextDecoder.Decode(bytes, false, out _).Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*(newmtl|map_Kd)\s+(.+?)\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) continue;
            if (match.Groups[1].Value == "newmtl") { material = match.Groups[2].Value.Trim(); continue; }
            if (material == null) continue;
            string name = match.Groups[2].Value.Trim();
            // Common MTL map options precede the filename. Prefer the complete path (spaces).
            if (name.StartsWith('-'))
            {
                string[] parts = Tokens(name); int i = 0;
                while (i < parts.Length && parts[i].StartsWith('-'))
                {
                    string option = parts[i++]; int count = option is "-mm" ? 2 : option is "-o" or "-s" or "-t" ? 3 : 1;
                    for (int n = 0; n < count && i < parts.Length; n++) { if (option is "-o" or "-s" or "-t" && !float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) break; i++; }
                }
                name = string.Join(' ', parts.Skip(i));
            }
            string? target = Resolve(directory, name);
            if (textureByMaterial.TryGetValue(material, out var prior) && prior != target) { textureByMaterial[material] = null; Notes.Add("材質「" + material + "」のMTL参照が競合するため、基本色で表示します。"); }
            else textureByMaterial[material] = target;
        }
    }
    public void ReadTextures(ModelPreviewData model, CancellationToken token)
    {
        foreach (var mesh in model.Meshes)
        {
            token.ThrowIfCancellationRequested();
            if (model.EmbeddedTextures.ContainsKey(mesh.TexturePath)) continue;
            string? path = textureByMaterial.TryGetValue(mesh.MaterialName, out var mapped) ? mapped : Resolve("", mesh.TexturePath);
            if (mesh.TexturePath.Length == 0 && !textureByMaterial.ContainsKey(mesh.MaterialName)) continue;
            textureFiles[mesh.MaterialName] = path;
            try
            {
                if (path == null) throw new InvalidDataException("フォルダー外、競合、または不正な参照です。");
                if (!ImagePreviewDecoder.Supports(path)) throw new NotSupportedException("画像形式が未対応です。");
                if (!Files.ContainsKey(path))
                {
                    if (textureFiles.Count > 128 || textureBytes + new FileInfo(Path.Combine(root, path)).Length > 128L * 1024 * 1024) throw new InvalidDataException("参照画像の総量上限（128 MiB / 128件）を超えています。");
                    byte[] bytes = ReadFile(path, 64 * 1024 * 1024, token); textureBytes += bytes.Length;
                }
            }
            catch (Exception ex) when (FileFailure(ex)) { textureFiles[mesh.MaterialName] = null; Notes.Add($"材質「{mesh.MaterialName}」: テクスチャを読み込めません（基本色で表示）。{ex.Message}"); }
        }
    }
    public string CacheKey()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("kwta-local-v0.13.0-1:" + SourcePath));
        foreach (var (path, bytes) in Files.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        { hash.AppendData(Encoding.UTF8.GetBytes(path + "\0")); hash.AppendData(SHA256.HashData(bytes)); }
        foreach (string note in Notes.Distinct().Order(StringComparer.Ordinal)) hash.AppendData(Encoding.UTF8.GetBytes(note + "\0"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public ModelMaterialResult Materials(ModelPreviewData model, CancellationToken token)
    {
        var materials = new Dictionary<string, Material>(StringComparer.Ordinal); var images = new Dictionary<string, BitmapSource>(StringComparer.Ordinal); var notes = new List<string>(); long pixels = 0;
        foreach (var mesh in model.Meshes)
        {
            token.ThrowIfCancellationRequested();
            if (materials.ContainsKey(mesh.MaterialName)) continue;
            string? key = model.EmbeddedTextures.ContainsKey(mesh.TexturePath) ? "embedded:" + mesh.TexturePath : textureFiles.GetValueOrDefault(mesh.MaterialName);
            if (key == null) continue;
            try
            {
                if (!images.TryGetValue(key, out var bitmap))
                {
                    if (key.StartsWith("embedded:", StringComparison.Ordinal))
                    {
                        var texture = model.EmbeddedTextures[mesh.TexturePath];
                        if (texture.Width == 0) bitmap = ImagePreviewDecoder.Decode(texture.Bytes, ImageExtension(texture.Bytes));
                        else { bitmap = BitmapSource.Create(texture.Width, texture.Height, 96, 96, PixelFormats.Bgra32, null, texture.Bytes, texture.Width * 4); bitmap.Freeze(); }
                    }
                    else bitmap = ImagePreviewDecoder.Decode(Files[key], key);
                    double scale = Math.Min(1, 2048d / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
                    BitmapSource resized = scale < 1 ? new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)) : bitmap;
                    long count = (long)resized.PixelWidth * resized.PixelHeight;
                    if (pixels + count > 32L * 1024 * 1024) throw new InvalidDataException("テクスチャ総画素上限を超えています。");
                    int stride = resized.PixelWidth * 4; byte[] detached = new byte[stride * resized.PixelHeight]; resized.CopyPixels(detached, stride, 0);
                    bitmap = BitmapSource.Create(resized.PixelWidth, resized.PixelHeight, 96, 96, PixelFormats.Bgra32, null, detached, stride); bitmap.Freeze(); images.Add(key, bitmap); pixels += count;
                }
                var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new System.Windows.Rect(0, 0, 1, 1) }; brush.Freeze();
                var material = new DiffuseMaterial(brush); material.Freeze(); materials.Add(mesh.MaterialName, material);
            }
            catch (Exception ex) when (FileFailure(ex) || ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { notes.Add($"材質「{mesh.MaterialName}」: 画像を表示できません（基本色で表示）。{ex.Message}"); }
        }
        return new(materials, notes.Distinct().Take(100).ToArray());
    }
    private static string ImageExtension(byte[] b) => b.AsSpan().StartsWith(new byte[] {137,80,78,71}) ? ".png" : b.AsSpan().StartsWith(new byte[] {255,216,255}) ? ".jpg" : b.AsSpan().StartsWith("BM"u8) ? ".bmp" : b.AsSpan().StartsWith("GIF8"u8) ? ".gif" : b.AsSpan().StartsWith(new byte[]{0,0,1,0}) ? ".ico" : ".tiff";
    private static bool FileFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
