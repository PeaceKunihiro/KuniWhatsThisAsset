namespace KuniWhatsThisAsset.Core;
public static class UpdateInstaller
{
    public static void CheckTarget(string target)
    {
        string root = Path.GetFullPath(target);
        if (root.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(UpdateDistribution.FullPath(root, UpdateDistribution.Executable))) throw new InvalidDataException("アプリの更新先フォルダーが不正です。");
        string probe = Path.Combine(root, ".kwta-write-" + Guid.NewGuid().ToString("N"));
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }
    public static void Apply(string staged, string target, string backup, Version expected)
    {
        var manifest = UpdateDistribution.Verify(staged, expected); CheckTarget(target);
        string root = Path.GetFullPath(target), stage = Path.GetFullPath(staged);
        if (root.Equals(stage, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(backup).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新作業フォルダーが不正です。");
        using var installLock = new FileStream(Path.Combine(root, ".kwta-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        string installedManifest = UpdateDistribution.FullPath(root, UpdateDistribution.ManifestPath);
        DistributionManifest? previous = null;
        if (File.Exists(installedManifest))
        {
            if (new FileInfo(installedManifest).Length > 256 * 1024) throw new InvalidDataException("既存の配布一覧が不正です。");
            previous = UpdateDistribution.Parse(File.ReadAllBytes(installedManifest));
        }
        var nextNames = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = (previous?.Files ?? []).Where(f => !nextNames.Contains(f.Path) && UpdateDistribution.Matches(UpdateDistribution.FullPath(root, f.Path), f)).Select(f => f.Path).ToArray();
        var touched = manifest.Files.Select(f => f.Path).Concat(stale).Append(UpdateDistribution.ManifestPath).ToArray();
        Directory.CreateDirectory(backup);
        var existed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owned = previous?.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Back up everything before the first replacement. Only distribution-owned paths are touched.
        foreach (string relative in touched)
        {
            string destination = UpdateDistribution.FullPath(root, relative);
            if (Directory.Exists(destination)) throw new IOException("更新ファイルの位置にフォルダーがあります: " + relative);
            if (File.Exists(destination))
            {
                if (relative != UpdateDistribution.ManifestPath && owned != null && !owned.Contains(relative)) throw new IOException("既存のユーザーファイルと更新ファイルが重複しています: " + relative);
                string saved = UpdateDistribution.FullPath(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(destination, saved, overwrite: false); existed.Add(relative);
            }
        }
        var changed = new List<string>();
        try
        {
            foreach (var file in manifest.Files)
            {
                Replace(UpdateDistribution.FullPath(stage, file.Path), UpdateDistribution.FullPath(root, file.Path)); changed.Add(file.Path);
            }
            foreach (string relative in stale) { File.Delete(UpdateDistribution.FullPath(root, relative)); changed.Add(relative); }
            Replace(UpdateDistribution.FullPath(stage, UpdateDistribution.ManifestPath), installedManifest); changed.Add(UpdateDistribution.ManifestPath);
            UpdateDistribution.Verify(root, expected);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            var rollbackErrors = new List<Exception>();
            foreach (string relative in changed.AsEnumerable().Reverse())
            {
                try
                {
                    string destination = UpdateDistribution.FullPath(root, relative);
                    if (existed.Contains(relative)) Replace(UpdateDistribution.FullPath(backup, relative), destination);
                    else File.Delete(destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { rollbackErrors.Add(ex); }
            }
            if (rollbackErrors.Count > 0) throw new AggregateException("更新と復元に失敗しました。作業フォルダーのbackupを保持しています: " + backup, new[] { failure }.Concat(rollbackErrors));
            throw new IOException("更新に失敗したため、変更したファイルを元に戻しました。", failure);
        }
    }
    private static void Replace(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".kwta-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, destination, overwrite: true); break; }
                catch (IOException ex) when (attempt < 30 && (ex.HResult & 0xffff) is 32 or 33) { Thread.Sleep(100); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
