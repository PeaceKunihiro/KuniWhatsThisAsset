using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using KuniWhatsThisAsset.Core;

string output = Path.GetFullPath(Path.Combine("artifacts", "test-packages"));
Directory.CreateDirectory(output);
int passed = 0, failed = 0;
string guid = new('a', 32);
byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
}
string Archive(string name, Action<TarWriter> write)
{
    string file = Path.Combine(output, name + ".unitypackage");
    using var fs = File.Create(file);
    using var gzip = new GZipStream(fs, CompressionLevel.Fastest);
    using var tar = new TarWriter(gzip, TarEntryFormat.Ustar);
    write(tar);
    return file;
}
void Member(TarWriter tar, string path, byte[] data)
{
    using var stream = new MemoryStream(data);
    tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, path) { DataStream = stream });
}
void Asset(TarWriter tar, string id, string path, byte[] data)
{
    Member(tar, id + "/asset", data); // pathname deliberately follows asset
    Member(tar, id + "/pathname", Utf8(path));
    Member(tar, id + "/asset.meta", Utf8("fileFormatVersion: 2\nguid: " + id + "\n"));
}
PackageReport Read(string file, ScanLimits? limits = null) => new PackageReader(limits).Read(file);
void Reject(Action action)
{
    try { action(); } catch (ReadLimitExceededException) { return; } catch (InvalidDataException) { return; } catch (EndOfStreamException) { return; }
    throw new Exception("不正な入力が拒否されませんでした。");
}

Test("normal package / order independence / folder / unknown extension", () =>
{
    string file = Archive("normal", tar =>
    {
        Asset(tar, guid, "Assets/readme.txt", Utf8("Hello UnityPackage"));
        Asset(tar, new string('b', 32), "Assets/data.custom", [1, 2, 3]);
        string folder = new('c', 32);
        Member(tar, folder + "/pathname", Utf8("Assets/Textures"));
        Member(tar, folder + "/asset.meta", Utf8("fileFormatVersion: 2\nfolderAsset: yes\n"));
    });
    var r = Read(file);
    Check(r.FileCount == 2 && r.Items.Count == 3, "件数が不正");
    Check(r.Items.Single(i => i.Path.EndsWith(".txt")).TextPreview == "Hello UnityPackage", "テキストが不正");
    Check(r.Findings.Count == 0, "正常fixtureで予期しない警告");
});
Test("static rules / UTF16 / no execution", () =>
{
    string code = "using System; Process.Start(\"never-run.exe\"); new HttpClient(); [DllImport(\"x\")] Assembly.Load(bytes); [InitializeOnLoad]";
    var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(code)).ToArray();
    string file = Archive("warnings", tar =>
    {
        Asset(tar, guid, "Assets/Editor/Suspicious.cs", bytes);
        Asset(tar, new string('b', 32), "Assets/Textures/disguised.png", [(byte)'M', (byte)'Z', 0, 0]);
        Asset(tar, new string('c', 32), "Assets/Plugins/plugin.dll", [(byte)'M', (byte)'Z']);
    });
    var r = Read(file);
    foreach (string codeName in new[] { "PROCESS", "NETWORK", "PINVOKE", "ASSEMBLY_LOAD", "EDITOR_AUTO", "BINARY_EXECUTABLE", "FORMAT_MISMATCH", "EXECUTABLE" })
        Check(r.Findings.Any(f => f.Code == codeName), "未検出: " + codeName);
});
foreach (string path in new[] { "../escape", "Assets/../escape", "/absolute", "C:/absolute", "Assets\\escape", "//server/share", "Assets/a:ads", "Assets/NUL.txt", "Assets/trailing.", "Assets/a\0b", "Assets//x" })
    Test("reject path " + path.Replace('\0', '_'), () => Reject(() => Read(Archive("invalid-path", tar => Asset(tar, guid, path, [])))));
