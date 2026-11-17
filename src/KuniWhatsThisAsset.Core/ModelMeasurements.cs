using System.Globalization;
using System.Text.RegularExpressions;

namespace KuniWhatsThisAsset.Core;
public sealed record ModelBounds(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double Length => MaxZ - MinZ;
    public void Validate()
    {
        if (new[]{MinX,MinY,MinZ,MaxX,MaxY,MaxZ}.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1e24) || Width < 0 || Height < 0 || Length < 0) throw new InvalidDataException("推測寸法の範囲が不正です。");
    }
    public static ModelBounds From(float[] positions, double factor)
    {
        if (positions.Length < 3 || positions.Length % 3 != 0 || !double.IsFinite(factor) || factor <= 0) throw new InvalidDataException("寸法の座標・換算値が不正です。");
        double[] min = [double.PositiveInfinity,double.PositiveInfinity,double.PositiveInfinity], max = [double.NegativeInfinity,double.NegativeInfinity,double.NegativeInfinity];
        for(int i=0;i<positions.Length;i++) { double value=positions[i]*factor; min[i%3]=Math.Min(min[i%3],value); max[i%3]=Math.Max(max[i%3],value); }
        var bounds=new ModelBounds(min[0],min[1],min[2],max[0],max[1],max[2]); bounds.Validate(); return bounds;
    }
    public ModelBounds Multiply(double factor) => new(MinX*factor,MinY*factor,MinZ*factor,MaxX*factor,MaxY*factor,MaxZ*factor);
    public static ModelBounds Union(IEnumerable<ModelBounds> bounds)
    {
        var all=bounds.ToArray(); if(all.Length==0)throw new InvalidDataException("計測可能なメッシュがありません。");
        return new(all.Min(b=>b.MinX),all.Min(b=>b.MinY),all.Min(b=>b.MinZ),all.Max(b=>b.MaxX),all.Max(b=>b.MaxY),all.Max(b=>b.MaxZ));
    }
}
public sealed record MeshMeasurement(string Name, string NodePath, double[]? LocalScale, double[]? CombinedScale, ModelBounds Bounds, double? FileUnitMeters, string Basis)
{
    public void Validate()
    {
        static bool Bad(double[]? values) => values != null && (values.Length != 3 || values.Any(v => !double.IsFinite(v) || Math.Abs(v)>1e18));
        if(Name==null||NodePath==null||Basis==null||Name.Length>4096||NodePath.Length>16384||Basis.Length>4096||Bounds==null||Bad(LocalScale)||Bad(CombinedScale)||FileUnitMeters is {} unit&&(!double.IsFinite(unit)||unit<=0||unit>1e12))throw new InvalidDataException("Scale・寸法の情報が不正です。");
        Bounds.Validate();
    }
}
public sealed record ModelMeasurement(ModelBounds Bounds, MeshMeasurement[] Meshes, string Basis)
{
    public void Validate()
    {
        if(Bounds==null||Meshes==null||Meshes.Length is <1 or >10000||Basis==null||Basis.Length>4096)throw new InvalidDataException("モデル計測情報が不正です。");
        Bounds.Validate(); foreach(var mesh in Meshes){if(mesh==null)throw new InvalidDataException("メッシュ計測情報がありません。");mesh.Validate();}
    }
}
public static class ModelMeasurements
{
    public static ModelMeasurement? Calculate(ModelPreviewData model)
    {
        if(model.Meshes.Any(m=>m.Measurement==null))return null;
        var meshes=model.Meshes.Select(m=>m.Measurement!).ToArray(); if(meshes.Length==0)return null;
        var result=new ModelMeasurement(ModelBounds.Union(meshes.Select(m=>m.Bounds)),meshes,model.MeasurementNote);result.Validate();return result;
    }
    // Only ModelImporter scalars are inspected; Unity is never started.
    public static ModelPreviewData ApplyImportSettings(ModelPreviewData model, string metadata)
    {
        metadata=Regex.Match(metadata,@"(?m)^ModelImporter:[ \t]*\r?\n(?<body>(?:[ \t]+[^\r\n]*(?:\r?\n|$)|\r?\n)*)",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)).Groups["body"].Value;
        string? Scalar(string key) => Regex.Match(metadata,@"(?m)^\s{2,4}"+key+@":\s*([^\r\n]+)",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)) is {Success:true} m ? m.Groups[1].Value.Trim() : null;
        string? scaleText=Scalar("globalScale"),unitsText=Scalar("useFileUnits")??Scalar("useFileScale");
        double scale=1; bool invalid=false;
        if(scaleText!=null&&(!double.TryParse(scaleText,NumberStyles.Float,CultureInfo.InvariantCulture,out scale)||!double.IsFinite(scale)||scale<=0||scale>1e9)){scale=1;invalid=true;}
        bool units=unitsText!="0"; if(unitsText!=null&&unitsText is not("0" or "1"))invalid=true;
        string setting=$"Unity Scale Factor={scale.ToString("G9",CultureInfo.InvariantCulture)}{(scaleText==null?"（仮定）":"（meta）")} / Convert Units={(units?"ON":"OFF")}{(unitsText==null?"（仮定）":"（meta）")}"+(invalid?" / 不正な設定値は既定値を使用":"");
        var meshes=model.Meshes.Select(mesh=>
        {
            if(mesh.Measurement is not {} measurement)return mesh;
            double multiplier=scale*(!units&&measurement.FileUnitMeters is {} unit?1/unit:1);
            var updated=measurement with{Bounds=measurement.Bounds.Multiply(multiplier),Basis=measurement.Basis+" / "+setting};updated.Validate();
            return mesh with{Measurement=updated};
        }).ToArray();
        return model with{Meshes=meshes};
    }
    public static string Number(double value) => value.ToString(value!=0&&(Math.Abs(value)<1e-6||Math.Abs(value)>=1e9)?"G6":"0.######",CultureInfo.InvariantCulture);
    public static string Scale(double[]? values) => values==null?"不明":$"X {Number(values[0])} / Y {Number(values[1])} / Z {Number(values[2])}";
    public static string Size(ModelBounds bounds) => $"全幅 {Number(bounds.Width)} × 全高 {Number(bounds.Height)} × 全長 {Number(bounds.Length)} m";
    public static string ScaleSummary(ModelMeasurement measurement)
    {
        var values=measurement.Meshes.Select(m=>Scale(m.LocalScale)).Distinct().Take(2).ToArray();return values.Length==1?values[0]:"メッシュごとに異なる";
    }
}
