using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpAssimp;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;

internal static class MeasurementChecks
{
    public static void Run(MainWindow main)
    {
        void Check(bool value,string why){if(!value)throw new Exception(why);}
        void Near(double actual,double expected,string name){Check(Math.Abs(actual-expected)<1e-4,$"{name}: {actual} != {expected}");}
        string directory=Path.GetFullPath("artifacts/measurements-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        if(!SharpAssimp.Unmanaged.AssimpLibrary.Instance.IsLibraryLoaded)SharpAssimp.Unmanaged.AssimpLibrary.Instance.LoadLibrary(NativeDependencies.AssimpPath());
        string ascii=Path.Combine(directory,"known.fbx");
        using(var context=new AssimpContext())
        {
            using var stream=new MemoryStream(Encoding.UTF8.GetBytes("v 0 0 0\nv 2 3 0\nv 0 1 4\nf 1 2 3\n"));var scene=context.ImportFileFromStream(stream,PostProcessSteps.Triangulate,"obj")!;
            scene.RootNode!.Children[0]!.Transform=Matrix4x4.CreateScale(2,3,4);Check(context.ExportFile(scene,ascii,"fbxa"),"Fixture export failed");
        }
        string original=File.ReadAllText(ascii);ModelPreviewData? shown=null;
        foreach(int unit in new[]{1,100})
        {
            string text=Regex.Replace(original,"(P:\\s*\"UnitScaleFactor\"[^\\r\\n]*,\\s*)[0-9.eE+-]+",m=>m.Groups[1].Value+unit);
            Check(text.Contains("\"UnitScaleFactor\""),"Unit fixture missing");
            var data=ModelWorker.ImportAsync(new("test.fbx",Encoding.UTF8.GetBytes(text),new()),CancellationToken.None).GetAwaiter().GetResult();
            var result=ModelMeasurements.Calculate(data)!;Near(result.Bounds.Width,4*unit/100d,"FBX width");Near(result.Bounds.Height,9*unit/100d,"FBX height");Near(result.Bounds.Length,16*unit/100d,"FBX length");
            var scale=result.Meshes.Single().LocalScale!;Near(Math.Abs(scale[0]),2,"Local X");Near(Math.Abs(scale[1]),3,"Local Y");Near(Math.Abs(scale[2]),4,"Local Z");
            Console.WriteLine($"PASS FBX UnitScaleFactor={unit}: "+ModelMeasurements.Size(result.Bounds)+" / "+ModelMeasurements.Scale(scale));
            var scaled=ModelMeasurements.Calculate(ModelMeasurements.ApplyImportSettings(data,"ModelImporter:\n  meshes:\n    globalScale: 2\n    useFileUnits: 1\n"))!;Near(scaled.Bounds.Height,18*unit/100d,"Unity scale multiplier");
            var unconverted=ModelMeasurements.Calculate(ModelMeasurements.ApplyImportSettings(data,"ModelImporter:\n  meshes:\n    globalScale: 2\n    useFileUnits: 0\n"))!;Near(unconverted.Bounds.Height,18,"Convert Units off");
            if(unit==100)shown=ModelMeasurements.ApplyImportSettings(data,"");
        }
        Check(ModelMeasurementReader.Scale(new Matrix4x4(1,.4f,0,0,0,1,0,0,0,0,1,0,0,0,0,1))==null,"Shear must not claim a Scale");
        var obj=ModelImporter.Import(new("test.obj",Encoding.UTF8.GetBytes("v 0 0 0\nv 2 3 0\nv 0 1 4\nf 1 2 3\n"),new()));var objMeasurement=ModelMeasurements.Calculate(obj)!;Near(objMeasurement.Bounds.Height,3,"OBJ metre assumption");Check(objMeasurement.Meshes.Single().LocalScale==null,"OBJ invented source Scale");
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        ((TextBlock)typeof(MainWindow).GetField("PreviewHint",flags)!.GetValue(main)!).Text="";
        typeof(MainWindow).GetMethod("ClearPreviewFindings",flags)!.Invoke(main,null);typeof(MainWindow).GetMethod("Notify",flags)!.Invoke(main,[Array.Empty<Finding>()]);
        var view=(ModelViewport)typeof(MainWindow).GetField("ModelView",flags)!.GetValue(main)!;view.SetModel(shown!);view.Visibility=Visibility.Visible;main.UpdateLayout();
        var info=(TextBlock)typeof(ModelViewport).GetField("info",flags)!.GetValue(view)!;Check(info.Text.Contains("全幅 4")&&info.Text.Contains("全高 9")&&info.Text.Contains("全長 16")&&info.Text.Contains("Scale（ローカル）"),"Measurement UI missing");
        var cache=new RenderCache(Path.Combine(directory,"cache"));string key=new('a',64);var measurement=ModelMeasurements.Calculate(shown!)!;
        Check(cache.Write(key,new(shown!.Statistics,view.CaptureViews(),shown.Notes){Measurement=measurement},new()),"Measurement cache write failed");var cached=cache.Read(key)!;Near(cached.Measurement!.Bounds.Height,9,"Cache measurement");view.ShowCached(cached,()=>{});Check(info.Text.Contains("全高 9"),"Cache lost measurement UI");
        var dialog=new ModelMeasurementWindow(measurement){Owner=main};dialog.Show();dialog.UpdateLayout();
        void Capture(FrameworkElement element,string path){var bitmap=new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(element);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);png.Save(file);}
        Capture(dialog,"artifacts/model-measurement-details-v0.14.0.png");dialog.Close();view.SetModel(shown);main.UpdateLayout();FeatureChecks.Pump(Task.Delay(100));Capture(main,"artifacts/model-measurement-preview-v0.14.0.png");
        Console.WriteLine("PASS measurement metre/centimetre FBX, local Scale, Unity meta/on-off conversion, OBJ assumptions, shear refusal, live/cache display and details dialog");
    }
}
