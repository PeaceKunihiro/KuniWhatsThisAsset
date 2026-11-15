using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;

internal static class ModelMaterialChecks
{
    public static void Run()
    {
        string file = Path.GetFullPath("artifacts/test-packages/materials.unitypackage");
        string modelGuid = new('a', 32), materialGuid = new('b', 32), textureGuid = new('c', 32), maskGuid = new('d', 32);
        byte[] Png(byte[] pixels)
        {
            var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var output = new MemoryStream(); png.Save(output); return output.ToArray();
        }
        string metadata = $"fileFormatVersion: 2\nModelImporter:\n  externalObjects:\n  - first:\n      type: UnityEngine:Material\n      name: SourceName\n    second: {{fileID: 2100000, guid: {materialGuid}, type: 2}}\n";
        string material = $"Material:\n  m_Name: DifferentName\n  m_SavedProperties:\n    m_TexEnvs:\n    - _MainTex:\n        m_Texture: {{fileID: 2800000, guid: {textureGuid}, type: 3}}\n    - _AlphaMask:\n        m_Texture: {{fileID: 2800000, guid: {maskGuid}, type: 3}}\n    m_Floats:\n    - _AlphaMaskMode: 1\n    - _Cutoff: 0.5\n    m_Colors:\n    - _Color: {{r: 1, g: 1, b: 1, a: 1}}\n";
        using (var gzip = new GZipStream(File.Create(file), CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar))
        {
            void Add(string guid, string path, byte[] bytes, string meta = "fileFormatVersion: 2\n")
            {
                foreach (var (name, data) in new[] { ("pathname", Encoding.UTF8.GetBytes(path)), ("asset", bytes), ("asset.meta", Encoding.UTF8.GetBytes(meta)) })
                { using var input = new MemoryStream(data); tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, guid + "/" + name) { DataStream = input }); }
            }
            Add(modelGuid, "Assets/model.fbx", [1], metadata);
            Add(materialGuid, "Assets/Variants/DifferentName.mat", Encoding.UTF8.GetBytes(material));
            Add(textureGuid, "Assets/main.png", Png([0, 0, 255, 255, 255, 0, 0, 255]));
            Add(maskGuid, "Assets/mask.png", Png([255, 255, 255, 255, 0, 0, 0, 255]));
        }
        var reader = new PackageReader(); var report = reader.Read(file);
        var captured = reader.ReadAssetSet(file, [modelGuid, materialGuid], metadata: true);
        if (captured.Count != 2 || Encoding.UTF8.GetString(captured[modelGuid]) != metadata) throw new Exception("Bulk meta capture failed");
        var data = new ModelPreviewData(new(3, 1, 1, 1, 0, false), [new([0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 1, 2], [1, 1, 1, 1], [0, 0, 1, 0, 0, 1], "SourceName")], []);
        var materials = ModelMaterials.Load(reader, report, report.Items.Single(i => i.Guid == modelGuid), metadata, data, CancellationToken.None);
        var mapped = (MaterialGroup)materials["SourceName"];
        var source = (BitmapSource)((ImageBrush)((DiffuseMaterial)mapped.Children[0]).Brush).ImageSource;
        var result = new byte[8]; source.CopyPixels(result, 8, 0);
        if (!mapped.IsFrozen || result[2] != 255 || result[3] != 255 || result[7] != 0) throw new Exception("Material remap, texture color or alpha mask incorrect");
        Console.WriteLine("PASS bulk asset/meta capture, Unity material remap, texture color and alpha-mask cutout");
    }
}
