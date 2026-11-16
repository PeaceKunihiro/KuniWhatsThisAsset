using System.IO;
using System.Numerics;
using System.Text.Json;
using SharpAssimp;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;
internal static class SharlenChecks
{
    public static int Probe()
    {
        try
        {
            var reader=new PackageReader(); var report=reader.Read("R:/Sharlen.unitypackage");
            var item=report.Items.Single(i=>i.Path.EndsWith("/Sharlen.fbx"));
            var bytes=reader.ReadAssetBytes(report.SourcePath,item.Guid);
            File.WriteAllBytes("artifacts/sharlen-analysis/Sharlen.fbx",bytes);
            var input=new ModelInput(item.Path,bytes,new());
            SharpAssimp.Unmanaged.AssimpLibrary.Instance.LoadLibrary(NativeDependencies.AssimpPath());
            using var context=new AssimpContext(); context.SetConfig(new SharpAssimp.Configs.FBXImportAnimationsConfig(false)); using var io=new VirtualModelIO(input); context.SetIOSystem(io);
            var scene=context.ImportFile(io.MainPath,PostProcessSteps.Triangulate|PostProcessSteps.GenerateSmoothNormals|PostProcessSteps.JoinIdenticalVertices|PostProcessSteps.ValidateDataStructure);
            var globals=new Dictionary<string,Matrix4x4>(); var nodes=new List<object>(); var meshNodes=new List<(Node Node,Matrix4x4 World)>();
            void Walk(Node node,Matrix4x4 parent)
            {
                var world=Matrix4x4.Transpose(node.Transform)*parent; globals[node.Name]=world;
                nodes.Add(new {node.Name,Parent=node.Parent?.Name,Transform=Matrix(node.Transform),Global=Matrix(Matrix4x4.Transpose(world)),Meshes=node.MeshIndices});
                if(node.HasMeshes) meshNodes.Add((node,world)); foreach(var child in node.Children) Walk(child,world);
            }
            Walk(scene.RootNode!,Matrix4x4.Identity);
            object Bounds(IEnumerable<Vector3> points) {var a=points.ToArray(); return new { Min=new[]{a.Min(v=>v.X),a.Min(v=>v.Y),a.Min(v=>v.Z)},Max=new[]{a.Max(v=>v.X),a.Max(v=>v.Y),a.Max(v=>v.Z)}};}
            var meshes=meshNodes.SelectMany(n=>n.Node.MeshIndices.Select(i=>
            {
                var mesh=scene.Meshes[i];
                return new {Node=n.Node.Name,TransformId=UnityModelIds.Id("Transform","//RootNode/root/"+ (n.Node.Name=="Hair"?"Body/Hair":n.Node.Name)+"/Transform"),Mesh=mesh.Name,Vertices=mesh.VertexCount,Material=scene.Materials[mesh.MaterialIndex].Name,LocalBounds=Bounds(mesh.Vertices),WorldBounds=Bounds(mesh.Vertices.Select(v=>Vector3.Transform(v,n.World))),Bones=mesh.Bones.Select(b=>new {b.Name,Offset=Matrix(b.OffsetMatrix),Weights=b.VertexWeightCount,BindBounds=Bounds(mesh.Vertices.Select(v=>Vector3.Transform(v,Matrix4x4.Transpose(b.OffsetMatrix)*globals[b.Name])))})};
            })).ToArray();
            File.WriteAllText("artifacts/sharlen-analysis/scene.json",JsonSerializer.Serialize(new {Nodes=nodes,Meshes=meshes},new JsonSerializerOptions{WriteIndented=true}));
            foreach(var prefab in report.Items.Where(i=>i.Format==".prefab" && !File.Exists("artifacts/sharlen-analysis/"+i.Guid+".prefab"))) File.WriteAllBytes("artifacts/sharlen-analysis/"+prefab.Guid+".prefab",reader.ReadAssetBytes(report.SourcePath,prefab.Guid));
            Console.WriteLine("PASS Sharlen scene probe"); return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex); return 1;}
    }
    public static int Actual()
    {
        var app=new App(); app.InitializeComponent();
        try
        {
            var reader=new PackageReader(); var report=reader.Read("R:/Sharlen.unitypackage");
            var item=report.Items.Single(i=>i.Path.EndsWith("/Sharlen.fbx"));
            var bytes=reader.ReadAssetBytes(report.SourcePath,item.Guid);
            var poses=FbxBindPose.Read(bytes); if(!poses.ContainsKey("Hair")) throw new Exception("Hair bind pose not found");
            var data=ModelWorker.ImportAsync(new(item.Path,bytes,new()),CancellationToken.None).GetAwaiter().GetResult();
            void Check(ModelPreviewData model,string name)
            {
                var hair=model.Meshes.Where(m=>m.NodeName=="Hair").ToArray();
                var points=hair.SelectMany(m=>Enumerable.Range(0,m.Positions.Length/3).Select(i=>new Vector3(m.Positions[i*3],m.Positions[i*3+1],m.Positions[i*3+2]))).ToArray();
                if(points.Length==0 || points.Min(p=>p.Y)<1.17f || points.Max(p=>p.Y)>1.67f || points.Any(p=>Math.Abs(p.Z)>.21f) || model.Notes.Any(n=>n.Contains("バインド姿勢を解析できない"))) throw new Exception("Hair detached from head: "+name);
                Console.WriteLine($"PASS Sharlen {name}: hair Y=[{points.Min(p=>p.Y):F4},{points.Max(p=>p.Y):F4}], Z=[{points.Min(p=>p.Z):F4},{points.Max(p=>p.Z):F4}]");
            }
            void Render(ModelPreviewData model,PackageItem source,string meta,string label)
            {
                var material=ModelMaterials.LoadDetailed(reader,report,source,meta,model,CancellationToken.None);
                var view=new ModelViewport();view.SetModel(model,material.Materials);var photos=view.CaptureViews();
                File.WriteAllBytes("artifacts/sharlen-analysis/"+label+"-front.png",photos[0]); File.WriteAllBytes("artifacts/sharlen-analysis/"+label+"-side.png",photos[1]);
            }
            Check(data,"FBX"); Render(data,item,TextDecoder.Decode(reader.ReadAssetMetaBytes(report.SourcePath,item.Guid),false,out _),"fixed-fbx");
            foreach(var prefab in report.Items.Where(i=>i.Format==".prefab" && (i.Name.StartsWith("Sharlen"))))
            {
                var result=Task.Run(()=>PrefabPreviewBuilder.BuildAsync(reader,report,prefab,CancellationToken.None)).GetAwaiter().GetResult();
                Check(result.Model,prefab.Name); Render(result.Model,prefab,result.MaterialMetadata,"fixed-"+prefab.Guid);
            }
            return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
        finally {app.Shutdown();}
    }
    private static float[] Matrix(Matrix4x4 m)=>[m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
}
