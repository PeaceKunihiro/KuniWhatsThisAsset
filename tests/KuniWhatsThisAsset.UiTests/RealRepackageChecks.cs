using System.IO;
using KuniWhatsThisAsset.Core;
internal static class RealRepackageChecks
{
    public static int Run()
    {
        try
        {
            string directory=Path.GetFullPath("artifacts/repackage-real");Directory.CreateDirectory(directory);
            foreach(var (label,path)in new[]{("Fuzzy","E:/GPT_WORKS/KWT_TEST/Fuzzy_ver.2.00/Fuzzy.unitypackage"),("Luchika","E:/GPT_WORKS/KWT_TEST/Luchika_ver.1.00/Luchika.unitypackage")})
            {
                var source=PackageRepackager.Analyze(path,new());var plan=PackageRepackager.Plan(source,label+"_まとめ");
                string output=Path.Combine(directory,label+"-"+Guid.NewGuid().ToString("N")+".unitypackage");var result=PackageRepackager.Save(plan,output);
                var report=new PackageReader(new(){IgnoreReadLimits=true}).Read(output);
                if(report.FileCount!=source.Report.FileCount||report.Items.Any(i=>i.Guid!=plan.AddedFolderGuid&&!source.Report.Items.Any(old=>old.Guid==i.Guid&&old.Size==i.Size)))throw new Exception("Real content changed");
                Console.WriteLine($"PASS real {label} repackage: {source.Report.Items.Count} -> {report.Items.Count} assets, {report.FileCount} payload files, all member hashes verified; {new FileInfo(output).Length} bytes");
            }
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
