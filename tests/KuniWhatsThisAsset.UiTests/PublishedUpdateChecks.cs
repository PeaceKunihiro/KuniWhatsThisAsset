using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;
internal static class PublishedUpdateChecks
{
    public static int Run()
    {
        Process? parent=null,helper=null,restarted=null;
        try
        {
            string work=Path.GetFullPath("artifacts/published-update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
            string staged=Path.Combine(work,"package"),target=Path.Combine(work,"installed");
            var version=new Version(0,11,0);
            UpdateDistribution.Extract(Path.GetFullPath("Stable/KuniWhatsThisAsset-v0.11.0-win-x64.zip"),staged,version,CancellationToken.None);
            string old=Path.GetFullPath("artifacts/KuniWhatsThisAsset-v0.10.1-win-x64");
            foreach(string file in Directory.EnumerateFiles(old,"*",SearchOption.AllDirectories)) {string output=Path.Combine(target,Path.GetRelativePath(old,file));Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.Copy(file,output);}
            File.WriteAllText(Path.Combine(target,"preferences.json"),"preserve-user-settings");Directory.CreateDirectory(Path.Combine(target,"RenderCache"));File.WriteAllText(Path.Combine(target,"RenderCache","test.txt"),"preserve-cache");
            var start=new ProcessStartInfo(Path.Combine(target,UpdateDistribution.Executable)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--model-worker");
            parent=Process.Start(start)!;var parentOutput=parent.StandardOutput.ReadToEndAsync();var parentError=parent.StandardError.ReadToEndAsync();
            string helperExe=Path.Combine(work,"updater.exe");File.Copy(Path.Combine(staged,UpdateDistribution.Executable),helperExe);
            var plan=new ApplyUpdatePlan(work,target,version.ToString(3),parent.Id,parent.StartTime.ToUniversalTime().Ticks);
            string planFile=Path.Combine(work,"plan.json");File.WriteAllBytes(planFile,JsonSerializer.SerializeToUtf8Bytes(plan));
            var helperStart=new ProcessStartInfo(helperExe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};helperStart.ArgumentList.Add("--apply-update");helperStart.ArgumentList.Add(planFile);
            helper=Process.Start(helperStart)!;var error=helper.StandardError.ReadToEndAsync();
            string? ready=helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();if(ready!="READY")throw new Exception("Update helper not ready: "+error.GetAwaiter().GetResult());
            Thread.Sleep(200);
            if(FileVersionInfo.GetVersionInfo(Path.Combine(target,UpdateDistribution.Executable)).FileMinorPart!=10 || helper.HasExited)throw new Exception("Replaced executable before parent exit");
            using(var writer=new BinaryWriter(parent.StandardInput.BaseStream,Encoding.UTF8,false))
            {
                byte[] name=Encoding.UTF8.GetBytes("Assets/triangle.obj"),obj=Encoding.UTF8.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");writer.Write(name.Length);writer.Write(name);writer.Write(obj.Length);writer.Write(obj);writer.Write(0);writer.Write(0);
            }
            if(!parent.WaitForExit(20000)||parent.ExitCode!=0)throw new Exception("Old worker failed: "+parentError.GetAwaiter().GetResult());
            if(!helper.WaitForExit(30000)||helper.ExitCode!=0)throw new Exception("Update helper failed: "+error.GetAwaiter().GetResult());
            UpdateDistribution.Verify(target,version);
            using var result=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(work,"result.json")));int pid=result.RootElement.GetProperty("ProcessId").GetInt32();restarted=Process.GetProcessById(pid);
            var clock=Stopwatch.StartNew();while(clock.Elapsed<TimeSpan.FromSeconds(20)){restarted.Refresh();if(restarted.HasExited)throw new Exception("Restarted app exited early");if(restarted.MainWindowHandle!=IntPtr.Zero && restarted.MainWindowTitle.Contains("v0.11.0"))break;Thread.Sleep(100);}
            if(restarted.MainWindowHandle==IntPtr.Zero||!restarted.MainWindowTitle.Contains("v0.11.0"))throw new Exception("New application window did not initialize: "+restarted.MainWindowTitle);
            if(File.ReadAllText(Path.Combine(target,"preferences.json"))!="preserve-user-settings"||File.ReadAllText(Path.Combine(target,"RenderCache/test.txt"))!="preserve-cache")throw new Exception("User settings/cache changed");
            restarted.CloseMainWindow();if(!restarted.WaitForExit(10000))throw new Exception("Updated window did not close");
            Console.WriteLine("PASS published single-file helper: waits for v0.10.1, replaces with v0.11.0, restarts WPF window, preserves settings/cache and old unowned files");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally {foreach(var process in new[]{parent,helper,restarted})if(process!=null){try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}process.Dispose();}}
    }
}
