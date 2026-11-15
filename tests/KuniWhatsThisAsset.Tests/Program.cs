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
    try { action(); } catch (InvalidDataException) { return; } catch (EndOfStreamException) { return; }
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
Console.WriteLine($"\n{passed} passed, {failed} failed. Fixtures: {output}");
return failed == 0 ? 0 : 1;
