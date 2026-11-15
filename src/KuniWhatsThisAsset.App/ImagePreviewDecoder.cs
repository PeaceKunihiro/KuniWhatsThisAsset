using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace KuniWhatsThisAsset.App;

public static class ImagePreviewDecoder
{
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico";
    public static BitmapSource Decode(byte[] bytes, string path)
    {
        if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("画像の入力上限（64 MiB）を超えています。");
        using var stream = new MemoryStream(bytes, writable: false);
        const BitmapCreateOptions options = BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile;
        BitmapDecoder decoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => new PngBitmapDecoder(stream, options, BitmapCacheOption.None),
            ".jpg" or ".jpeg" => new JpegBitmapDecoder(stream, options, BitmapCacheOption.None),
            ".bmp" => new BmpBitmapDecoder(stream, options, BitmapCacheOption.None),
            ".gif" => new GifBitmapDecoder(stream, options, BitmapCacheOption.None),
            ".tif" or ".tiff" => new TiffBitmapDecoder(stream, options, BitmapCacheOption.None),
            ".ico" => new IconBitmapDecoder(stream, options, BitmapCacheOption.None),
            _ => throw new NotSupportedException("この画像形式は未対応です。")
        };
        var frame = decoder.Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > 16_000_000)
            throw new InvalidDataException("画像プレビューの寸法上限（各辺16,384・合計1,600万画素）を超えています。");
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
