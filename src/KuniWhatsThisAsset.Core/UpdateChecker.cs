using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KuniWhatsThisAsset.Core;

public sealed record AvailableUpdate(Version Version, Uri DownloadUri);
public enum UpdateCheckStatus { Skipped, UpToDate, Available, Unavailable }
public sealed record UpdateCheckResult(UpdateCheckStatus Status, AvailableUpdate? Update = null);
public interface IUpdateAttemptStore { bool TryReserve(DateTimeOffset now); }
public sealed class FileUpdateAttemptStore(string path) : IUpdateAttemptStore
{
    private sealed record State(DateOnly LastAttemptDate, DateTimeOffset AttemptedAtUtc);
    public bool TryReserve(DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            FileStream stream;
            bool created;
            try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); created = true; }
            catch (IOException) { stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); created = false; }
            using (stream)
            {
                var today = DateOnly.FromDateTime(now.DateTime);
                if (!created)
                {
                    if (stream.Length is 0 or > 4096) return false;
                    var previous = JsonSerializer.Deserialize<State>(stream);
                    if (previous == null || previous.LastAttemptDate == default || previous.LastAttemptDate >= today) return false;
                }
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(new State(today, now.ToUniversalTime()));
                stream.Position = 0; stream.Write(data); stream.SetLength(data.Length); stream.Flush(flushToDisk: true);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return false; }
    }
}

public sealed class UpdateChecker(HttpClient client, IUpdateAttemptStore store, Func<DateTimeOffset>? clock = null)
{
    public static readonly Uri StableUri = new("https://github.com/PeaceKunihiro/KuniWhatsThisAsset/tree/master/Stable");
    public static readonly Uri ApiUri = new("https://api.github.com/repos/PeaceKunihiro/KuniWhatsThisAsset/contents/Stable?ref=master");
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public static UpdateChecker CreateDefault() => new(SharedClient, new FileUpdateAttemptStore(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KuniWhatsThisAsset", "update-check.json")));

    public async Task<AvailableUpdate?> CheckAsync(Version current, CancellationToken cancellationToken = default)
        => (await CheckDetailedAsync(current, false, cancellationToken).ConfigureAwait(false)).Update;
    public async Task<UpdateCheckResult> CheckDetailedAsync(Version current, bool manual, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Reserve before sending, including failed/cancelled attempts and concurrent app instances.
            bool reserved = store.TryReserve((clock ?? (() => DateTimeOffset.Now))());
            if (!manual && !reserved) return new(UpdateCheckStatus.Skipped);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri);
            request.Headers.UserAgent.ParseAdd($"KuniWhatsThisAsset/{current.ToString(3)}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1024 * 1024) return new(UpdateCheckStatus.Unavailable);
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (memory.Length + read > 1024 * 1024) return new(UpdateCheckStatus.Unavailable);
                memory.Write(buffer, 0, read);
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(memory.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 1000) return new(UpdateCheckStatus.Unavailable);
            var update = FindLatest(memory.ToArray(), current);
            return new(update == null ? UpdateCheckStatus.UpToDate : UpdateCheckStatus.Available, update);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or ArgumentException or RegexMatchTimeoutException)
        { return new(UpdateCheckStatus.Unavailable); }
    }
    public static AvailableUpdate? FindLatest(byte[] json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 1000) return null;
        string? String(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        AvailableUpdate? latest = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || String(item, "type") != "file") continue;
            string name = String(item, "name") ?? "";
            if (name.Length > 128) continue;
            var match = Regex.Match(name, @"\AKuniWhatsThisAsset-v([0-9]+\.[0-9]+\.[0-9]+)-win-x64\.zip\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version) || version <= current || (latest != null && version <= latest.Version)) continue;
            string expected = "https://raw.githubusercontent.com/PeaceKunihiro/KuniWhatsThisAsset/master/Stable/" + name;
            if (String(item, "download_url") != expected) continue;
            latest = new(version, new Uri(expected));
        }
        return latest;
    }
}

