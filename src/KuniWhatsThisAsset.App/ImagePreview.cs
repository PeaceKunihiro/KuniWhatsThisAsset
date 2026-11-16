using System.IO;
using System.Windows;
using KuniWhatsThisAsset.Core;
namespace KuniWhatsThisAsset.App;
public partial class MainWindow
{
    private ScanLimits activeLimits = new();
    private CancellationTokenSource? imageCancellation;
    private void StopImagePreview()
    {
        imageCancellation?.Cancel();
        imageCancellation = null;
        ImagePreview.Source = null;
        ImagePreview.Visibility = Visibility.Collapsed;
    }
    private async Task ShowImageAsync(PackageItem item, PackageReport source, ScanLimits limits)
    {
        ClearPreviewFindings();
        using var cts = new CancellationTokenSource();
        imageCancellation = cts;
        PreviewHint.Text = "画像を読み込み中…";
        try
        {
            var bitmap = await Task.Run(() =>
            {
                byte[] bytes = new PackageReader(limits).ReadAssetBytes(source.SourcePath, item.Guid, cancellationToken: cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                var result = ImagePreviewDecoder.Decode(bytes, item.Path);
                cts.Token.ThrowIfCancellationRequested();
                return result;
            });
            if (cts.IsCancellationRequested) return;
            ImagePreview.Source = bitmap;
            ImagePreview.Visibility = Visibility.Visible;
            PreviewHint.Text = "";
            Status.Text = $"{item.Name} · {bitmap.PixelWidth} × {bitmap.PixelHeight} px · {item.SizeLabel}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or InvalidDataException or NotSupportedException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            if (!cts.IsCancellationRequested)
            {
                AddPreviewFailure(item, "PREVIEW_IMAGE_FAILED", ex.Message);
                PreviewHint.Text = "画像を表示できません。警告欄を確認してください。";
            }
        }
        finally { if (ReferenceEquals(imageCancellation, cts)) imageCancellation = null; }
    }
}

