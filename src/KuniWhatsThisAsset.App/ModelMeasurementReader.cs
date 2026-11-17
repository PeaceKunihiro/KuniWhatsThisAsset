using System.Globalization;
using System.Numerics;
using KuniWhatsThisAsset.Core;
using SharpAssimp;
namespace KuniWhatsThisAsset.App;
public static class ModelMeasurementReader
{
    public sealed record Units(double Factor, double? FileMeters, string Basis);
    public static Units ReadUnits(Scene scene, bool fbx)
    {
        if(!fbx)return new(1,null,"OBJ: 1座標単位=1 mを仮定。元ファイルのTransform Scale情報なし。");
        double unit=double.NaN;
        if(scene.Metadata!=null&&scene.Metadata.TryGetValue("UnitScaleFactor",out var entry))
            try{unit=Convert.ToDouble(entry.Data,CultureInfo.InvariantCulture);}catch(Exception ex)when(ex is FormatException or InvalidCastException or OverflowException){}
        // Remove the generated root's unit factor for measurement only; retain preview geometry.
        var root=Matrix4x4.Transpose(scene.RootNode!.Transform);var scale=Scale(root);
        double rootFactor=scale==null?double.NaN:Math.Abs(scale[0]);
        if(double.IsFinite(unit)&&unit>0&&unit<=1e14&&rootFactor>0&&scale!=null&&Math.Abs(Math.Abs(scale[1])-rootFactor)<rootFactor*1e-4&&Math.Abs(Math.Abs(scale[2])-rootFactor)<rootFactor*1e-4)
            return new(unit/100/rootFactor,unit/100,$"FBX: 1ファイル単位={ModelMeasurements.Number(unit/100)} m / 解析ルート倍率={ModelMeasurements.Number(rootFactor)}を除いて換算");
        return new(1,null,"FBX: 単位または解析ルート倍率を確認できないため、1解析座標単位=1 mを仮定");
    }
    public static double[]? Scale(Matrix4x4 matrix)
    {
        if(!Matrix4x4.Decompose(matrix,out var scale,out var rotation,out var translation))return null;
        var rebuilt=Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(rotation)*Matrix4x4.CreateTranslation(translation);
        float[] a=[matrix.M11,matrix.M12,matrix.M13,matrix.M21,matrix.M22,matrix.M23,matrix.M31,matrix.M32,matrix.M33,matrix.M14,matrix.M24,matrix.M34,matrix.M44];
        float[] b=[rebuilt.M11,rebuilt.M12,rebuilt.M13,rebuilt.M21,rebuilt.M22,rebuilt.M23,rebuilt.M31,rebuilt.M32,rebuilt.M33,rebuilt.M14,rebuilt.M24,rebuilt.M34,rebuilt.M44];
        for(int i=0;i<a.Length;i++)if(!float.IsFinite(a[i])||Math.Abs(a[i]-b[i])>1e-4*Math.Max(1,Math.Abs(a[i])))return null;
        return [scale.X,scale.Y,scale.Z];
    }
}
