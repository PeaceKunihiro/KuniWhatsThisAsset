using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;

// Reads only known scalar/texture fields. Unity scripts and shaders are never executed.
public static class ModelMaterials
{
    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static string Decode(byte[] bytes) => TextDecoder.Decode(bytes, false, out _);
    public static Dictionary<string, Material> Load(PackageReader reader, PackageReport report, PackageItem modelItem, string metadata, ModelPreviewData model, CancellationToken token)
    {
        var names = model.Meshes.Select(m => m.MaterialName).Distinct(StringComparer.Ordinal).ToArray();
        var remaps = Matches(metadata, @"(?m)^\s+name: ([^\r\n]+)\r?\n\s+second: \{fileID: 2100000, guid: ([0-9a-fA-F]{32})")
            .GroupBy(m => m.Groups[1].Value.Trim()).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
        string folder = modelItem.Path[..Math.Max(0, modelItem.Path.LastIndexOf('/'))] + "/";
        var selected = new Dictionary<string, PackageItem>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            var item = remaps.TryGetValue(name, out var guid) ? report.Items.FirstOrDefault(i => i.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase) && i.Format == ".mat") : null;
            // Name fallback is restricted to the model's own directory tree; ambiguous variants are not guessed.
            if (item == null && !remaps.ContainsKey(name))
            {
                var candidates = report.Items.Where(i => i.Format == ".mat" && i.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(i.Path) == name).ToArray();
                if (candidates.Length == 1) item = candidates[0];
            }
            if (item != null) selected[name] = item;
        }
        var materialBytes = reader.ReadAssetSet(report.SourcePath, selected.Values.Select(i => i.Guid).Distinct().ToArray(), maxFileBytes: 2 * 1024 * 1024, maxTotalBytes: 16 * 1024 * 1024, cancellationToken: token);
        var descriptions = new Dictionary<string, Description>(StringComparer.Ordinal);
        foreach (var (name, item) in selected)
            if (materialBytes.TryGetValue(item.Guid, out var bytes)) descriptions[name] = Parse(Decode(bytes));
        var shaderIds = materialBytes.Values.SelectMany(b => Matches(Decode(b), @"m_Shader: \{[^}\r\n]*guid: ([0-9a-fA-F]{32})").Select(m => m.Groups[1].Value)).Distinct().ToArray();
        var shaders = reader.ReadAssetSet(report.SourcePath, report.Items.Where(i => i.Format == ".shader" && shaderIds.Contains(i.Guid)).Select(i => i.Guid).ToArray(), maxFileBytes: 2 * 1024 * 1024, maxTotalBytes: 16 * 1024 * 1024, cancellationToken: token);
        foreach (var (name, item) in selected)
        {
            if (!materialBytes.TryGetValue(item.Guid, out var bytes) || !descriptions.TryGetValue(name, out var description)) continue;
            string shader = Matches(Decode(bytes), @"m_Shader: \{[^}\r\n]*guid: ([0-9a-fA-F]{32})").Cast<Match>().FirstOrDefault()?.Groups[1].Value ?? "";
            if (shaders.TryGetValue(shader, out var source) && Regex.IsMatch(Decode(source), "\"RenderType\"\\s*=\\s*\"Transparent(?:Cutout)?\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) descriptions[name] = description with { Transparent = true };
        }
        var textureIds = descriptions.Values.SelectMany(d => new[] { d.Texture, d.Mask }).Where(g => g.Length == 32).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var textures = report.Items.Where(i => textureIds.Contains(i.Guid, StringComparer.OrdinalIgnoreCase) && ImagePreviewDecoder.Supports(i.Path)).ToDictionary(i => i.Guid, StringComparer.OrdinalIgnoreCase);
        var imageBytes = reader.ReadAssetSet(report.SourcePath, textures.Keys.ToArray(), maxTotalBytes: 256L * 1024 * 1024, cancellationToken: token);
        var decoded = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        long decodedPixels = 0;
        BitmapSource? Image(string guid)
        {
            if (decoded.TryGetValue(guid, out var cached)) return cached;
            if (!textures.TryGetValue(guid, out var item) || !imageBytes.TryGetValue(guid, out var bytes)) return null;
            try
            {
                token.ThrowIfCancellationRequested();
                var source = ImagePreviewDecoder.Decode(bytes, item.Path);
                double scale = Math.Min(1, 2048d / Math.Max(source.PixelWidth, source.PixelHeight));
                BitmapSource resized = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
                long count = (long)resized.PixelWidth * resized.PixelHeight;
                if (decodedPixels + count > 32L * 1024 * 1024) return null;
                // Detach the resized pixels so the original 4K decoding buffer can be collected.
                int stride = resized.PixelWidth * 4; var pixels = new byte[stride * resized.PixelHeight];
                resized.CopyPixels(pixels, stride, 0);
                var result = BitmapSource.Create(resized.PixelWidth, resized.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride); result.Freeze();
                decodedPixels += count; decoded[guid] = result; return result;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
        }
        var output = new Dictionary<string, Material>(StringComparer.Ordinal);
        foreach (var (name, description) in descriptions)
        {
            token.ThrowIfCancellationRequested();
            var bitmap = Image(description.Texture);
            Brush brush;
            if (bitmap == null) brush = new SolidColorBrush(description.Color);
            else
            {
                int w = bitmap.PixelWidth, h = bitmap.PixelHeight, stride = w * 4;
                var pixels = new byte[stride * h]; bitmap.CopyPixels(pixels, stride, 0);
                var mask = description.MaskMode > 0 ? Image(description.Mask) : null;
                byte[]? maskPixels = null;
                if (mask != null)
                {
                    var resized = new TransformedBitmap(mask, new ScaleTransform((double)w / mask.PixelWidth, (double)h / mask.PixelHeight));
                    maskPixels = new byte[pixels.Length]; resized.CopyPixels(maskPixels, stride, 0);
                }
                for (int p = 0; p < pixels.Length; p += 4)
                {
                    pixels[p] = (byte)(pixels[p] * description.Color.B / 255); pixels[p + 1] = (byte)(pixels[p + 1] * description.Color.G / 255); pixels[p + 2] = (byte)(pixels[p + 2] * description.Color.R / 255);
                    float alpha = pixels[p + 3] / 255f * description.Color.A / 255;
                    if (maskPixels != null) alpha = description.MaskMode == 1 ? maskPixels[p + 2] / 255f : alpha * maskPixels[p + 2] / 255f;
                    pixels[p + 3] = description.Transparent ? (byte)(alpha < description.Cutoff ? 0 : 255) : (byte)255;
                }
                var colored = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride); colored.Freeze();
                brush = new ImageBrush(colored) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill };
            }
            brush.Freeze();
            var material = new MaterialGroup(); material.Children.Add(new DiffuseMaterial(brush));
            material.Freeze(); output[name] = material;
        }
        return output;
    }
    private sealed record Description(string Texture, string Mask, Color Color, bool Transparent, float Cutoff, int MaskMode);
    private static Description Parse(string text)
    {
        string Texture(string key) => Matches(text, @"(?m)^\s*- " + Regex.Escape(key) + @":\s*\r?\n\s+m_Texture: \{[^}\r\n]*guid: ([0-9a-fA-F]{32})").Cast<Match>().FirstOrDefault()?.Groups[1].Value ?? "";
        float Number(string key, float fallback) => float.TryParse(Matches(text, @"(?m)^\s*- " + Regex.Escape(key) + @": ([-+0-9.eE]+)\s*$").Cast<Match>().FirstOrDefault()?.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && float.IsFinite(n) ? n : fallback;
        var colorMatch = Matches(text, @"(?m)^\s*- _Color: \{r: ([-+0-9.eE]+), g: ([-+0-9.eE]+), b: ([-+0-9.eE]+), a: ([-+0-9.eE]+)\}").Cast<Match>().FirstOrDefault();
        byte Channel(int i) => colorMatch != null && float.TryParse(colorMatch.Groups[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) && float.IsFinite(n) ? (byte)(Math.Clamp(n, 0, 1) * 255) : (byte)255;
        string texture = new[] { Texture("_MainTex"), Texture("_BaseMap"), Texture("_BaseColorMap") }.FirstOrDefault(t => t.Length > 0) ?? "";
        bool transparent = text.Contains("RenderType: Transparent", StringComparison.Ordinal) || text.Contains("RenderType: TransparentCutout", StringComparison.Ordinal) || Number("_Mode", 0) > 0 || Number("_AlphaMaskMode", 0) > 0 || Number("_RenderingMode", 0) > 0;
        return new(texture, Texture("_AlphaMask"), Color.FromArgb(Channel(4), Channel(1), Channel(2), Channel(3)), transparent, Math.Clamp(Number("_Cutoff", .5f), .01f, .99f), (int)Math.Clamp(Number("_AlphaMaskMode", 0), 0, 4));
    }
}
