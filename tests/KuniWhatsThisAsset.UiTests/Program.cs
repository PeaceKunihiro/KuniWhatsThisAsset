using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new App(); app.InitializeComponent();
        var main = new MainWindow();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        object? Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(main, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(main)!;
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
        try
        {
            main.Show(); main.UpdateLayout();
            var notice = Field<Expander>("SecurityNotice");
            var host = Field<ContentControl>("WarningContentHost");
            var grid = Field<DataGrid>("FindingsGrid");
            var splitter = Field<GridSplitter>("WarningSplitter");
            Invoke("Notify", (object)new Finding[] { new(Severity.Warning, "TEST", "test", "warning") });
            Check(notice.Visibility == Visibility.Visible && !notice.IsExpanded, "Notification must be passive");
            notice.IsExpanded = true; main.UpdateLayout();
            var row = Field<RowDefinition>("WarningRow");
            Check(splitter.Visibility == Visibility.Visible && row.ActualHeight >= 90, "Resizable row missing");
            row.Height = new GridLength(270); main.UpdateLayout();
            Check(row.ActualHeight >= 260, "Warning row did not resize");
            for (int cycle = 0; cycle < 2; cycle++)
            {
                Invoke("DetachWarnings_Click", main, new RoutedEventArgs(Button.ClickEvent));
                var detached = Field<Window>("warningWindow"); detached.UpdateLayout();
                Check(detached.IsVisible && host.Content == null && splitter.Visibility == Visibility.Collapsed, "Detach failed");
                Check(grid.Items.Count == 1 && grid.IsVisible, "Detached warnings lost");
                Invoke("Notify", (object)Array.Empty<Finding>());
                Check(!detached.IsVisible && notice.Visibility == Visibility.Collapsed, "Normal results must hide warnings");
                Invoke("Notify", (object)new Finding[] { new(Severity.Warning, "TEST", "test", "updated") });
                Check(detached.IsVisible && grid.Items.Count == 1, "Detached results not updated");
                detached.Close(); main.UpdateLayout();
                Check(host.Content == Field<Grid>("WarningContent") && notice.IsExpanded, "Redock failed");
            }
            Invoke("Notify", (object)Array.Empty<Finding>()); main.UpdateLayout();
            Check(notice.Visibility == Visibility.Collapsed && splitter.Visibility == Visibility.Collapsed, "Normal state leaves warning space");
            Console.WriteLine("PASS warning resize, repeated detach/redock, shared results, passive visibility");
            var pixels = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 };
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 8);
            foreach (var format in new[] { ".png", ".jpg", ".bmp", ".gif", ".tiff" })
            {
                System.Windows.Media.Imaging.BitmapEncoder encoder = format switch
                {
                    ".png" => new System.Windows.Media.Imaging.PngBitmapEncoder(),
                    ".jpg" => new System.Windows.Media.Imaging.JpegBitmapEncoder(),
                    ".bmp" => new System.Windows.Media.Imaging.BmpBitmapEncoder(),
                    ".gif" => new System.Windows.Media.Imaging.GifBitmapEncoder(),
                    _ => new System.Windows.Media.Imaging.TiffBitmapEncoder()
                };
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = new System.IO.MemoryStream(); encoder.Save(stream);
                var decoded = ImagePreviewDecoder.Decode(stream.ToArray(), "test" + format);
                Check(decoded.PixelWidth == 2 && decoded.PixelHeight == 1 && decoded.IsFrozen, "Image decode failed: " + format);
                var readPixels = new byte[8]; decoded.CopyPixels(readPixels, 8, 0);
                if (format == ".png") Check(readPixels.SequenceEqual(pixels), "PNG pixel mismatch");
            }
            var ico = ImagePreviewDecoder.Decode(System.IO.File.ReadAllBytes("IMGs/KWTA_icon.ico"), "test.ico");
            Check(ico.PixelWidth > 0, "ICO decode failed");
            bool refused = false;
            try { ImagePreviewDecoder.Decode(new byte[] { 1, 2, 3 }, "bad.png"); }
            catch (Exception ex) when (ex is System.IO.IOException or FormatException or NotSupportedException or ArgumentException) { refused = true; }
            Check(refused, "Broken image accepted");
            Console.WriteLine("PASS PNG/JPEG/BMP/GIF/TIFF/ICO decode, PNG pixel fidelity and corrupt image refusal");
            var pngEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            pngEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var pngStream = new System.IO.MemoryStream(); pngEncoder.Save(pngStream);
            string packageFile = System.IO.Path.GetFullPath("artifacts/test-packages/ui-image.unitypackage");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(packageFile)!);
            string imageGuid = new string('d', 32);
            using (var fs = System.IO.File.Create(packageFile))
            using (var zip = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
            using (var tar = new System.Formats.Tar.TarWriter(zip, System.Formats.Tar.TarEntryFormat.Ustar))
            {
                foreach (var member in new[] { ("asset", pngStream.ToArray()), ("pathname", System.Text.Encoding.UTF8.GetBytes("Assets/test.png")), ("asset.meta", System.Text.Encoding.UTF8.GetBytes("fileFormatVersion: 2\n")) })
                {
                    using var content = new System.IO.MemoryStream(member.Item2);
                    tar.WriteEntry(new System.Formats.Tar.UstarTarEntry(System.Formats.Tar.TarEntryType.RegularFile, imageGuid + "/" + member.Item1) { DataStream = content });
                }
            }
            var imageReport = new PackageReader().Read(packageFile);
            typeof(MainWindow).GetField("report", flags)!.SetValue(main, imageReport);
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            Invoke("ShowItem", imageReport.Items[0]);
            var imageView = Field<Image>("ImagePreview");
            var frame = new System.Windows.Threading.DispatcherFrame();
            var started = DateTime.UtcNow;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (imageView.Source != null || DateTime.UtcNow - started > TimeSpan.FromSeconds(10)) frame.Continue = false; };
            timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop();
            Check(imageView.Source != null && imageView.Visibility == Visibility.Visible, "Selected package image not displayed");
            Invoke("ShowItem", new PackageItem("text", "Assets/test.txt", 4, false, Array.Empty<Finding>(), "説明文"));
            Check(imageView.Source == null && imageView.Visibility == Visibility.Collapsed && Field<TextBox>("Preview").Text == "説明文", "Switch from image to text failed");
            Console.WriteLine("PASS package selection -> image preview -> text selection");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { main.Close(); app.Shutdown(); }
    }
}



