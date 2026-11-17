using KuniWhatsThisAsset.Core;
internal static class MeasurementChecks
{
    public static void Run(string output,Action<string,Action> test,Action<bool,string> check)
    {
        ModelPreviewData Fixture()=>new(new(3,1,1,1,0,false),[new([0,0,0,2,3,0,0,0,4],[0,0,1,0,0,1,0,0,1],[0,1,2],[1,1,1,1]){Measurement=new("Mesh","Root/Mesh",[1,2,3],[1,2,3],new(-1,-2,-3,1,1,1),.01,"fixture")}],[]);
        test("measurement bounds preserve axis extents, translation and small nonzero values",()=>
        {
            var bounds=ModelBounds.From([-3,-4,-5,2,6,9],.5);check(bounds.Width==2.5&&bounds.Height==5&&bounds.Length==7,"Axis sizes wrong");
            var union=ModelBounds.Union([bounds,new(10,10,10,11,11,11)]);check(union.Width==12.5&&union.Height==13&&union.Length==13.5,"Union omitted offset");
            check(ModelMeasurements.Number(1e-9)!="0","Small positive dimension rounded to zero");
        });
        test("measurement Unity metadata is scoped, applies scale/units once and leaves vertices unchanged",()=>
        {
            var model=Fixture();var updated=ModelMeasurements.ApplyImportSettings(model,"Other:\n  globalScale: 1000\nModelImporter:\n  meshes:\n    globalScale: 2\n    useFileUnits: 0\nOther2:\n  globalScale: 500\n");
            check(updated.Meshes[0].Measurement!.Bounds.Width==400,"Wrong Unity settings multiplier");check(updated.Meshes[0].Positions.SequenceEqual(model.Meshes[0].Positions)&&model.Meshes[0].Measurement!.Bounds.Width==2,"Geometry/source mutated");
            var bad=ModelMeasurements.ApplyImportSettings(model,"ModelImporter:\n  globalScale: NaN\n");check(bad.Meshes[0].Measurement!.Bounds.Width==2&&bad.Meshes[0].Measurement!.Basis.Contains("不正"),"Invalid Scale was accepted");
        });
        test("measurement invalid scales/bounds are refused and absent metadata stays unknown",()=>
        {
            var model=Fixture();bool rejected=false;try{(model.Meshes[0].Measurement! with{LocalScale=[double.NaN,1,1]}).Validate();}catch(InvalidDataException){rejected=true;}check(rejected,"NaN Scale accepted");
            rejected=false;try{new ModelBounds(1,0,0,0,1,1).Validate();}catch(InvalidDataException){rejected=true;}check(rejected,"Negative extent accepted");
            check(ModelMeasurements.Calculate(model with{Meshes=[model.Meshes[0] with{Measurement=null}]})==null,"Missing metric invented");
        });
        test("measurement cache roundtrip and corrupt measurement fallback",()=>
        {
            string folder=Path.Combine(output,"measurement-cache-"+Guid.NewGuid().ToString("N"));var cache=new RenderCache(folder);string key=new('b',64);var data=Fixture();
            check(cache.Write(key,new(data.Statistics,[[1],[2],[3]],[]){Measurement=ModelMeasurements.Calculate(data)},new()),"Cache write failed");check(cache.Read(key)!.Measurement!.Bounds.Height==3,"Cache metric lost");
            string file=Path.Combine(folder,key+".json");File.WriteAllText(file,File.ReadAllText(file).Replace("\"LocalScale\":[1,2,3]","\"LocalScale\":[1,2]"));check(cache.Read(key)==null,"Corrupt measurement reused");
        });
    }
}
