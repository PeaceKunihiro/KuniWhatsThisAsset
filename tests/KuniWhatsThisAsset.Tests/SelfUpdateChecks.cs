using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KuniWhatsThisAsset.Core;
internal static class SelfUpdateChecks
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static readonly Version Version = new(0,12,0);
    private static readonly AvailableUpdate Update = new(Version,new Uri("https://raw.githubusercontent.com/PeaceKunihiro/KuniWhatsThisAsset/master/Stable/KuniWhatsThisAsset-v0.12.0-win-x64.zip"));
    private static byte[] Manifest(Dictionary<string,byte[]> files,string version="0.12.0")=>JsonSerializer.SerializeToUtf8Bytes(new DistributionManifest(1,version,files.Select(f=>new DistributionFile(f.Key,f.Value.Length,Hash(f.Value))).ToArray()));
    private static void Write(string folder,Dictionary<string,byte[]> files,string version="0.12.0")
    {
        foreach(var (name,bytes) in files) {string path=Path.Combine(folder,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);}
        Directory.CreateDirectory(Path.Combine(folder,"Libs"));File.WriteAllBytes(Path.Combine(folder,UpdateDistribution.ManifestPath),Manifest(files,version));
    }
    private static byte[] Zip(Dictionary<string,byte[]> files,string? extra=null,bool link=false)
    {
        using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
        {
            foreach(var (name,bytes) in files.Append(new(UpdateDistribution.ManifestPath,Manifest(files)))) {using var stream=zip.CreateEntry(name).Open();stream.Write(bytes);}
            if(extra!=null){var entry=zip.CreateEntry(extra);if(link)entry.ExternalAttributes=0xa1ff<<16;using var stream=entry.Open();stream.WriteByte(1);}
        }
        return memory.ToArray();
    }
    public static void Run(string root,Action<string,Action> test,Action<bool,string> check)
    {
        string Folder()=>Path.Combine(root,"self-update-"+Guid.NewGuid().ToString("N"));
        Dictionary<string,byte[]> Files(string text)=>new(){[UpdateDistribution.Executable]=Encoding.UTF8.GetBytes(text),["Libs/runtime.dll"]=[1,2,3]};
        test("manual update checks bypass daily limit and retain automatic once-a-day reservation",()=>
        {
            int calls=0;using var handler=new FakeUpdateHandler((_,_)=>{calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[]")});});using var client=new HttpClient(handler);
            var checker=new UpdateChecker(client,new FileUpdateAttemptStore(Path.Combine(Folder(),"attempt.json")));
            check(checker.CheckDetailedAsync(new(0,11,0),true).Result.Status==UpdateCheckStatus.UpToDate,"Manual initial failed");
            check(checker.CheckDetailedAsync(new(0,11,0),false).Result.Status==UpdateCheckStatus.Skipped,"Manual did not reserve automatic day");
            check(checker.CheckDetailedAsync(new(0,11,0),true).Result.Status==UpdateCheckStatus.UpToDate && calls==2,"Manual was limited");
        });
        test("manual malformed/HTTP failure is unavailable rather than up to date",()=>
        {
            using var handler=new FakeUpdateHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{}")}));using var client=new HttpClient(handler);
            check(new UpdateChecker(client,new FileUpdateAttemptStore(Path.Combine(Folder(),"attempt.json"))).CheckDetailedAsync(new(0,11,0),true).Result.Status==UpdateCheckStatus.Unavailable,"Invalid list reported latest");
        });
        test("update download validates SHA256 and file manifest before staging",()=>
        {
            byte[] zip=Zip(Files("new"));var calls=new List<Uri>();
            using var handler=new FakeUpdateHandler((request,_)=>{calls.Add(request.RequestUri!);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=request.RequestUri!.AbsolutePath.EndsWith(".sha256")?new ByteArrayContent(Encoding.UTF8.GetBytes(Hash(zip)+"  KuniWhatsThisAsset-v0.12.0-win-x64.zip\n")):new ByteArrayContent(zip)});});using var client=new HttpClient(handler);
            var prepared=new UpdateDownloader(client,Folder()).PrepareAsync(Update).GetAwaiter().GetResult();check(calls.Count==2 && UpdateDistribution.Verify(prepared.PackageDirectory,Version).Files.Length==2,"Staging verification failed");
        });
        test("update bad checksum, redirects, cancellation and external URL leave installation untouched",()=>
        {
            foreach(int mode in new[]{0,1,2,3})
            {
                string work=Folder();using var cancel=new CancellationTokenSource();if(mode==2)cancel.Cancel();
                using var handler=new FakeUpdateHandler((_,_)=>Task.FromResult(new HttpResponseMessage(mode==1?HttpStatusCode.Redirect:HttpStatusCode.OK){Content=new StringContent(new string('0',64)+"  KuniWhatsThisAsset-v0.12.0-win-x64.zip\n")}));using var client=new HttpClient(handler);
                bool refused=false;try{new UpdateDownloader(client,work).PrepareAsync(mode==3?Update with{DownloadUri=new Uri("https://example.invalid/payload.zip")}:Update,cancellationToken:cancel.Token).GetAwaiter().GetResult();}catch(Exception ex) when(ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException){refused=true;}
                check(refused && (!Directory.Exists(work)||!Directory.EnumerateFileSystemEntries(work).Any()),"Bad download was staged or left work behind");
            }
        });
        test("update ZIP traversal, case aliases, symlinks and settings entries are rejected",()=>
        {
            foreach(var entry in new[]{"../escape.dll","Libs/../../escape.dll","preferences.json","Libs/preferences.json","kuniwhatsthisasset.app.exe","Libs/link.dll","Libs/NUL.dll","Libs/end./test.dll"})
            {
                string file=Folder()+".zip";File.WriteAllBytes(file,Zip(Files("new"),entry,entry.EndsWith("link.dll")));
                bool refused=false;try{UpdateDistribution.Extract(file,Folder(),Version,CancellationToken.None);}catch(InvalidDataException){refused=true;}check(refused,"Unsafe ZIP accepted: "+entry);
            }
        });
        test("installer preserves settings, cache, custom files and modified obsolete dependencies",()=>
        {
            string install=Folder(),staged=Folder();var old=Files("old");old["Libs/obsolete.dll"]=[7];old["Libs/modified.dll"]=[8];Write(install,old,"0.11.0");Write(staged,Files("new"));
            File.WriteAllText(Path.Combine(install,"preferences.json"),"user settings");Directory.CreateDirectory(Path.Combine(install,"RenderCache"));File.WriteAllText(Path.Combine(install,"RenderCache","photo.txt"),"cache");File.WriteAllText(Path.Combine(install,"custom.txt"),"custom");File.WriteAllBytes(Path.Combine(install,"Libs/modified.dll"),[9]);
            UpdateInstaller.Apply(staged,install,Folder(),Version);
            check(File.ReadAllText(Path.Combine(install,"preferences.json"))=="user settings" && File.ReadAllText(Path.Combine(install,"RenderCache/photo.txt"))=="cache" && File.ReadAllText(Path.Combine(install,"custom.txt"))=="custom","User data overwritten");
            check(!File.Exists(Path.Combine(install,"Libs/obsolete.dll")) && File.ReadAllBytes(Path.Combine(install,"Libs/modified.dll"))[0]==9 && File.ReadAllText(Path.Combine(install,UpdateDistribution.Executable))=="new","Managed file transition failed");
        });
        test("installer rolls back replaced executable when a later dependency is locked",()=>
        {
            string install=Folder(),staged=Folder();Write(install,Files("old"),"0.11.0");Write(staged,Files("new"));
            using var locked=new FileStream(Path.Combine(install,"Libs/runtime.dll"),FileMode.Open,FileAccess.Read,FileShare.Read);
            bool refused=false;try{UpdateInstaller.Apply(staged,install,Folder(),Version);}catch(IOException){refused=true;}
            check(refused && File.ReadAllText(Path.Combine(install,UpdateDistribution.Executable))=="old" && UpdateDistribution.Verify(install,new(0,11,0)).Files.Length==2,"Failed update did not restore old files");
        });
        test("installer rejects staged tampering and unknown-file collisions",()=>
        {
            string install=Folder(),staged=Folder();Write(install,Files("old"),"0.11.0");Write(staged,Files("new"));File.WriteAllText(Path.Combine(staged,UpdateDistribution.Executable),"tampered");
            bool refused=false;try{UpdateInstaller.Apply(staged,install,Folder(),Version);}catch(InvalidDataException){refused=true;}check(refused && File.ReadAllText(Path.Combine(install,UpdateDistribution.Executable))=="old","Tampered stage installed");
            staged=Folder();var next=Files("new");next["Libs/custom.dll"]=[5];Write(staged,next);File.WriteAllBytes(Path.Combine(install,"Libs/custom.dll"),[6]);
            refused=false;try{UpdateInstaller.Apply(staged,install,Folder(),Version);}catch(IOException){refused=true;}check(refused && File.ReadAllBytes(Path.Combine(install,"Libs/custom.dll"))[0]==6 && File.ReadAllText(Path.Combine(install,UpdateDistribution.Executable))=="old","Unknown file overwritten");
        });
    }
}
