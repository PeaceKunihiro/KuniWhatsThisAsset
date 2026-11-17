using System.IO.Compression;
using System.Text;
using KuniWhatsThisAsset.Core;
internal static class TarExtensionChecks
{
    private static readonly string GuidA=new('a',32);
    private static byte[] U(string text)=>Encoding.UTF8.GetBytes(text);
    private static byte[] Header(string name,int size,byte type)
    {
        byte[] h=new byte[512];U(name).CopyTo(h,0);U(Convert.ToString(size,8).PadLeft(11,'0')).CopyTo(h,124);h[156]=type;"ustar\0"u8.CopyTo(h.AsSpan(257));h[263]=h[264]=(byte)'0';h.AsSpan(148,8).Fill(32);U(Convert.ToString(h.Sum(b=>(int)b),8).PadLeft(6,'0')).CopyTo(h,148);h[154]=0;h[155]=32;return h;
    }
    private static byte[] Record(string name,byte[] data,byte type=(byte)'0',int? size=null)=>[..Header(name,size??data.Length,type),..data,..new byte[(512-data.Length%512)%512]];
    private static byte[] Pax(params (string Key,string Value)[] values)
    {
        var result=new List<byte>();foreach(var (key,value)in values){string body=key+"="+value+"\n";int count=U(body).Length+2;while(true){string line=count+" "+body;int actual=U(line).Length;if(actual==count){result.AddRange(U(line));break;}count=actual;}}return result.ToArray();
    }
    public static void Run(string output,Action<string,Action> test,Action<bool,string> check)
    {
        string File(byte[] tar){string path=Path.Combine(output,"extensions-"+Guid.NewGuid()+".unitypackage");using var zip=new GZipStream(System.IO.File.Create(path),CompressionLevel.Fastest);zip.Write(tar);return path;}
        byte[] PathRecord()=>Record(GuidA+"/pathname",U("Assets/a.bin"));
        byte[] Meta()=>Record(GuidA+"/asset.meta",U("fileFormatVersion: 2\n"));
        test("GNU archives with conventional dot-root prefixes normalize member names",()=>
        {
            byte[] tar=[..Record("./",[],(byte)'5'),..Record("./"+GuidA+"/asset",[1]),..Record("./"+GuidA+"/pathname",U("Assets/a.txt")),..Record("./"+GuidA+"/asset.meta",U("fileFormatVersion: 2\n")),..new byte[1024]];
            string file=File(tar);var source=PackageRepackager.Analyze(file,new());string target=Path.Combine(output,"dot-root-"+Guid.NewGuid()+".unitypackage");PackageRepackager.Save(PackageRepackager.Plan(source,"Grouped"),target);check(new PackageReader().Read(target).Items.Any(i=>i.Path=="Assets/Grouped/a.txt"),"Dot-root layout not preserved");
        });
        test("total pathname budget can be explicitly approved without weakening integrity",()=>
        {
            string file=File([..Record(GuidA+"/asset",[1]),..PathRecord(),..Meta(),..new byte[1024]]);bool refused=false;var limits=new ScanLimits{MaxPathBytes=4};
            try{new PackageReader(limits).Read(file);}catch(ReadLimitExceededException ex){refused=ex.Kind==ReadLimitKind.PathBytes;}check(refused,"Path memory budget ignored");check(new PackageReader(limits with{IgnoreReadLimits=true}).Read(file).Items.Count==1,"Approved pathname read failed");
        });
        test("PAX size applies to the payload, not intermediary GNU LongLink",()=>
        {
            byte[] tar=[..Record("pax",Pax(("path",GuidA+"/asset"),("size","4")),(byte)'x'),..Record("././@LongLink",U(GuidA+"/asset\0"),(byte)'L'),..Record("ignored",[1,2,3,4],size:0),..PathRecord(),..Meta(),..new byte[1024]];
            string file=File(tar);check(new PackageReader().ReadAssetBytes(file,GuidA).SequenceEqual(new byte[]{1,2,3,4}),"Size override affected a control record");
        });
        test("PAX global/local overrides and empty-value deletion are scoped",()=>
        {
            byte[] tar=[..Record("global",Pax(("path",new string('b',32)+"/asset")),(byte)'g'),..Record("local",Pax(("path",GuidA+"/asset")),(byte)'x'),..Record("ignored",[1,2]),..Record("local",Pax(("path","")),(byte)'x'),..PathRecord(),..Record("global",Pax(("path","")),(byte)'g'),..Meta(),..new byte[1024]];
            check(new PackageReader().Read(File(tar)).Items.Single().Path=="Assets/a.bin","PAX global state leaked across entries");
        });
        test("malformed extensions, traversal, links and dangling LongLink are rejected",()=>
        {
            var headers=new List<byte[]>{Record("pax",U("0 path=bad\n"),(byte)'x'),Record("pax",U("999 path=bad\n"),(byte)'x'),Record("pax",Pax(("path","../escape")),(byte)'x'),Record("pax",Pax(("linkpath","outside")),(byte)'x'),Record("pax",Pax(("GNU.sparse.size","4")),(byte)'x'),Record("././@LongLink",U("../escape\0"),(byte)'L'),Record("pax",Pax(("size","-1")),(byte)'x')};
            foreach(var header in headers)
            {
                bool rejected=false;try{new PackageReader().Read(File([..header,..Record(GuidA+"/asset",[1]),..PathRecord(),..Meta(),..new byte[1024]]));}catch(Exception ex)when(ex is InvalidDataException or EndOfStreamException){rejected=true;}check(rejected,"Unsafe extension accepted");
            }
            bool dangling=false;try{new PackageReader().Read(File([..Record("././@LongLink",U(GuidA+"/asset\0"),(byte)'L'),..new byte[1024]]));}catch(InvalidDataException){dangling=true;}check(dangling,"Unmatched LongLink accepted");
        });
        test("extension memory/physical-entry limits precede allocation even on approved reads",()=>
        {
            foreach(bool approved in new[]{false,true})
            {
                bool refused=false;try{new PackageReader(new(){IgnoreReadLimits=approved}).Read(File([..Header("pax",1024*1024+1,(byte)'x'),..new byte[1024]]));}catch(InvalidDataException){refused=true;}check(refused,"Oversized extension payload was read");
            }
            string file=File([..Record("pax",Pax(("comment","metadata counts")),(byte)'x'),..Record(GuidA+"/asset",[1]),..PathRecord(),..Meta(),..new byte[1024]]);
            bool limited=false;try{new PackageReader(new(){MaxEntries=1}).Read(file);}catch(ReadLimitExceededException ex){limited=ex.Kind==ReadLimitKind.Entries;}check(limited,"Metadata not included in entry limit");
        });
    }
}
