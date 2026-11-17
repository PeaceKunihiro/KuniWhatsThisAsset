using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KuniWhatsThisAsset.Core;
internal static class RepackageChecks
{
    private static readonly string GuidA = new('a',32), GuidB = new('b',32);
    private static readonly byte[] Asset = Encoding.UTF8.GetBytes("// payload stays identical\n"), Meta = Encoding.UTF8.GetBytes($"fileFormatVersion: 2\nguid: {GuidA}\n");
    private static byte[] Utf8(string value)=>Encoding.UTF8.GetBytes(value);
    public static void Run(string output, Action<string,Action> test, Action<bool,string> check)
    {
        string FileName(string name)=>Path.Combine(output,"repack-"+name+"-"+Guid.NewGuid().ToString("N")+".unitypackage");
        string Make(string path,TarEntryFormat format=TarEntryFormat.Ustar,bool root=false,bool extra=false,string? second=null)
        {
            string file=FileName("source"); using var fs=File.Create(file);using var gzip=new GZipStream(fs,CompressionLevel.Fastest);using var writer=new TarWriter(gzip,format);
            void Add(string name,byte[] bytes)
            {
                using var stream=new MemoryStream(bytes);
                TarEntry entry=format switch{TarEntryFormat.Pax=>new PaxTarEntry(TarEntryType.RegularFile,name),TarEntryFormat.Gnu=>new GnuTarEntry(TarEntryType.RegularFile,name),_=>new UstarTarEntry(TarEntryType.RegularFile,name)};entry.DataStream=stream;writer.WriteEntry(entry);
            }
            Add(GuidA+"/asset",Asset);Add(GuidA+"/asset.meta",Meta);Add(GuidA+"/pathname",Utf8(path));
            if(root){Add(GuidB+"/pathname",Utf8("Assets"));Add(GuidB+"/asset.meta",Utf8($"fileFormatVersion: 2\nguid: {GuidB}\nfolderAsset: yes\n"));}
            if(extra)Add(GuidA+"/"+new string('x',140)+".dat",[0,1,2,3]);
            if(second!=null){string guid=new('c',32);Add(guid+"/pathname",Utf8(second));Add(guid+"/asset",[9]);Add(guid+"/asset.meta",Utf8("fileFormatVersion: 2\n"));}
            return file;
        }
        Dictionary<string,byte[]> Members(string file)
        {
            using var fs=File.OpenRead(file);using var gzip=new GZipStream(fs,CompressionMode.Decompress);using var tar=new TarReader(gzip);var result=new Dictionary<string,byte[]>();TarEntry? entry;
            while((entry=tar.GetNextEntry())!=null){if(entry.EntryType==TarEntryType.Directory)continue;using var data=new MemoryStream();entry.DataStream?.CopyTo(data);result.Add(entry.Name,data.ToArray());}return result;
        }
        test("GNU and PAX archives preserve long pathname and long member names",()=>
        {
            string path="Assets/"+string.Join('/',Enumerable.Repeat("日本語の長いフォルダー",180))+"/data.txt";
            foreach(var format in new[]{TarEntryFormat.Gnu,TarEntryFormat.Pax})
            {
                string file=Make(path,format,extra:true);var source=PackageRepackager.Analyze(file,new());check(source.Report.Items.Single().Path==path,"Long pathname was truncated");
                string target=FileName("long-output");var plan=PackageRepackager.Plan(source,"まとめ");PackageRepackager.Save(plan,target);
                var members=Members(target);check(Encoding.UTF8.GetString(members[GuidA+"/pathname"])=="Assets/まとめ"+path[6..],"Long target path mismatch");check(members[GuidA+"/asset"].SequenceEqual(Asset)&&members[GuidA+"/asset.meta"].SequenceEqual(Meta)&&members.Keys.Any(k=>k.Length>100),"Payload/meta/long member lost");
            }
        });
        test("repackage keeps original, GUID and every asset/meta/preview member",()=>
        {
            string file=Make("Assets/Avatar/a.txt");byte[] original=File.ReadAllBytes(file);var source=PackageRepackager.Analyze(file,new());var plan=PackageRepackager.Plan(source,"Grouped");string target=FileName("output");
            var result=PackageRepackager.Save(plan,target);var report=new PackageReader().Read(target);var members=Members(target);
            check(File.ReadAllBytes(file).SequenceEqual(original)&&report.Items.Any(i=>i.Guid==GuidA&&i.Path=="Assets/Grouped/Avatar/a.txt")&&report.Items.Count==2,"Original/GUID/layout not preserved");
            check(members[GuidA+"/asset"].SequenceEqual(Asset)&&members[GuidA+"/asset.meta"].SequenceEqual(Meta)&&plan.AddedFolderGuid!=null&&members.ContainsKey(plan.AddedFolderGuid+"/asset.meta"),"Folder metadata incorrect");
            check(result.Sha256==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))),"Result hash mismatch");
        });
        test("existing Assets folder GUID/meta is reused without adding another folder",()=>
        {
            string file=Make("Assets/a.txt",root:true),target=FileName("root-output");var plan=PackageRepackager.Plan(PackageRepackager.Analyze(file,new()),"Grouped");PackageRepackager.Save(plan,target);
            var before=Members(file);var after=Members(target);check(plan.AddedFolderGuid==null&&new PackageReader().Read(target).Items.Count==2&&before[GuidB+"/asset.meta"].SequenceEqual(after[GuidB+"/asset.meta"]),"Root metadata changed");
        });
        test("special root folders stay in place only with explicit preservation",()=>
        {
            string file=Make("Assets/StreamingAssets/data.txt",second:"Assets/Avatar/a.bin");var source=PackageRepackager.Analyze(file,new());bool refused=false;try{PackageRepackager.Plan(source,"Grouped",false);}catch(InvalidDataException){refused=true;}check(refused,"Special directory moved silently");
            string target=FileName("special-output");PackageRepackager.Save(PackageRepackager.Plan(source,"Grouped",true),target);var paths=new PackageReader().Read(target).Items.Select(i=>i.Path).ToArray();check(paths.Contains("Assets/StreamingAssets/data.txt")&&paths.Contains("Assets/Grouped/Avatar/a.bin"),"Exception paths wrong");
        });
        test("repackage rejects input changes, overwrite, invalid folder and cancellation",()=>
        {
            string file=Make("Assets/a.txt");var plan=PackageRepackager.Plan(PackageRepackager.Analyze(file,new()),"Grouped");string target=FileName("refused-output");
            foreach(string name in new[]{"../bad","a/b","NUL","StreamingAssets","a."}){bool refused=false;try{PackageRepackager.Plan(plan.Source,name);}catch(InvalidDataException){refused=true;}check(refused,"Invalid group name accepted");}
            File.WriteAllText(target,"keep");bool blocked=false;try{PackageRepackager.Save(plan,target);}catch(IOException){blocked=true;}check(blocked&&File.ReadAllText(target)=="keep","Existing file overwritten");
            using var cancel=new CancellationTokenSource();cancel.Cancel();string canceled=FileName("cancel-output");try{PackageRepackager.Save(plan,canceled,token:cancel.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}check(!File.Exists(canceled),"Canceled output committed");
            File.WriteAllBytes(file,[1,2,3]);blocked=false;try{PackageRepackager.Save(plan,FileName("changed-output"));}catch(IOException){blocked=true;}check(blocked,"Changed source accepted");
        });
        test("repackage rejects project settings and file/folder collisions",()=>
        {
            string outside=Make("ProjectSettings/a.asset");bool refused=false;try{PackageRepackager.Analyze(outside,new());}catch(InvalidDataException){refused=true;}check(refused,"Non-Assets silently dropped");
            string collision=Make("Assets/a",second:"Assets/a/b.txt");refused=false;try{PackageRepackager.Plan(PackageRepackager.Analyze(collision,new()),"Grouped");}catch(InvalidDataException){refused=true;}check(refused,"File ancestor conflict accepted");
        });
        test("long filesystem paths can be read and saved",()=>
        {
            string folder=Path.Combine(output,"long-"+Guid.NewGuid().ToString("N"));for(int i=0;i<10;i++)folder=Path.Combine(folder,"directory-1234567890123456789");Directory.CreateDirectory(folder);
            string original=Make("Assets/a.txt"),file=Path.Combine(folder,"source.unitypackage"),target=Path.Combine(folder,"grouped.unitypackage");File.Copy(original,file);
            PackageRepackager.Save(PackageRepackager.Plan(PackageRepackager.Analyze(file,new()),"Grouped"),target);check(new PackageReader().Read(target).Items.Count==2,"Long filesystem output failed");
        });
    }
}
