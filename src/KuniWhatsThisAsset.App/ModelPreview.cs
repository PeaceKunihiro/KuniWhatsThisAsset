using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public partial class MainWindow
{
    private CancellationTokenSource? modelCancellation;
    private void StopModelPreview()
    {
        modelCancellation?.Cancel(); modelCancellation = null;
        ModelView.ClearModel(); ModelView.Visibility = Visibility.Collapsed;
    }
    private async Task ShowModelAsync(PackageItem item, PackageReport source, ScanLimits limits, Func<ModelStatistics, bool>? confirm = null)
    {
        using var cts = new CancellationTokenSource(); modelCancellation = cts;
        PreviewHint.Text = "モデルを解析中…";
        try
        {
            string metadataText = "";
            var input = await Task.Run(() =>
            {
                var reader = new PackageReader(limits);
                byte[] bytes = reader.ReadAssetBytes(source.SourcePath, item.Guid, 256 * 1024 * 1024, cts.Token);
                var dependencies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                if (item.Format == ".obj")
                {
                    string text = TextDecoder.Decode(bytes, false, out _);
                    string directory = item.Path.Contains('/') ? item.Path[..item.Path.LastIndexOf('/')] : "";
                    long total = 0;
                    foreach (Match match in Regex.Matches(text, @"(?m)^\s*mtllib\s+([^\r\n]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    {
                        string reference = match.Groups[1].Value.Trim();
                        var references = new[] { reference }.Concat(reference.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                        foreach (string name in references.Distinct())
                        {
                            string? path = VirtualModelIO.Resolve(directory, name);
                            if (path == null || dependencies.ContainsKey(path)) continue;
                            var dependency = source.Items.FirstOrDefault(i => !i.IsDirectory && i.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && i.Format == ".mtl");
                            if (dependency == null) continue;
                            if (dependencies.Count >= 32) throw new InvalidDataException("MTLファイルが多すぎます。");
                            byte[] content = reader.ReadAssetBytes(source.SourcePath, dependency.Guid, 4 * 1024 * 1024, cts.Token);
                            total += content.Length; if (total > 32 * 1024 * 1024) throw new InvalidDataException("MTLの総サイズが大きすぎます。");
                            dependencies[path] = content;
                        }
                    }
                }
                int referencedMaterialCount = 0;
                if (!item.Findings.Any(f => f.Code == "META_MISSING"))
                {
                    var metadata = reader.ReadAssetMetaBytes(source.SourcePath, item.Guid, cts.Token);
                    var text = TextDecoder.Decode(metadata, false, out _);
                    metadataText = text;
                    var materialGuids = source.Items.Where(i => i.Format == ".mat").Select(i => i.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var resolvedGuids = Regex.Matches(text, @"\bguid:\s*([0-9a-fA-F]{32})\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                                                .Select(m => m.Groups[1].Value).Where(materialGuids.Contains);
                    var declaredGuids = Regex.Matches(text, @"\bfileID:\s*2100000\s*,\s*guid:\s*([0-9a-fA-F]{32})\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Select(m => m.Groups[1].Value);
                    referencedMaterialCount = resolvedGuids.Concat(declaredGuids).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                }
                return new ModelInput(item.Path, bytes, dependencies, referencedMaterialCount);
            }, cts.Token);
            var model = await ModelWorker.ImportAsync(input, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            var stats = model.Statistics;
            bool approved = ModelRenderPolicy.Approve(stats, confirm ?? (s => MessageBox.Show(this,
                $"このモデルのレンダリングは高負荷になる可能性があります。\n\nポリゴン数（三角形換算）: {s.TriangleCount:N0}\n依存マテリアル数: {s.MaterialCount:N0}\n\n警告条件：100,000ポリゴン以上、またはマテリアル10以上。\nレンダリングを開始しますか？",
                "モデル描画の負荷確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes));
            cts.Token.ThrowIfCancellationRequested();
            if (!approved) { PreviewHint.Text = "モデルのレンダリングを中止しました。"; return; }
            // No WPF mesh or scene is created until the load warning has been approved.
            PreviewHint.Text = "テクスチャを読み込み中…";
            var materials = await Task.Run(() => ModelMaterials.Load(new PackageReader(limits), source, item, metadataText, model, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            ModelView.SetModel(model, materials); ModelView.Visibility = Visibility.Visible; PreviewHint.Text = "";
            Status.Text = $"{item.Name} · {stats.TriangleCount:N0}ポリゴン（三角形） · Material {stats.MaterialCount:N0}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            if (!cts.IsCancellationRequested) PreviewHint.Text = "モデルを表示できません。\n" + ex.Message;
        }
        finally { if (ReferenceEquals(modelCancellation, cts)) modelCancellation = null; }
    }
}