Test("reject traversal in archive member", () => Reject(() => Read(Archive("member-traversal", tar => Member(tar, "../escape", [])))));
Test("reject symbolic link", () => Reject(() => Read(Archive("symlink", tar => tar.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, guid + "/asset") { LinkName = "../../outside" })))));
Test("reject hard link", () => Reject(() => Read(Archive("hardlink", tar => tar.WriteEntry(new UstarTarEntry(TarEntryType.HardLink, guid + "/asset") { LinkName = "elsewhere" })))));
Test("reject duplicate member", () => Reject(() => Read(Archive("duplicate-member", tar => { Asset(tar, guid, "Assets/a.txt", []); Member(tar, guid + "/asset", []); }))));
Test("reject case-insensitive duplicate path", () => Reject(() => Read(Archive("duplicate-path", tar => { Asset(tar, guid, "Assets/a.txt", []); Asset(tar, new string('b', 32), "Assets/A.txt", []); }))));
Test("reject missing pathname", () => Reject(() => Read(Archive("missing-path", tar => Member(tar, guid + "/asset", [])))));
Test("reject missing asset", () => Reject(() => Read(Archive("missing-asset", tar => Member(tar, guid + "/pathname", Utf8("Assets/a.txt"))))));
Test("member limit", () => Reject(() => Read(Archive("member-limit", tar => Asset(tar, guid, "Assets/a.txt", new byte[256])), new() { MaxMemberBytes = 128 })));
Test("expanded limit / compressible input", () => Reject(() => Read(Archive("expanded-limit", tar => Asset(tar, guid, "Assets/a.txt", new byte[100_000])), new() { MaxExpandedBytes = 4096 })));
Test("compressed input limit", () => Reject(() => Read(Path.Combine(output, "normal.unitypackage"), new() { MaxPackageBytes = 8 })));
Test("entry limit", () => Reject(() => Read(Path.Combine(output, "normal.unitypackage"), new() { MaxEntries = 1 })));
Test("asset limit", () => Reject(() => Read(Path.Combine(output, "normal.unitypackage"), new() { MaxAssets = 1 })));
Test("partial scan is visible", () =>
{
    string file = Archive("partial", tar => Asset(tar, guid, "Assets/a.cs", Utf8(new string(' ', 8192) + "Process.Start(x)")));
    Check(Read(file, new() { MaxTextBytes = 4096 }).Findings.Any(f => f.Code == "SCAN_PARTIAL"), "解析省略警告なし");
});
Test("metadata is scanned", () =>
{
    string file = Archive("metadata", tar =>
    {
        Member(tar, guid + "/asset", []); Member(tar, guid + "/pathname", Utf8("Assets/a.custom"));
        Member(tar, guid + "/asset.meta", Utf8("url: https://example.invalid\n"));
    });
    Check(Read(file).Findings.Any(f => f.Code == "NETWORK" && f.Path.EndsWith(".meta")), "meta未解析");
});
Test("invalid UTF8 pathname", () => Reject(() => Read(Archive("bad-utf8", tar => Member(tar, guid + "/pathname", [255, 255])))));
Test("invalid gzip", () => { string f = Path.Combine(output, "bad.unitypackage"); File.WriteAllText(f, "not gzip"); Reject(() => Read(f)); });
Test("gzip CRC corruption", () =>
{
    byte[] b = File.ReadAllBytes(Path.Combine(output, "normal.unitypackage")); b[^8] ^= 0xff;
    string f = Path.Combine(output, "bad-crc.unitypackage"); File.WriteAllBytes(f, b); Reject(() => Read(f));
});
Test("truncated gzip", () =>
{
    byte[] b = File.ReadAllBytes(Path.Combine(output, "normal.unitypackage"));
    string f = Path.Combine(output, "truncated.unitypackage"); File.WriteAllBytes(f, b[..(b.Length / 2)]); Reject(() => Read(f));
});
Test("cancellation", () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { new PackageReader().Read(Path.Combine(output, "normal.unitypackage"), cts.Token); }
    catch (OperationCanceledException) { return; }
    throw new Exception("キャンセルされませんでした");
});
Test("missing gzip trailer", () =>
{
    byte[] b = File.ReadAllBytes(Path.Combine(output, "normal.unitypackage"));
    string f = Path.Combine(output, "missing-trailer.unitypackage"); File.WriteAllBytes(f, b[..^8]); Reject(() => Read(f));
});
Test("missing tar end blocks", () =>
{
    using var input = File.OpenRead(Path.Combine(output, "normal.unitypackage"));
    using var unzip = new GZipStream(input, CompressionMode.Decompress);
    using var memory = new MemoryStream(); unzip.CopyTo(memory);
    byte[] tarBytes = memory.ToArray();
    // Remove the two terminator blocks while retaining a valid gzip container.
    string f = Path.Combine(output, "missing-tar-end.unitypackage");
    using (var fs = File.Create(f)) using (var zip = new GZipStream(fs, CompressionLevel.Fastest)) zip.Write(tarBytes.AsSpan(0, tarBytes.Length - 1024));
    Reject(() => Read(f));
});
Test("signature rules include ELF and Mach-O", () =>
{
    foreach (byte[] signature in new byte[][] { [0x7f, 69, 76, 70], [0xcf, 0xfa, 0xed, 0xfe] })
        Check(SecurityScanner.Scan("Assets/fake.txt", signature, signature.Length).Findings.Any(f => f.Code == "BINARY_EXECUTABLE"), "実体形式未検出");
});
Test("preview and unexpected members are checked", () =>
{
    string file = Archive("extra-members", tar =>
    {
        Asset(tar, guid, "Assets/a.txt", Utf8("hello"));
        Member(tar, guid + "/preview.png", [(byte)'M', (byte)'Z']);
        Member(tar, guid + "/hidden.ps1", Utf8("Process.Start(x)"));
    });
    var r = Read(file);
    Check(r.Findings.Any(f => f.Code == "BINARY_EXECUTABLE" && f.Path.EndsWith("preview.png")), "preview未解析");
    Check(r.Findings.Any(f => f.Code == "EXECUTABLE" && f.Path.EndsWith("hidden.ps1")), "未知メンバー未解析");
});
Test("reject PAX metadata before processing payload", () =>
{
    string file = Path.Combine(output, "pax.unitypackage");
    using (var fs = File.Create(file)) using (var zip = new GZipStream(fs, CompressionLevel.Fastest)) using (var writer = new TarWriter(zip, TarEntryFormat.Pax))
    {
        using var data = new MemoryStream(Utf8("Assets/a.txt"));
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, guid + "/pathname") { DataStream = data });
    }
    Reject(() => Read(file));
});
Test("limit override is scoped and preserves validation", () =>
{
    var original = new ScanLimits { MaxPackageBytes = 8 };
    string file = Path.Combine(output, "normal.unitypackage");
    ReadLimitKind kind;
    try { Read(file, original); throw new Exception("No limit exception"); }
    catch (ReadLimitExceededException ex) { kind = ex.Kind; }
    Check(kind == ReadLimitKind.PackageBytes, "Wrong limit kind");
    var raised = original;
    while (raised.MaxPackageBytes < new FileInfo(file).Length) raised = raised.Increase(kind);
    Check(Read(file, raised).FileCount == 2, "Retry failed");
    Check(original.MaxPackageBytes == 8, "Original limit mutated");
    Reject(() => Read(Path.Combine(output, "bad-crc.unitypackage"), raised));
});
Test("approved reading bypasses all resource limits but preserves integrity and cancellation", () =>
{
    var restricted = new ScanLimits { MaxPackageBytes = 1, MaxExpandedBytes = 1, MaxMemberBytes = 1, MaxEntries = 1, MaxAssets = 1, Timeout = TimeSpan.FromMilliseconds(1) };
    var approved = restricted with { IgnoreReadLimits = true };
    var r = Read(Path.Combine(output, "normal.unitypackage"), approved);
    Check(r.FileCount == 2, "Approved read failed");
    Check(!restricted.IgnoreReadLimits, "Approval leaked to original settings");
    Reject(() => Read(Path.Combine(output, "normal.unitypackage"), restricted));
    Reject(() => Read(Path.Combine(output, "bad-crc.unitypackage"), approved));
    Reject(() => Read(Path.Combine(output, "member-traversal.unitypackage"), approved));
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { new PackageReader(approved).Read(Path.Combine(output, "normal.unitypackage"), cts.Token); }
    catch (OperationCanceledException) { return; }
    throw new Exception("Cancellation bypassed");
});
Test("Shift-JIS preview and static analysis", () =>
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    byte[] bytes = Encoding.GetEncoding(932).GetBytes("// 日本語の説明です\nProcess.Start(x);");
    var scan = SecurityScanner.Scan("Assets/test.cs", bytes, bytes.Length);
    Check(scan.Text!.Contains("日本語の説明です"), "Shift-JIS mojibake");
    Check(scan.Findings.Any(f => f.Code == "PROCESS"), "Shift-JIS code not scanned");
    Check(!scan.Findings.Any(f => f.Code == "TEXT_ENCODING"), "Valid Shift-JIS warned");
});
Test("UTF8 BOM and clipped multibyte sample", () =>
{
    byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Utf8("日本語")).ToArray();
    Check(TextDecoder.Decode(bytes, false, out bool bad) == "日本語" && !bad, "BOM not removed");
    byte[] clipped = Utf8("説明文")[..^1];
    Check(TextDecoder.Decode(clipped, true, out bad) == "説明" && !bad, "Clipped UTF8 incorrectly fell back");
    TextDecoder.Decode([0x81, 0x30], false, out bad);
    Check(bad, "Invalid UTF8/CP932 not warned");
});
Test("full selected asset read beyond sample budget and input cap", () =>
{
    byte[] data = new byte[3 * 1024 * 1024]; data[^1] = 123;
    string file = Archive("image-data", tar => Asset(tar, guid, "Assets/texture.png", data));
    var reader = new PackageReader();
    Check(reader.ReadAssetBytes(file, guid).SequenceEqual(data), "Image data was truncated");
    Reject(() => reader.ReadAssetBytes(file, guid, maxBytes: 1024));
    Reject(() => reader.ReadAssetBytes(Path.Combine(output, "bad-crc.unitypackage"), guid));
});
Test("update candidate numeric ordering and URL allowlist", () =>
{
    string JsonEntry(string version, string suffix = "win-x64.zip", bool evil = false) => System.Text.Json.JsonSerializer.Serialize(new {
        name = "KuniWhatsThisAsset-v" + version + "-" + suffix, type = "file",
        download_url = evil ? "https://example.invalid/run.exe" : "https://raw.githubusercontent.com/PeaceKunihiro/KuniWhatsThisAsset/master/Stable/KuniWhatsThisAsset-v" + version + "-" + suffix });
    byte[] json = Utf8("[" + string.Join(",", JsonEntry("0.9.0"), JsonEntry("0.10.0"), JsonEntry("0.99.0", evil: true), JsonEntry("9.0.0", "win-arm64.zip"), JsonEntry("9.0.0-beta")) + "]");
    Check(UpdateChecker.FindLatest(json, new Version(0, 8, 0))?.Version == new Version(0, 10, 0), "Not numeric latest");
    Check(UpdateChecker.FindLatest(json, new Version(0, 10, 0)) == null, "Same/older should not notify");
    Check(UpdateChecker.FindLatest(Utf8("{}"), new Version(0, 1, 0)) == null, "Wrong root accepted");
});
Test("update attempt persists across instances and day changes", () =>
{
    string file = Path.Combine(output, "update-" + Guid.NewGuid() + ".json");
    var now = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(9));
    Check(new FileUpdateAttemptStore(file).TryReserve(now), "First attempt blocked");
    Check(!new FileUpdateAttemptStore(file).TryReserve(now.AddHours(1)), "Same day allowed");
    Check(new FileUpdateAttemptStore(file).TryReserve(now.AddDays(1)), "Next day blocked");
    Check(!new FileUpdateAttemptStore(file).TryReserve(now), "Clock rollback allowed");
    File.WriteAllText(file, "broken");
    Check(!new FileUpdateAttemptStore(file).TryReserve(now.AddDays(2)), "Corrupt state should suppress request");
    File.WriteAllText(file, "");
    Check(!new FileUpdateAttemptStore(file).TryReserve(now), "Empty prior state accepted");
});
Test("failed update counts for day; next day can find update", () =>
{
    string file = Path.Combine(output, "update-" + Guid.NewGuid() + ".json");
    var now = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(9));
    int calls = 0;
    using var handler = new FakeUpdateHandler((request, token) =>
    {
        Check(request.RequestUri == UpdateChecker.ApiUri && request.Headers.Authorization == null && request.Headers.UserAgent.Count > 0, "Unexpected request");
        calls++;
        return Task.FromResult(calls == 1 ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) :
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("[{\"type\":\"file\",\"name\":\"KuniWhatsThisAsset-v1.0.0-win-x64.zip\",\"download_url\":\"https://raw.githubusercontent.com/PeaceKunihiro/KuniWhatsThisAsset/master/Stable/KuniWhatsThisAsset-v1.0.0-win-x64.zip\"}]") });
    });
    using var client = new System.Net.Http.HttpClient(handler);
    var checker = new UpdateChecker(client, new FileUpdateAttemptStore(file), () => now);
    Check(checker.CheckAsync(new Version(0, 7, 0)).GetAwaiter().GetResult() == null, "Failure not quiet");
    Check(checker.CheckAsync(new Version(0, 7, 0)).GetAwaiter().GetResult() == null && calls == 1, "Failure retried same day");
    now = now.AddDays(1);
    Check(checker.CheckAsync(new Version(0, 7, 0)).GetAwaiter().GetResult()?.Version == new Version(1, 0, 0) && calls == 2, "Next day request failed");
});
Test("update invalid JSON, connection failure and cancellation are quiet", () =>
{
    foreach (int mode in new[] { 0, 1, 2 })
    {
        string file = Path.Combine(output, "update-" + Guid.NewGuid() + ".json");
        using var handler = new FakeUpdateHandler((_, token) => mode switch {
            0 => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("broken") }),
            1 => Task.FromException<System.Net.Http.HttpResponseMessage>(new System.Net.Http.HttpRequestException("offline")),
            _ => Task.FromCanceled<System.Net.Http.HttpResponseMessage>(new CancellationToken(true)) });
        using var client = new System.Net.Http.HttpClient(handler);
        var checker = new UpdateChecker(client, new FileUpdateAttemptStore(file));
        Check(checker.CheckAsync(new Version(0, 7, 0)).GetAwaiter().GetResult() == null, "Failure escaped");
    }
});
Test("concurrent update checks reserve only one request", () =>
{
    string file = Path.Combine(output, "update-" + Guid.NewGuid() + ".json");
    int calls = 0;
    using var handler = new FakeUpdateHandler(async (_, token) => { Interlocked.Increment(ref calls); await Task.Delay(20, token); return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden); });
    using var client = new System.Net.Http.HttpClient(handler);
    Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => new UpdateChecker(client, new FileUpdateAttemptStore(file)).CheckAsync(new Version(0, 7, 0))))).GetAwaiter().GetResult();
    Check(calls == 1, "Concurrent requests sent");
});
Test("model load warning inclusive thresholds and no-render decision", () =>
{
    var stats = new ModelStatistics(3, 99999, 1, 9, 0, false);
    int prompts = 0;
    bool Refuse(ModelStatistics s) { prompts++; return false; }
    Check(ModelRenderPolicy.Approve(stats, Refuse) && prompts == 0, "Below threshold warned");
    Check(!ModelRenderPolicy.Approve(stats with { TriangleCount = 100000 }, Refuse) && prompts == 1, "100000 polygons did not warn");
    Check(!ModelRenderPolicy.Approve(stats with { MaterialCount = 10 }, Refuse) && prompts == 2, "10 materials did not warn");
    Check(ModelRenderPolicy.Approve(stats with { TriangleCount = 100001 }, _ => true), "Approval rejected");
});
Console.WriteLine($"\n{passed} passed, {failed} failed. Fixtures: {output}");
return failed == 0 ? 0 : 1;




internal sealed class FakeUpdateHandler(Func<System.Net.Http.HttpRequestMessage, CancellationToken, Task<System.Net.Http.HttpResponseMessage>> callback) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
}

