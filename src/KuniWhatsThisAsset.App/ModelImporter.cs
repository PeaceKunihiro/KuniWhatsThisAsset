using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SharpAssimp;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;

public sealed record ModelInput(string Path, byte[] Bytes, Dictionary<string, byte[]> Dependencies, int ReferencedMaterialCount = 0);
public static class ModelImporter
{
    public static ModelPreviewData Import(ModelInput input)
    {
        if (input.Bytes.Length > 256 * 1024 * 1024 || input.Dependencies.Count > 32 || input.Dependencies.Values.Sum(b => (long)b.Length) > 32 * 1024 * 1024) throw new InvalidDataException("モデル入力のプレビュー上限を超えています。");
        var native = Path.Combine(AppContext.BaseDirectory, "Native", "assimp.dll");
        if (!File.Exists(native)) throw new FileNotFoundException("同梱のモデル解析DLLが見つかりません。", native);
        try
        {
            if (!SharpAssimp.Unmanaged.AssimpLibrary.Instance.IsLibraryLoaded && !SharpAssimp.Unmanaged.AssimpLibrary.Instance.LoadLibrary(native))
                throw new InvalidOperationException("モデル解析DLLのロードに失敗しました。");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("モデル解析DLLをロードできません。Microsoft Visual C++ v14 x64ランタイムと、Native/assimp.dllの存在を確認してください。 " + ex.Message, ex);
        }        using var context = new AssimpContext();
        context.SetConfig(new SharpAssimp.Configs.FBXImportAnimationsConfig(false));
        using var io = new VirtualModelIO(input);
        context.SetIOSystem(io);
        var scene = context.ImportFile(io.MainPath, PostProcessSteps.Triangulate | PostProcessSteps.GenerateSmoothNormals | PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.ValidateDataStructure);
        if (scene == null || scene.RootNode == null || !scene.HasMeshes) throw new InvalidDataException("表示できるメッシュがありません。");
        var result = new List<PreviewMesh>();
        var materials = new HashSet<int>();
        var bones = new HashSet<string>(StringComparer.Ordinal);
        long vertices = 0, triangles = 0;
        var pending = new Stack<(Node Node, Matrix4x4 Parent, int Depth, string Path)>();
        pending.Push((scene.RootNode, Matrix4x4.Identity, 0, ""));
        int visited = 0;
        while (pending.TryPop(out var next))
        {
            if (++visited > 100_000 || next.Depth > 128) throw new InvalidDataException("モデルの階層が大きすぎます。");
            // Assimp stores column-vector transforms; System.Numerics uses row vectors.
            var transform = Matrix4x4.Transpose(next.Node.Transform) * next.Parent;
            if (!Matrix4x4.Invert(transform, out var inverse)) throw new InvalidDataException("モデルの変換行列が不正です。");
            var normalTransform = Matrix4x4.Transpose(inverse);
            foreach (int index in next.Node.MeshIndices)
            {
                if (index < 0 || index >= scene.MeshCount) throw new InvalidDataException("メッシュ参照が不正です。");
                var mesh = scene.Meshes[index];
                var faces = mesh.Faces.Where(f => f.IndexCount == 3).ToArray();
                if (faces.Length == 0) continue;
                vertices += mesh.VertexCount; triangles += faces.Length;
                if (vertices > 2_000_000 || triangles > 2_000_000 || result.Count >= 10_000) throw new InvalidDataException("モデルプレビューの形状上限（200万頂点・200万三角形）を超えています。");
                var positions = new float[checked(mesh.VertexCount * 3)];
                var normals = new float[positions.Length];
                for (int i = 0; i < mesh.VertexCount; i++)
                {
                    Vector3 v = Vector3.Transform(mesh.Vertices[i], transform);
                    Vector3 n = mesh.HasNormals ? Vector3.TransformNormal(mesh.Normals[i], normalTransform) : Vector3.UnitY;
                    if (n.LengthSquared() > 0) n = Vector3.Normalize(n);
                    positions[i * 3] = v.X; positions[i * 3 + 1] = v.Y; positions[i * 3 + 2] = v.Z;
                    normals[i * 3] = n.X; normals[i * 3 + 1] = n.Y; normals[i * 3 + 2] = n.Z;
                }
                var indices = faces.SelectMany(f => f.Indices).ToArray();
                if (transform.GetDeterminant() < 0)
                    for (int i = 0; i < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
                if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= scene.MaterialCount) throw new InvalidDataException("材質参照が不正です。");
                materials.Add(mesh.MaterialIndex);
                var material = scene.Materials[mesh.MaterialIndex];
                var diffuse = material.HasColorDiffuse ? material.ColorDiffuse : new Vector4(.72f, .64f, .53f, 1f);
                var uv = new float[mesh.VertexCount * 2];
                if (mesh.HasTextureCoords(0)) for (int i = 0; i < mesh.VertexCount; i++) { uv[i * 2] = mesh.TextureCoordinateChannels[0][i].X; uv[i * 2 + 1] = 1 - mesh.TextureCoordinateChannels[0][i].Y; }
                result.Add(new(positions, normals, indices, [Math.Clamp(diffuse.X, 0, 1), Math.Clamp(diffuse.Y, 0, 1), Math.Clamp(diffuse.Z, 0, 1), 1], uv, material.Name ?? "", next.Node.Name ?? "", material.HasTextureDiffuse ? material.TextureDiffuse.FilePath : "", next.Path, mesh.Name ?? "", mesh.BoneCount > 0));
                foreach (var bone in mesh.Bones) bones.Add(bone.Name);
            }
            foreach (var child in next.Node.Children) pending.Push((child, transform, next.Depth + 1, next.Path.Length == 0 ? child.Name : next.Path + "/" + child.Name));
        }
        string[] notes = ["静止形状・テクスチャ・簡易アニメ調照明で表示。Unity Shader、Prefab設定、アニメーションの完全再現は非対応。"];
        if (io.DeniedCount > 0) notes = [notes[0], $"パッケージ外または未取得の外部参照を{io.DeniedCount}件ブロックしました。"];
        var data = new ModelPreviewData(new(vertices, triangles, scene.MeshCount, Math.Max(materials.Count, input.ReferencedMaterialCount), bones.Count, bones.Count > 0), result.ToArray(), notes);
        data.Validate();
        return data;
    }
}

