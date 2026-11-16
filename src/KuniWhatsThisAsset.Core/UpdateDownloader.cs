using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace KuniWhatsThisAsset.Core;
public sealed record UpdateProgress(string Message, long Bytes = 0, long? Total = null);
public sealed record PreparedUpdate(string WorkDirectory, string PackageDirectory, Version Version);
public sealed class UpdateDownloader(HttpClient client, string workBase)
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public static UpdateDownloader CreateDefault() => new(SharedClient, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KuniWhatsThisAsset", "Updates"));
    public async Task<PreparedUpdate> PrepareAsync(AvailableUpdate update, IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string filename = $"KuniWhatsThisAsset-v{update.Version.ToString(3)}-win-x64.zip";
        string expected = "https://raw.githubusercontent.com/PeaceKunihiro/KuniWhatsThisAsset/master/Stable/" + filename;
        if (update.DownloadUri.AbsoluteUri != expected) throw new InvalidDataException("更新元URLが不正です。");
        string work = Path.Combine(Path.GetFullPath(workBase), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        bool complete = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(10));
            progress?.Report(new("チェックサムを取得中…"));
            string checksum = Path.Combine(work, "download.sha256");
            await Download(new Uri(expected[..^4] + ".sha256"), checksum, 4096, null, timeout.Token).ConfigureAwait(false);
            string text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(checksum, timeout.Token).ConfigureAwait(false));
            var match = Regex.Match(text, @"\A([0-9a-fA-F]{64})[ \t]+\*?" + Regex.Escape(filename) + @"\r?\n?\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) throw new InvalidDataException("公開チェックサムを確認できません。");
            string zip = Path.Combine(work, "download.zip");
            await Download(update.DownloadUri, zip, 512L * 1024 * 1024, progress, timeout.Token).ConfigureAwait(false);
            progress?.Report(new("ダウンロード内容を検証中…"));
            using (var stream = File.OpenRead(zip))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, timeout.Token).ConfigureAwait(false)).Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ダウンロードしたZIPのSHA256が一致しません。更新は中止しました。");
            string package = Path.Combine(work, "package");
            await Task.Run(() => UpdateDistribution.Extract(zip, package, update.Version, timeout.Token), timeout.Token).ConfigureAwait(false);
            complete = true; return new(work, package, update.Version);
        }
        finally
        {
            if (!complete) { try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
    private async Task Download(Uri uri, string output, long max, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("KuniWhatsThisAsset-Updater/1.0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
        long? length = response.Content.Headers.ContentLength; if (length > max) throw new InvalidDataException("更新ダウンロードの容量上限を超えています。");
        using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false); using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        byte[] buffer = new byte[65536]; long total = 0; int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += count; if (total > max) throw new InvalidDataException("更新ダウンロードの容量上限を超えています。");
            await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); progress?.Report(new("最新版をダウンロード中…", total, length));
        }
        if (length != null && total != length) throw new InvalidDataException("更新ファイルの受信が完了していません。");
    }
}
