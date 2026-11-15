namespace KuniWhatsThisAsset.Core;

public sealed record ModelStatistics(long VertexCount, long TriangleCount, int MeshCount, int MaterialCount, int BoneCount, bool HasSkinning)
{
    public bool RequiresLoadWarning => TriangleCount >= 100_000 || MaterialCount >= 10;
}
public sealed record PreviewMesh(float[] Positions, float[] Normals, int[] Indices, float[] Color, float[]? UV = null, string MaterialName = "", string NodeName = "", string TexturePath = "");
public sealed record ModelPreviewData(ModelStatistics Statistics, PreviewMesh[] Meshes, string[] Notes)
{
    public void Validate()
    {
        if (Meshes == null || Statistics == null || Notes == null || Meshes.Length > 10_000 || Notes.Length > 100 || Notes.Any(n => n == null || n.Length > 4096)) throw new InvalidDataException("モデルデータの件数が不正です。");
        long vertices = 0, triangles = 0;
        foreach (var mesh in Meshes)
        {
            if (mesh == null || mesh.Positions == null || mesh.Normals == null || mesh.Indices == null || mesh.Color == null || mesh.Positions.Length % 3 != 0 || mesh.Indices.Length % 3 != 0 || mesh.Normals.Length != mesh.Positions.Length || mesh.Color.Length != 4)
                throw new InvalidDataException("モデルの配列長が不正です。");
            if (mesh.UV != null && (mesh.UV.Length != mesh.Positions.Length / 3 * 2 || mesh.UV.Any(v => !float.IsFinite(v) || Math.Abs(v) > 1e6))) throw new InvalidDataException("UV座標が不正です。");
            if (mesh.MaterialName.Length > 4096 || mesh.NodeName.Length > 4096 || mesh.TexturePath.Length > 4096) throw new InvalidDataException("モデル名称が長すぎます。");
            vertices += mesh.Positions.Length / 3; triangles += mesh.Indices.Length / 3;
            if (vertices > 2_000_000 || triangles > 2_000_000 || mesh.Positions.Any(v => !float.IsFinite(v) || Math.Abs(v) > 1e12f) || mesh.Normals.Any(v => !float.IsFinite(v)) || mesh.Color.Any(v => !float.IsFinite(v) || v < 0 || v > 1) || mesh.Indices.Any(i => i < 0 || i >= mesh.Positions.Length / 3))
                throw new InvalidDataException("モデルの値・索引・プレビュー容量が不正です。");
        }
        if (vertices != Statistics.VertexCount || triangles != Statistics.TriangleCount || triangles == 0 || Statistics.MeshCount < 1 || Statistics.MaterialCount is < 1 or > 10000 || Statistics.MeshCount > 10000 || Statistics.BoneCount is < 0 or > 2000000)
            throw new InvalidDataException("モデルの統計が不正です。");
    }
}



