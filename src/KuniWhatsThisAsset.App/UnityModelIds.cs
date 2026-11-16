using System.IO.Hashing;
using System.Text;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public static class UnityModelIds
{
    // ModelImporter identifiers, documented by Unity's importer team; only exact ID matches are used.
    public static string Id(string type, string identifier) => unchecked((long)XxHash64.HashToUInt64(Encoding.UTF8.GetBytes($"Type:{type}->{identifier}0"))).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string ObjectPath(PreviewMesh mesh) => "//RootNode/root" + (mesh.NodePath.Length == 0 ? "" : "/" + mesh.NodePath);
    public static string Renderer(PreviewMesh mesh) { string type = mesh.Skinned ? "SkinnedMeshRenderer" : "MeshRenderer"; return Id(type, ObjectPath(mesh) + "/" + type); }
    public static string GameObject(PreviewMesh mesh) => Id("GameObject", ObjectPath(mesh));
    public static string Mesh(PreviewMesh mesh) => Id("Mesh", mesh.MeshName.Length > 0 ? mesh.MeshName : mesh.NodeName);
}
