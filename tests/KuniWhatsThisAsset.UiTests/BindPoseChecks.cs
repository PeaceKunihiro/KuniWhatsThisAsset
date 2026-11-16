using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using KuniWhatsThisAsset.App;
internal static class BindPoseChecks
{
    private sealed record Element(string Name, object[] Values, Element[] Children);
    public static void Run()
    {
        void Check(bool ok,string why) { if(!ok) throw new Exception(why); }
        byte[] Fixture(bool wide,bool compressed,bool conflict=false,bool duplicateName=false,bool rest=false)
        {
            using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream);
            writer.Write("Kaydara FBX Binary  \0\u001a\0"u8);writer.Write(wide?7500:7400);
            void Number(long n) { if(wide) writer.Write(n); else writer.Write(checked((int)n)); }
            void Write(Element node)
            {
                long start=stream.Position; Number(0);Number(node.Values.Length);Number(0);var name=Encoding.ASCII.GetBytes(node.Name);writer.Write((byte)name.Length);writer.Write(name);long begin=stream.Position;
                foreach(var value in node.Values)
                {
                    if(value is long id) {writer.Write((byte)'L');writer.Write(id);}
                    else if(value is string text) {var data=Encoding.UTF8.GetBytes(text);writer.Write((byte)'S');writer.Write(data.Length);writer.Write(data);}
                    else if(value is double[] matrix)
                    {
                        byte[] data=matrix.SelectMany(BitConverter.GetBytes).ToArray();
                        if(compressed) {using var memory=new MemoryStream();using(var zip=new ZLibStream(memory,CompressionLevel.Fastest,true)) zip.Write(data);data=memory.ToArray();}
                        writer.Write((byte)'d');writer.Write(matrix.Length);writer.Write(compressed?1:0);writer.Write(data.Length);writer.Write(data);
                    }
                }
                long size=stream.Position-begin;foreach(var child in node.Children) Write(child);
                if(node.Children.Length>0) writer.Write(new byte[wide?25:13]);long end=stream.Position;
                stream.Position=start;Number(end);Number(node.Values.Length);Number(size);stream.Position=end;
            }
            double[] matrix=[1,0,0,0,0,0,-1,0,0,1,0,0,2,3,4,1];
            Element Pose(double[] m)=>new("PoseNode",[],[new("Node",[1L],[]),new("Matrix",[m],[])]);
            var nodes=new List<Element>{new("Model",[1L,"Hair\0\u0001Model","Mesh"],[])};
            if(duplicateName) nodes.Add(new("Model",[2L,"Hair\0\u0001Model","Mesh"],[]));
            var poses=new List<Element>{Pose(matrix)};
            if(conflict) {var other=(double[])matrix.Clone();other[12]=9;poses.Add(Pose(other));}
            nodes.Add(new("Pose",[3L,"Pose\0\u0001Pose",rest?"RestPose":"BindPose"],poses.ToArray()));
            Write(new("Objects",[],nodes.ToArray()));writer.Write(new byte[wide?25:13]);return stream.ToArray();
        }
        foreach(bool wide in new[]{false,true}) foreach(bool compressed in new[]{false,true})
        {
            var result=FbxBindPose.Read(Fixture(wide,compressed));
            Check(result.Count==1 && Vector3.Distance(Vector3.Transform(new(0,0,1),result["Hair"]),new(2,4,4))<1e-6,"FBX bind matrix orientation/translation/array encoding");
        }
        Check(FbxBindPose.Read(Fixture(false,false,conflict:true)).Count==0,"Conflicting bind poses were guessed");
        Check(FbxBindPose.Read(Fixture(false,false,duplicateName:true)).Count==0,"Duplicate model names were guessed");
        Check(FbxBindPose.Read(Fixture(false,false,rest:true)).Count==0,"Rest pose was treated as bind pose");
        Check(FbxBindPose.Read(Encoding.UTF8.GetBytes("; FBX 7.4.0 project file\n")).Count==0,"ASCII fallback changed");
        var bad=Fixture(false,false);BitConverter.GetBytes(int.MaxValue).CopyTo(bad,27);
        bool refused=false;try{FbxBindPose.Read(bad);}catch(InvalidDataException){refused=true;}Check(refused,"Invalid node offset accepted");
        Console.WriteLine("PASS binary FBX 7400/7500 bind pose, compressed matrices, orientation, conflicting poses/names and malformed offset");
    }
}