public sealed class VirtualModelIO : IOSystem
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string baseDirectory;
    public int DeniedCount { get; private set; }
    public string MainPath { get; }
    public VirtualModelIO(ModelInput input)
    {
        PackageReader.ValidatePath(input.Path);
        baseDirectory = input.Path.Contains('/') ? input.Path[..input.Path.LastIndexOf('/')] : "";
        MainPath = "kwta_model" + Path.GetExtension(input.Path).ToLowerInvariant();
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int counter = 0;
        foreach (var (path, data) in input.Dependencies)
        {
            PackageReader.ValidatePath(path);
            if (!Path.GetExtension(path).Equals(".mtl", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("モデルの補助ファイル形式が不正です。");
            string alias = $"kwta_dependency_{counter++}.mtl";
            aliases[path] = alias;
            files[alias] = Encoding.UTF8.GetBytes(TextDecoder.Decode(data, false, out _));
        }
        byte[] modelBytes = input.Bytes;
        if (MainPath.EndsWith(".obj", StringComparison.Ordinal))
        {
            string text = TextDecoder.Decode(modelBytes, false, out _);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s*mtllib\s+([^\r\n]+)", match =>
            {
                string reference = match.Groups[1].Value.Trim();
                string? whole = Resolve(baseDirectory, reference);
                if (whole != null && aliases.TryGetValue(whole, out var alias)) return "mtllib " + alias;
                var resolved = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(r => Resolve(baseDirectory, r))
                    .Where(p => p != null && aliases.ContainsKey(p)).Select(p => aliases[p!]).ToArray();
                return "mtllib " + (resolved.Length == 0 ? "kwta_missing.mtl" : string.Join(" ", resolved));
            }, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            modelBytes = Encoding.UTF8.GetBytes(text);
        }
        // Native string marshalling can depend on the Windows ANSI code page.
        // ASCII aliases keep model/MTL file access independent of the original Unicode paths.
        files[MainPath] = modelBytes;
    }    public static string? Resolve(string baseDirectory, string reference)
    {
        reference = reference.Replace('\\', '/');
        if (reference.StartsWith('/') || reference.Contains(':') || reference.Any(char.IsControl)) return null;
        var parts = new List<string>();
        foreach (var part in ((baseDirectory.Length == 0 ? "" : baseDirectory + "/") + reference).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        string result = string.Join('/', parts);
        try { PackageReader.ValidatePath(result); return result; } catch (InvalidDataException) { return null; }
    }
    public override IOStream OpenFile(string pathToFile, FileIOMode fileMode)
    {
        if (fileMode != FileIOMode.Read && fileMode != FileIOMode.ReadBinary) { DeniedCount++; return null!; }
        var path = Resolve("", pathToFile);
        if (path == null || !files.TryGetValue(path, out var bytes))
        {
            path = Resolve(baseDirectory, pathToFile);
            if (path == null || !files.TryGetValue(path, out bytes)) { DeniedCount++; return null!; }
        }
        return new BytesModelStream(path, bytes);
    }
    private sealed class BytesModelStream(string path, byte[] bytes) : IOStream(path, FileIOMode.ReadBinary)
    {
        private readonly MemoryStream stream = new(bytes, writable: false);
        public override bool IsValid => !IsDisposed;
        public override long Read(byte[] dataRead, long count) => stream.Read(dataRead, 0, (int)Math.Min(count, dataRead.Length));
        public override long Write(byte[] dataToWrite, long count) => 0;
        public override ReturnCode Seek(long offset, Origin seekOrigin)
        {
            try
            {
                long target = seekOrigin switch { Origin.Set => offset, Origin.Current => stream.Position + offset, Origin.End => stream.Length - offset, _ => -1 };
                if (target < 0 || target > stream.Length) return ReturnCode.Failure;
                stream.Position = target; return ReturnCode.Success;
            }
            catch (IOException) { return ReturnCode.Failure; }
        }
        public override long GetPosition() => stream.Position;
        public override long GetFileSize() => stream.Length;
        public override void Flush() { }
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }
}





