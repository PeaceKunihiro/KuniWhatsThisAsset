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
        if (Environment.GetCommandLineArgs().Contains("--fuwatto-bindings")) return ReferenceChecks.Actual();
        if (Environment.GetCommandLineArgs().Contains("--published-update")) return PublishedUpdateChecks.Run();
        if (Environment.GetCommandLineArgs().Contains("--repackage-models")) return RealRepackageChecks.Run();
        if (Environment.GetCommandLineArgs().Contains("--real-models")) return RealModelChecks.Run();
        if (Environment.GetCommandLineArgs().Contains("--texture-models")) return TextureModelChecks.Run();
        if (Environment.GetCommandLineArgs().Contains("--fuwatto-analysis")) return FuwattoChecks.Run();
        if (Environment.GetCommandLineArgs().Contains("--model-ids")) return ModelIdProbe.Run();
        if (Environment.GetCommandLineArgs().Contains("--sharlen-models")) return SharlenChecks.Actual();
        if (Environment.GetCommandLineArgs().Contains("--sharlen-probe")) return SharlenChecks.Probe();
        var app = new App(); app.InitializeComponent();
        var main = new MainWindow(System.IO.Path.GetFullPath("artifacts/ui-profile-" + Guid.NewGuid().ToString("N")));
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
            var licenses = LicenseWindow.LoadLicenses();
            Check(licenses.Count == 7 && licenses.All(l => l.Text.Length > 100), "License text missing");
            Check(licenses.Any(l => l.Text.Contains("MICROSOFT SOFTWARE LICENSE TERMS")), "Distribution license missing");
            var licenseWindow = new LicenseWindow { Owner = main };
            licenseWindow.Show();
            var list = (ListBox)licenseWindow.FindName("LicenseList");
            var licenseText = (TextBox)licenseWindow.FindName("LicenseText");
            for (int i = 0; i < licenses.Count; i++)
            {
                list.SelectedIndex = i;
                Check(licenseText.Text == licenses[i].Text && licenseText.IsReadOnly, "License dialog text mismatch");
            }
            licenseWindow.Close();
            Console.WriteLine("PASS embedded license originals, dialog selection and read-only display");
            string updateState = System.IO.Path.GetFullPath("artifacts/test-packages/ui-update-" + Guid.NewGuid() + ".json");
            int updateRequests = 0;
            using var updateClient = new System.Net.Http.HttpClient(new UiUpdateHandler(() => updateRequests++));
            var checker = new UpdateChecker(updateClient, new FileUpdateAttemptStore(updateState));
            var about = new AboutWindow(checker, new Version(0, 7, 0)) { Owner = main };
            about.Show(); about.UpdateLayout();
            var updatePanel = (StackPanel)about.FindName("UpdatePanel");
            var updateFrame = new System.Windows.Threading.DispatcherFrame();
            var updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            var updateStart = DateTime.UtcNow;
            updateTimer.Tick += (_, _) => { if (updatePanel.Visibility == Visibility.Visible || DateTime.UtcNow - updateStart > TimeSpan.FromSeconds(2)) updateFrame.Continue = false; };
            updateTimer.Start(); System.Windows.Threading.Dispatcher.PushFrame(updateFrame); updateTimer.Stop();
            Check(updatePanel.Visibility == Visibility.Visible && updateRequests == 1, "Update notification not shown");
            Check(((TextBlock)about.FindName("UpdateMessage")).Text.Contains("0.8.0"), "Wrong update version shown");
            Check(about.FindName("StableLink") != null, "Stable link missing");
            about.Close();
            var secondAbout = new AboutWindow(checker, new Version(0, 7, 0)) { Owner = main };
            secondAbout.Show(); secondAbout.UpdateLayout();
            Check(((StackPanel)secondAbout.FindName("UpdatePanel")).Visibility == Visibility.Collapsed && updateRequests == 1 && secondAbout.FindName("StableLink") != null, "Same-day dialog retried or notified");
            secondAbout.Close();
            Console.WriteLine("PASS About dialog new update display and same-day link-only fallback (mock HTTP)");
            var objBytes = System.Text.Encoding.UTF8.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
            var modelInput = new ModelInput("Assets/triangle.obj", objBytes, new Dictionary<string, byte[]>());
            var imported = ModelImporter.Import(modelInput);
            Check(imported.Statistics.TriangleCount == 1 && imported.Statistics.VertexCount == 3, "OBJ statistics wrong");
            var modelView = new ModelViewport { Width = 400, Height = 350 };
            modelView.SetModel(imported);
            Check(imported.Meshes[0].Positions.Max() == 1, "OBJ positions wrong");
            var workerModel = ModelWorker.ImportAsync(modelInput, CancellationToken.None).GetAwaiter().GetResult();
            Check(workerModel.Statistics.TriangleCount == 1, "Child process import failed");
            Console.WriteLine("PASS native OBJ import, WPF geometry and isolated worker roundtrip");
            var materialObj = new System.Text.StringBuilder("mtllib many.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\n");
            var materialMtl = new System.Text.StringBuilder();
            for (int i = 0; i < 10; i++) { materialObj.Append($"usemtl material{i}\nf 1 2 3\n"); materialMtl.Append($"newmtl material{i}\nKd 0.6 0.4 0.3\n"); }
            var materialsModel = ModelImporter.Import(new("Assets/many.obj", System.Text.Encoding.UTF8.GetBytes(materialObj.ToString()), new Dictionary<string, byte[]> { ["Assets/many.mtl"] = System.Text.Encoding.UTF8.GetBytes(materialMtl.ToString()) }));
            Check(materialsModel.Statistics.MaterialCount == 10 && materialsModel.Statistics.RequiresLoadWarning, "10 imported materials did not warn");
            var referenced = ModelImporter.Import(modelInput with { ReferencedMaterialCount = 10 });
            Check(referenced.Statistics.MaterialCount == 10 && referenced.Statistics.RequiresLoadWarning, "Unity material references did not warn");
            using (var virtualIO = new VirtualModelIO(modelInput))
            {
                foreach (string forbidden in new[] { "C:/private/model.mtl", "https://example.invalid/x", "//server/share/a", "../../outside" })
                    Check(virtualIO.OpenFile(forbidden, SharpAssimp.FileIOMode.ReadBinary) == null, "External reference accepted");
            }
            var exportScene = new SharpAssimp.Scene { RootNode = new SharpAssimp.Node("root") };
            var movedNode = new SharpAssimp.Node("translated", exportScene.RootNode) { Transform = System.Numerics.Matrix4x4.Transpose(System.Numerics.Matrix4x4.CreateTranslation(5, 0, 0)) };
            exportScene.RootNode.Children.Add(movedNode);
            var exportMesh = new SharpAssimp.Mesh(SharpAssimp.PrimitiveType.Triangle) { MaterialIndex = 0 };
            exportMesh.Vertices.AddRange(new[] { new System.Numerics.Vector3(0,0,0), new System.Numerics.Vector3(1,0,0), new System.Numerics.Vector3(0,1,0) });
            exportMesh.Faces.Add(new SharpAssimp.Face(new[] { 0, 1, 2 }));
            exportScene.Meshes.Add(exportMesh); exportScene.Materials.Add(new SharpAssimp.Material()); movedNode.MeshIndices.Add(0);
            using (var exporter = new SharpAssimp.AssimpContext())
            {
                foreach (string format in new[] { "fbx", "fbxa" })
                {
                    var fbxBytes = exporter.ExportToBlob(exportScene, format)?.Data ?? throw new Exception("FBX fixture export failed");
                    Check(fbxBytes.Length > 0, "FBX fixture export failed");
                    System.IO.File.WriteAllBytes("artifacts/test-packages/triangle-" + format + ".fbx", fbxBytes);
                    var fbx = ModelImporter.Import(new("Assets/model.fbx", fbxBytes, new Dictionary<string, byte[]>()));
                    Check(fbx.Statistics.TriangleCount == 1, "FBX import failed: " + format);
                    Check(Math.Abs(fbx.Meshes[0].Positions[0] - 5) < .01f, "FBX node translation incorrect: " + format + " x=" + fbx.Meshes[0].Positions[0]);
                }
            }
            Console.WriteLine("PASS material threshold, Unity references, external IO refusal, Binary/ASCII FBX with node transforms");
            string modelPackage = System.IO.Path.GetFullPath("artifacts/test-packages/ui-model.unitypackage");
            string modelGuid = new string('e', 32);
            void WriteModelPackage(byte[] asset, string? mtl = null, int materialReferences = 0)
            {
                using var fs = System.IO.File.Create(modelPackage);
                using var zip = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest);
                using var tar = new System.Formats.Tar.TarWriter(zip, System.Formats.Tar.TarEntryFormat.Ustar);
                string metadata = "fileFormatVersion: 2\n" + string.Join("\n", Enumerable.Range(1, materialReferences).Select(n => "  second: {fileID: 2100000, guid: " + n.ToString("x32") + ", type: 2}"));
                if (mtl != null)
                {
                    foreach (var entry in new[] { ("asset", mtl), ("pathname", "Assets/many.mtl"), ("asset.meta", "fileFormatVersion: 2\n") })
                    {
                        using var content = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(entry.Item2));
                        tar.WriteEntry(new System.Formats.Tar.UstarTarEntry(System.Formats.Tar.TarEntryType.RegularFile, new string('f', 32) + "/" + entry.Item1) { DataStream = content });
                    }
                }
                foreach (var member in new[] { ("asset", asset), ("pathname", System.Text.Encoding.UTF8.GetBytes("Assets/model.obj")), ("asset.meta", System.Text.Encoding.UTF8.GetBytes(metadata)) })
                {
                    using var stream = new System.IO.MemoryStream(member.Item2);
                    tar.WriteEntry(new System.Formats.Tar.UstarTarEntry(System.Formats.Tar.TarEntryType.RegularFile, modelGuid + "/" + member.Item1) { DataStream = stream });
                }
            }
            void PumpTask(Task task)
            {
                var waitFrame = new System.Windows.Threading.DispatcherFrame();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var waitTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                waitTimer.Tick += (_, _) => { if (task.IsCompleted || clock.Elapsed > TimeSpan.FromSeconds(20)) waitFrame.Continue = false; };
                waitTimer.Start(); System.Windows.Threading.Dispatcher.PushFrame(waitFrame); waitTimer.Stop();
                Check(task.IsCompleted, "Model preview timed out"); task.GetAwaiter().GetResult();
            }
            WriteModelPackage(objBytes);
            var modelReport = new PackageReader().Read(modelPackage);
            Invoke("StopModelPreview");
            var modelTask = (Task)Invoke("ShowModelAsync", modelReport.Items[0], modelReport, new ScanLimits(), new Func<ModelStatistics, bool>(_ => throw new Exception("Low model prompted")))!;
            PumpTask(modelTask);
            Check(Field<ModelViewport>("ModelView").Visibility == Visibility.Visible, "Package model not displayed");
            Invoke("StopModelPreview");
            byte[] highObj = System.Text.Encoding.UTF8.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\n" + string.Concat(Enumerable.Repeat("f 1 2 3\n", 100000)));
            WriteModelPackage(highObj);
            var highReport = new PackageReader().Read(modelPackage);
            int renderWarnings = 0;
            var highTask = (Task)Invoke("ShowModelAsync", highReport.Items[0], highReport, new ScanLimits(), new Func<ModelStatistics, bool>(s => { Check(s.TriangleCount == 100000, "Wrong polygon count in warning"); renderWarnings++; return false; }))!;
            PumpTask(highTask);
            Check(renderWarnings == 1 && Field<ModelViewport>("ModelView").Visibility == Visibility.Collapsed, "Declined high model rendered");
            Check(Field<TextBlock>("PreviewHint").Text.Contains("中止"), "Decline not reflected");
            Console.WriteLine("PASS package model preview and 100000-triangle warning before rendering with decline");
            bool corruptModelRejected = false;
            try { ModelWorker.ImportAsync(new("Assets/bad.fbx", new byte[] {1,2,3}, new Dictionary<string, byte[]>()), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (System.IO.InvalidDataException) { corruptModelRejected = true; }
            Check(corruptModelRejected, "Broken model not rejected by worker");
            var unicodeObj = ModelImporter.Import(modelInput with { Path = "Assets/モデル.obj" });
            Check(unicodeObj.Statistics.TriangleCount == 1, "Japanese model path failed");
            Console.WriteLine("PASS corrupt model worker refusal and Japanese virtual model path");
            Console.WriteLine("Assimp native: " + SharpAssimp.Unmanaged.AssimpLibrary.Instance.GetVersion());
            Invoke("StopModelPreview");
            WriteModelPackage(System.Text.Encoding.UTF8.GetBytes(materialObj.ToString()), materialMtl.ToString());
            var matReport = new PackageReader().Read(modelPackage);
            var modelItem = matReport.Items.Single(i => i.Format == ".obj");
            int materialPrompts = 0;
            var matTask = (Task)Invoke("ShowModelAsync", modelItem, matReport, new ScanLimits(), new Func<ModelStatistics, bool>(s => { Check(s.MaterialCount == 10, "Incorrect material threshold"); materialPrompts++; return true; }))!;
            PumpTask(matTask);
            Check(materialPrompts == 1 && Field<ModelViewport>("ModelView").Visibility == Visibility.Visible, "Approved 10-material model did not render");
            main.UpdateLayout();
            var renderedView = Field<ModelViewport>("ModelView");
            var previewCapture = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)renderedView.ActualWidth), Math.Max(1, (int)renderedView.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            previewCapture.Render(renderedView);
            var previewEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            previewEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(previewCapture));
            using (var captureFile = System.IO.File.Create("artifacts/model-preview-check.png")) previewEncoder.Save(captureFile);
            Invoke("StopModelPreview");
            WriteModelPackage(objBytes, materialReferences: 10);
            var metaReport = new PackageReader().Read(modelPackage);
            int metaPrompts = 0;
            var metaTask = (Task)Invoke("ShowModelAsync", metaReport.Items[0], metaReport, new ScanLimits(), new Func<ModelStatistics, bool>(s => { Check(s.MaterialCount == 10 && s.TriangleCount == 1, "Unity material refs not counted"); metaPrompts++; return false; }))!;
            PumpTask(metaTask);
            Check(metaPrompts == 1 && Field<ModelViewport>("ModelView").Visibility == Visibility.Collapsed, "Declined Unity-material model rendered");
            Console.WriteLine("PASS package MTL pre-render warning with approval and Unity meta references with refusal");
            ModelMaterialChecks.Run();
            MaterialResolverChecks.Run();
            FeatureChecks.Run(main); ReferenceChecks.Run(); BindPoseChecks.Run(); UpdateUiChecks.Run(main); RepackageUiChecks.Run(main);
            WarningIntegrationChecks.Run(main);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { main.Close(); app.Shutdown(); }
    }
}





internal sealed class UiUpdateHandler(Action onRequest) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        onRequest();
        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("[{\"type\":\"file\",\"name\":\"KuniWhatsThisAsset-v0.8.0-win-x64.zip\",\"download_url\":\"https://raw.githubusercontent.com/PeaceKunihiro/KuniWhatsThisAsset/master/Stable/KuniWhatsThisAsset-v0.8.0-win-x64.zip\"}]")
        });
    }
}









