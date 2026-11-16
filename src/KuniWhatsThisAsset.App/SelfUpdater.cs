using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using KuniWhatsThisAsset.Core;
namespace KuniWhatsThisAsset.App;
public interface IAppUpdater
{
    Task InstallAsync(AvailableUpdate update, IProgress<UpdateProgress> progress, CancellationToken token);
}
public sealed record ApplyUpdatePlan(string WorkDirectory, string TargetDirectory, string Version, int ParentId, long ParentStartedUtcTicks, bool Restart = true);
public sealed class SelfUpdater : IAppUpdater
{
    public async Task InstallAsync(AvailableUpdate update, IProgress<UpdateProgress> progress, CancellationToken token)
    {
#if !KWTA_SINGLE_FILE
        await Task.CompletedTask;
        throw new InvalidOperationException("自己更新はStableの配布版で利用できます。GitHubのStableから配布版を入手してください。");
#else
        string target = AppContext.BaseDirectory;
        UpdateInstaller.CheckTarget(target);
        var prepared = await UpdateDownloader.CreateDefault().PrepareAsync(update, progress, token);
        progress.Report(new("更新を準備中… アプリを終了して再起動します。"));
        ValidateExecutable(prepared.PackageDirectory, update.Version);
        string helper = Path.Combine(prepared.WorkDirectory, "updater.exe");
        File.Copy(Environment.ProcessPath!, helper, overwrite: false);
        using var parent = Process.GetCurrentProcess();
        var plan = new ApplyUpdatePlan(prepared.WorkDirectory, Path.GetFullPath(target), update.Version.ToString(3), parent.Id, parent.StartTime.ToUniversalTime().Ticks);
        string planPath = Path.Combine(prepared.WorkDirectory, "plan.json");
        File.WriteAllBytes(planPath, JsonSerializer.SerializeToUtf8Bytes(plan));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = prepared.WorkDirectory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planPath);
        using var process = Process.Start(start) ?? throw new IOException("更新プロセスを起動できません。");
        bool handedOff = false;
        try
        {
            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); readyTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            string? ready = await process.StandardOutput.ReadLineAsync(readyTimeout.Token);
            if (ready != "READY") throw new IOException("更新プロセスを準備できません。現在のアプリは終了していません。" + await process.StandardError.ReadToEndAsync(readyTimeout.Token));
            token.ThrowIfCancellationRequested(); handedOff = true;
            Application.Current.Shutdown();
        }
        finally { if (!handedOff && !process.HasExited) process.Kill(entireProcessTree: true); }
#endif
    }
    internal static void ValidateExecutable(string directory, Version expected)
    {
        var info = FileVersionInfo.GetVersionInfo(UpdateDistribution.FullPath(directory, UpdateDistribution.Executable));
        if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != expected) throw new InvalidDataException("更新本体のバージョンが一致しません。");
    }
    public static async Task<int> ApplyAsync(string planPath)
    {
        ApplyUpdatePlan? plan = null;
        bool readySent = false;
        try
        {
            if (new FileInfo(planPath).Length > 16384) throw new InvalidDataException("更新指示が大きすぎます。");
            plan = JsonSerializer.Deserialize<ApplyUpdatePlan>(File.ReadAllBytes(planPath)) ?? throw new InvalidDataException("更新指示が不正です。");
            string work = Path.GetFullPath(plan.WorkDirectory), target = Path.GetFullPath(plan.TargetDirectory);
            if (!Path.GetFullPath(planPath).Equals(Path.Combine(work, "plan.json"), StringComparison.OrdinalIgnoreCase) || !Version.TryParse(plan.Version, out var version) || plan.ParentId < 1 || plan.ParentId == Environment.ProcessId) throw new InvalidDataException("更新指示の場所・プロセスが不正です。");
            string package = Path.Combine(work, "package");
            UpdateDistribution.Verify(package, version); ValidateExecutable(package, version); UpdateInstaller.CheckTarget(target);
            using var parent = Parent(plan, target);
            await Console.Out.WriteLineAsync("READY"); await Console.Out.FlushAsync();
            readySent = true;
            if (parent != null)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await parent.WaitForExitAsync(wait.Token);
            }
            await Task.Run(() => UpdateInstaller.Apply(package, target, Path.Combine(work, "backup"), version));
            int? restarted = plan.Restart ? StartApplication(target, work) : null;
            File.WriteAllText(Path.Combine(work, "result.json"), JsonSerializer.Serialize(new { Success = true, Version = version.ToString(3), ProcessId = restarted }));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException or AggregateException or System.ComponentModel.Win32Exception)
        {
            try { await Console.Error.WriteLineAsync(ex.Message); } catch (IOException) { } // The original app may already have closed its redirected pipes.
            if (plan?.Restart == true && readySent)
            {
                MessageBox.Show("更新を完了できませんでした。\n" + ex.Message + "\n\n作業ファイル: " + plan.WorkDirectory, "KuniWhatsThisAsset 更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                // The installer restores changed files before returning an error.
                try { StartApplication(plan.TargetDirectory, null); } catch (Exception retry) when (retry is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
            return 1;
        }
    }
    private static Process? Parent(ApplyUpdatePlan plan, string target)
    {
        Process parent;
        try { parent = Process.GetProcessById(plan.ParentId); } catch (ArgumentException) { return null; }
        try
        {
            if (parent.HasExited) { parent.Dispose(); return null; }
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedUtcTicks || !string.Equals(parent.MainModule?.FileName, Path.Combine(target, UpdateDistribution.Executable), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新前アプリのプロセスが一致しません。");
            return parent;
        }
        catch { parent.Dispose(); throw; }
    }
    private static int StartApplication(string target, string? work)
    {
        var start = new ProcessStartInfo(UpdateDistribution.FullPath(target, UpdateDistribution.Executable)) { UseShellExecute = false, WorkingDirectory = target };
        if (work != null) { start.ArgumentList.Add("--update-completed"); start.ArgumentList.Add(work); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        using var process = Process.Start(start) ?? throw new IOException("更新したアプリを再起動できません。");
        return process.Id;
    }
    public static async Task CleanupAsync(string work, int helperId)
    {
        // Only a GUID-named directory below this application's update area can be removed.
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KuniWhatsThisAsset", "Updates") + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(work);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(full), "N", out _) || !string.Equals(Path.GetDirectoryName(full), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            try { using var helper = Process.GetProcessById(helperId); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await helper.WaitForExitAsync(timeout.Token); } catch (ArgumentException) { }
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
            var folders = new Stack<string>(); folders.Push(full); int count = 0;
            while (folders.TryPop(out string? folder))
                foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    var attributes = File.GetAttributes(entry);
                    if (++count > 4096 || (attributes & FileAttributes.ReparsePoint) != 0) return;
                    if ((attributes & FileAttributes.Directory) != 0) folders.Push(entry);
                }
            Directory.Delete(full, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
    }
}
