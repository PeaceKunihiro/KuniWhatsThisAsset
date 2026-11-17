using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpAssimp;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;

internal static class LocalModelChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(MainWindow main)
    {
        void Check(bool value, string why) { if (!value) throw new Exception(why); }
        string root = Path.GetFullPath("artifacts/local-models-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, "材質"));
        byte[] Png(byte color) { var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[]{0,0,color,255},4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);return stream.ToArray(); }
        const string obj = "mtllib \"材質/材質 名前.mtl\"\nv 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nusemtl Body\nf 1/1 2/2 3/3\n";
        string path=Path.Combine(root,"モデル.obj"),mtl=Path.Combine(root,"材質/材質 名前.mtl"),image=Path.Combine(root,"材質/色 名前.png");
        File.WriteAllText(path,obj); File.WriteAllBytes(mtl,TextDecoderTestEncoding().GetBytes("newmtl Body\nKd 1 0 0\nmap_Kd -s 1 1 1 \"色 名前.png\"\n")); File.WriteAllBytes(image,Png(240));
        var doc=LocalModelDocument.Read(path,CancellationToken.None);Check(doc.Input.Dependencies.Count==1,"Unicode MTL missing");
        var model=ModelWorker.ImportAsync(doc.Input,CancellationToken.None).GetAwaiter().GetResult();Check(model.Statistics.TriangleCount==1&&model.Meshes.Any(m=>m.MaterialName=="Body"),"OBJ material lost");doc.ReadTextures(model,CancellationToken.None);var materials=doc.Materials(model,CancellationToken.None);Check(materials.Materials.Count==1&&materials.Notes.Length==0,"Relative MTL texture failed: "+string.Join(";",doc.Notes.Concat(materials.Notes)));string first=doc.CacheKey();
        File.WriteAllBytes(image,Png(120));var changed=LocalModelDocument.Read(path,CancellationToken.None);changed.ReadTextures(model,CancellationToken.None);Check(changed.CacheKey()!=first,"Texture edit did not invalidate cache");
        File.Delete(image);var missing=LocalModelDocument.Read(path,CancellationToken.None);missing.ReadTextures(model,CancellationToken.None);Check(missing.Notes.Count>0&&missing.Materials(model,CancellationToken.None).Materials.Count==0&&missing.CacheKey()!=first,"Missing texture not passive/fallback");File.WriteAllBytes(image,Png(240));
        // Explicitly scoped file access must never read the parent, absolute paths, or link targets.
        string outside=Path.Combine(Path.GetDirectoryName(root)!,"outside-"+Guid.NewGuid()+".mtl");File.WriteAllText(outside,"newmtl Secret\n");
        File.WriteAllText(path,obj.Replace("\"材質/材質 名前.mtl\"","../"+Path.GetFileName(outside)));var denied=LocalModelDocument.Read(path,CancellationToken.None);Check(denied.Input.Dependencies.Count==0&&denied.Notes.Count>0,"Parent MTL escaped scope");
        File.WriteAllText(path,obj.Replace("\"材質/材質 名前.mtl\"",outside));Check(LocalModelDocument.Read(path,CancellationToken.None).Input.Dependencies.Count==0,"Absolute MTL escaped scope");File.WriteAllText(path,obj);
        using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();bool rejected=false;try{LocalModelDocument.Read(path,cancelled.Token);}catch(OperationCanceledException){rejected=true;}Check(rejected,"Cancelled read continued");}
        // Native exporter provides an independent FBX containing compressed embedded image data.
        string fbx=Path.Combine(root,"内蔵.fbx");
        SharpAssimp.Unmanaged.AssimpLibrary.Instance.LoadLibrary(Path.Combine(AppContext.BaseDirectory,"Libs/assimp.dll"));
        using(var context=new AssimpContext())
        {
            using var stream=new MemoryStream(Encoding.UTF8.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nf 1/1 2/2 3/3\n"));
            var scene=context.ImportFileFromStream(stream,PostProcessSteps.Triangulate,"obj")!;
            scene.Textures.Add(new EmbeddedTexture("png",Png(240),"embedded.png"));
            scene.Materials[0]!.TextureDiffuse=new TextureSlot("*0",TextureType.Diffuse,0,TextureMapping.FromUV,0,1,TextureOperation.Multiply,TextureWrapMode.Wrap,TextureWrapMode.Wrap,0);
            Check(context.ExportFile(scene,fbx,"fbx"),"FBX fixture export failed");
        }
        var fbxDoc=LocalModelDocument.Read(fbx,CancellationToken.None);var fbxModel=ModelWorker.ImportAsync(fbxDoc.Input,CancellationToken.None).GetAwaiter().GetResult();Check(fbxModel.Statistics.TriangleCount==1&&fbxModel.EmbeddedTextures.Count==1,"FBX embedded texture missing: "+string.Join(";",fbxModel.Meshes.Select(m=>m.TexturePath))+" / "+fbxModel.EmbeddedTextures.Count);fbxDoc.ReadTextures(fbxModel,CancellationToken.None);Check(fbxDoc.Materials(fbxModel,CancellationToken.None).Materials.Count==1,"Embedded FBX image not decoded");
        // Raw BGRA payload follows the same bounded transfer format.
        var raw=fbxModel with{EmbeddedTextures=fbxModel.EmbeddedTextures.ToDictionary(p=>p.Key,p=>new PreviewTexture(1,1,[0,0,240,255]))};raw.Validate();Check(fbxDoc.Materials(raw,CancellationToken.None).Materials.Count==1,"Raw embedded texture not decoded");
        var invalid=fbxModel with{EmbeddedTextures=new(){["*0"]=new(8193,1,[0,0,0,0])}};bool invalidRefused=false;try{invalid.Validate();}catch(InvalidDataException){invalidRefused=true;}Check(invalidRefused,"Invalid embedded dimensions accepted");
        string externalFbx=Path.Combine(root,"external.fbx");File.WriteAllBytes(Path.Combine(root,"external.png"),Png(240));
        using(var context=new AssimpContext())
        {
            using var stream=new MemoryStream(Encoding.UTF8.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nf 1/1 2/2 3/3\n"));var scene=context.ImportFileFromStream(stream,PostProcessSteps.Triangulate,"obj")!;
            scene.Materials[0]!.TextureDiffuse=new TextureSlot("external.png",TextureType.Diffuse,0,TextureMapping.FromUV,0,1,TextureOperation.Multiply,TextureWrapMode.Wrap,TextureWrapMode.Wrap,0);
            Check(context.ExportFile(scene,externalFbx,"fbxa"),"ASCII FBX export failed");
        }
        var externalDoc=LocalModelDocument.Read(externalFbx,CancellationToken.None);var externalModel=ModelWorker.ImportAsync(externalDoc.Input,CancellationToken.None).GetAwaiter().GetResult();externalDoc.ReadTextures(externalModel,CancellationToken.None);Check(externalDoc.Materials(externalModel,CancellationToken.None).Materials.Count==1,"ASCII FBX external image failed: "+string.Join(";",externalDoc.Notes));
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(main.Dispatcher));
        Task Load(string file,Func<ModelStatistics,bool>? confirm=null,bool cache=true)=>(Task)typeof(MainWindow).GetMethod("LoadLocalModelAsync",Flags)!.Invoke(main,[file,confirm,cache])!;
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,Flags)!.GetValue(main)!;
        FeatureChecks.Pump(Load(path));Check(Field<ModelViewport>("ModelView").Visibility==Visibility.Visible&&!Field<MenuItem>("RepackageMenu").IsEnabled&&Field<TextBlock>("Status").Text.Contains("1ポリゴン"),"Direct OBJ UI failed");
        var tree=Field<TreeView>("PackageTree");var modelNode=(TreeViewItem)tree.Items[0];var imageNode=modelNode.Items.OfType<TreeViewItem>().Single(n=>n.Header.ToString()!.EndsWith(".png"));imageNode.IsSelected=true;FeatureChecks.Pump(Task.Delay(100));Check(Field<Image>("ImagePreview").Visibility==Visibility.Visible&&Field<ModelViewport>("ModelView").Visibility!=Visibility.Visible,"Local dependency image selection failed");
        modelNode.Items.OfType<TreeViewItem>().Single(n=>n.Header.ToString()!.EndsWith(".mtl")).IsSelected=true;Check(Field<TextBox>("Preview").Visibility==Visibility.Visible&&Field<TextBox>("Preview").Text.Contains("map_Kd"),"Local MTL text selection failed");
        FeatureChecks.Pump(Load(path));Check(Field<TextBlock>("Status").Text.Contains("キャッシュ"),"Direct cache miss");
        FeatureChecks.Pump(Load(fbx));Check(Field<ModelViewport>("ModelView").Visibility==Visibility.Visible,"Direct FBX UI failed");
        FeatureChecks.Pump((Task)typeof(MainWindow).GetMethod("LoadPackage",Flags)!.Invoke(main,[externalFbx])!);Check(Field<ModelViewport>("ModelView").Visibility==Visibility.Visible&&Field<TextBox>("PackagePath").Text==externalFbx,"Shared open dispatch failed");
        main.UpdateLayout();var screenshot=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);screenshot.Render(main);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(screenshot));using(var file=File.Create("artifacts/local-model-preview-v0.13.0.png"))png.Save(file);
        string many=Path.Combine(root,"heavy.obj");File.WriteAllText(many,"v 0 0 0\nv 1 0 0\nv 0 1 0\n"+string.Join('\n',Enumerable.Repeat("f 1 2 3",100000)));bool asked=false;FeatureChecks.Pump(Load(many,s=>{asked=true;return false;},false));Check(asked&&Field<ModelViewport>("ModelView").Visibility!=Visibility.Visible,"Heavy direct model rendered before approval");
        File.WriteAllText(path,"MZinvalid");FeatureChecks.Pump(Load(path));Check(Field<ModelViewport>("ModelView").Visibility!=Visibility.Visible&&Field<TextBlock>("PreviewHint").Text.Contains("表示できません"),"Broken model left stale preview");
        Console.WriteLine("PASS direct OBJ/FBX, Shift-JIS/Unicode MTL, scoped textures, embedded PNG/BGRA, cache invalidation, load warning, cancellation and passive failures");
    }
    private static Encoding TextDecoderTestEncoding(){Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);return Encoding.GetEncoding(932);}
    public static void Actual(MainWindow main)
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(main.Dispatcher));
        foreach (var (name, package) in new[] { ("Fuzzy", "E:/GPT_WORKS/KWT_TEST/Fuzzy_ver.2.00/Fuzzy.unitypackage"), ("Luchika", "E:/GPT_WORKS/KWT_TEST/Luchika_ver.1.00/Luchika.unitypackage") })
        {
            var reader=new PackageReader();var report=reader.Read(package);var item=report.Items.Where(i=>i.Format==".fbx").MaxBy(i=>i.Size)!;
            string directory=Path.GetFullPath("artifacts/local-real-models/"+name);Directory.CreateDirectory(directory);string path=Path.Combine(directory,item.Name);File.WriteAllBytes(path,reader.ReadAssetBytes(package,item.Guid));
            var task=(Task)typeof(MainWindow).GetMethod("LoadLocalModelAsync",Flags)!.Invoke(main,[path,(Func<ModelStatistics,bool>)(_=>true),false])!;FeatureChecks.Pump(task,100);
            var status=(TextBlock)typeof(MainWindow).GetField("Status",Flags)!.GetValue(main)!;var viewport=(ModelViewport)typeof(MainWindow).GetField("ModelView",Flags)!.GetValue(main)!;
            if(viewport.Visibility!=Visibility.Visible||!status.Text.Contains("ポリゴン"))throw new Exception("Real direct model failed: "+name+" "+status.Text);
            Console.WriteLine("PASS real direct model "+name+": "+status.Text);
        }
    }
}
