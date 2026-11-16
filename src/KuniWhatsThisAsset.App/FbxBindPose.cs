using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace KuniWhatsThisAsset.App;

// Reads only binary FBX Model IDs and global bind-pose matrices. Geometry stays in Assimp.
public static class FbxBindPose
{
    public static Dictionary<string, Matrix4x4> Read(byte[] bytes)
    {
        if (bytes.Length < 27 || !bytes.AsSpan(0, 22).SequenceEqual("Kaydara FBX Binary  \0\u001a"u8)) return new();
        using var stream = new MemoryStream(bytes, writable: false); using var reader = new BinaryReader(stream, Encoding.UTF8);
        stream.Position = 23; bool wide = reader.ReadUInt32() >= 7500;
        int sentinel = wide ? 25 : 13, visited = 0;
        var models = new Dictionary<long, string>(); var poses = new Dictionary<long, Matrix4x4>(); var ambiguous = new HashSet<long>();
        long Number() { ulong n = wide ? reader.ReadUInt64() : reader.ReadUInt32(); if (n > (ulong)bytes.Length) throw new InvalidDataException("FBXノードの範囲が不正です。"); return (long)n; }
        void Require(long length, long end) { if (length < 0 || length > end - stream.Position) throw new InvalidDataException("FBXプロパティの範囲が不正です。"); }
        string String(long end)
        {
            uint length = reader.ReadUInt32(); Require(length, end);
            if (length > 16384) { stream.Position += length; return ""; }
            return new UTF8Encoding(false, true).GetString(reader.ReadBytes((int)length));
        }
        object? Property(long end, bool matrix)
        {
            Require(1, end); char type = (char)reader.ReadByte();
            int width = type switch { 'Y' => 2, 'C' => 1, 'I' or 'F' => 4, 'D' or 'L' => 8, _ => 0 };
            if (width != 0)
            {
                Require(width, end);
                return type switch { 'Y' => (object)(long)reader.ReadInt16(), 'C' => (long)reader.ReadByte(), 'I' => (long)reader.ReadInt32(), 'L' => reader.ReadInt64(), 'F' => (double)reader.ReadSingle(), _ => reader.ReadDouble() };
            }
            if (type == 'S') { Require(4, end); return String(end); }
            if (type == 'R') { Require(4, end); uint length = reader.ReadUInt32(); Require(length, end); stream.Position += length; return null; }
            if (type is not ('f' or 'd' or 'l' or 'i' or 'b' or 'c')) throw new InvalidDataException("FBXプロパティ型が未対応です。");
            Require(12, end); uint count = reader.ReadUInt32(), encoding = reader.ReadUInt32(), size = reader.ReadUInt32(); Require(size, end);
            if (!matrix || type != 'd' || count != 16) { stream.Position += size; return null; }
            if (size > 1024 || encoding > 1) throw new InvalidDataException("FBXバインド行列の圧縮形式が不正です。");
            byte[] data = reader.ReadBytes((int)size);
            if (encoding == 1)
            {
                using var compressed = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress); data = new byte[128]; compressed.ReadExactly(data);
                if (compressed.ReadByte() != -1) throw new InvalidDataException("FBXバインド行列が長すぎます。");
            }
            if (data.Length != 128) throw new InvalidDataException("FBXバインド行列の長さが不正です。");
            float[] values = Enumerable.Range(0, 16).Select(i => (float)BitConverter.ToDouble(data, i * 8)).ToArray();
            if (values.Any(v => !float.IsFinite(v) || Math.Abs(v) > 1e12f)) throw new InvalidDataException("FBXバインド行列の値が不正です。");
            // FBX serializes columns. The same sequence is a row-vector System.Numerics matrix.
            return new Matrix4x4(values[0],values[1],values[2],values[3],values[4],values[5],values[6],values[7],values[8],values[9],values[10],values[11],values[12],values[13],values[14],values[15]);
        }
        (string Name, object?[] Values, List<(string Name, object?[] Values)> Children)? Node(long parentEnd, int depth, bool capture = false)
        {
            if (depth > 128 || ++visited > 200000) throw new InvalidDataException("FBXバインド解析の階層・件数上限を超えています。");
            Require(sentinel, parentEnd); long end = Number(), count = Number(), length = Number(); int nameLength = reader.ReadByte();
            if (end == 0) return null;
            if (end > parentEnd || end <= stream.Position || count > 1000000) throw new InvalidDataException("FBXノードが不正です。");
            Require(nameLength, end); string name = Encoding.ASCII.GetString(reader.ReadBytes(nameLength)); Require(length, end); long propertiesEnd = stream.Position + length;
            bool keep = capture || name is "Model" or "Pose" or "PoseNode"; var values = new List<object?>();
            if (keep)
            {
                for (long i = 0; i < count; i++) { var value = Property(propertiesEnd, name == "Matrix"); if (i < 4) values.Add(value); }
                if (stream.Position != propertiesEnd) throw new InvalidDataException("FBXプロパティ長が一致しません。");
            }
            else stream.Position = propertiesEnd;
            var children = new List<(string Name, object?[] Values)>();
            if (name == "Pose" && (values.Count < 3 || values[2] is not string poseType || poseType != "BindPose"))
            { stream.Position = end; return (name, values.ToArray(), children); }
            if (name is "Objects" or "Pose" or "PoseNode" || capture)
                while (stream.Position < end - sentinel)
                {
                    var child = Node(end, depth + 1, name == "PoseNode"); if (child == null) break;
                    if (name == "PoseNode") children.Add((child.Value.Name, child.Value.Values));
                }
            if (name == "Model" && values.Count >= 3 && values[0] is long id && values[1] is string model && values[2] is string kind && kind == "Mesh")
            {
                string modelName = model.Split('\0')[0]; if (modelName.StartsWith("Model::", StringComparison.Ordinal)) modelName = modelName[7..];
                if (!models.TryAdd(id, modelName)) ambiguous.Add(id);
            }
            if (name == "PoseNode")
            {
                object? nodeId = children.FirstOrDefault(c => c.Name == "Node").Values?.FirstOrDefault();
                object? matrix = children.FirstOrDefault(c => c.Name == "Matrix").Values?.FirstOrDefault();
                if (nodeId is long idValue && matrix is Matrix4x4 transform)
                {
                    if (!Matrix4x4.Invert(transform, out _)) ambiguous.Add(idValue);
                    else if (poses.TryGetValue(idValue, out var prior) && !Close(prior, transform)) ambiguous.Add(idValue);
                    else poses[idValue] = transform;
                }
            }
            stream.Position = end; return (name, values.ToArray(), children);
        }
        while (stream.Position <= bytes.Length - sentinel) { if (Node(bytes.Length, 0) == null) break; }
        return models.GroupBy(m => m.Value, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single())
            .Where(m => !ambiguous.Contains(m.Key) && poses.ContainsKey(m.Key)).ToDictionary(m => m.Value, m => poses[m.Key], StringComparer.Ordinal);
    }
    private static bool Close(Matrix4x4 a, Matrix4x4 b)
    {
        float[] x = [a.M11,a.M12,a.M13,a.M14,a.M21,a.M22,a.M23,a.M24,a.M31,a.M32,a.M33,a.M34,a.M41,a.M42,a.M43,a.M44];
        float[] y = [b.M11,b.M12,b.M13,b.M14,b.M21,b.M22,b.M23,b.M24,b.M31,b.M32,b.M33,b.M34,b.M41,b.M42,b.M43,b.M44];
        return x.Zip(y).All(v => Math.Abs(v.First - v.Second) <= 1e-5f * Math.Max(1, Math.Max(Math.Abs(v.First), Math.Abs(v.Second))));
    }
}
