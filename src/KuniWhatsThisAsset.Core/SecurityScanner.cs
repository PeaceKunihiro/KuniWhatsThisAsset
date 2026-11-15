using System.Text;
using System.Text.RegularExpressions;

namespace KuniWhatsThisAsset.Core;

public static class SecurityScanner
{
    private static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".bat", ".cmd", ".ps1", ".vbs", ".scr", ".com", ".msi", ".js", ".jse", ".wsf", ".hta", ".pif", ".lnk", ".so", ".dylib" };
    private static readonly HashSet<string> TextFormats = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".shader", ".cginc", ".hlsl", ".compute", ".json", ".xml", ".uss", ".uxml", ".txt", ".md", ".yaml", ".yml", ".meta", ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim", ".asmdef", ".asmref", ".bat", ".cmd", ".ps1", ".vbs", ".js" };
    private static readonly (string Code, string Pattern, string Message)[] Rules =
    [
        ("PROCESS", @"\b(Process\s*\.\s*Start|ProcessStartInfo|ShellExecute\w*|CreateProcess\w*|WScript\s*\.\s*Shell)\b", "外部プロセスを起動する可能性のあるコードです。"),
        ("NETWORK", @"\b(HttpClient|WebClient|WebRequest|HttpWebRequest|UnityWebRequest|TcpClient|UdpClient|Socket|DownloadString|DownloadFile|https?://)\b", "ネットワーク通信・外部URLを含むコードです。"),
        ("PINVOKE", @"\b(DllImport|LibraryImport|NativeLibrary\s*\.\s*Load|GetProcAddress|LoadLibrary\w*)\b", "ネイティブコード呼び出し・ロードの記述です。"),
        ("ASSEMBLY_LOAD", @"\b(Assembly\s*\.\s*(Load\w*|UnsafeLoadFrom)|AppDomain\s*\.\s*\w+\s*\.\s*Load|AssemblyLoadContext)\b", "動的Assemblyロードの記述です。"),
        ("EDITOR_AUTO", @"\b(InitializeOnLoad(Method)?|DidReloadScripts|AssetPostprocessor|OnPostprocessAllAssets)\b", "Unity Editorで自動実行される可能性のあるコードです。")
    ];
    public static bool IsText(string path) => TextFormats.Contains(System.IO.Path.GetExtension(path));

    public static (List<Finding> Findings, string? Text) Scan(string path, byte[] bytes, long length)
    {
        var findings = new List<Finding>();
        var ext = System.IO.Path.GetExtension(path);
        void Add(Severity severity, string code, string message) => findings.Add(new(severity, code, path, message));
        if (Executables.Contains(ext)) Add(Severity.High, "EXECUTABLE", "実行可能形式またはスクリプトです。内容を実行せずに確認してください。");
        bool mz = bytes.Length >= 2 && bytes[0] == 'M' && bytes[1] == 'Z';
        bool elf = bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 69, 76, 70 });
        bool mach = bytes.Length >= 4 && (Convert.ToHexString(bytes.AsSpan(0, 4)) is "CFFAEDFE" or "CEFAEDFE" or "FEEDFACF" or "FEEDFACE" or "CAFEBABE");
        if (mz || elf || mach)
        {
            Add(Severity.High, "BINARY_EXECUTABLE", "実行可能バイナリのシグネチャを検出しました（DLLのmanaged/native判別は未対応）。");
            if (!(mz ? ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".scr", StringComparison.OrdinalIgnoreCase) : Executables.Contains(ext)))
                Add(Severity.High, "FORMAT_MISMATCH", "拡張子と実行可能バイナリの実体形式が一致しません。");
        }
        bool Has(params byte[] signature) => bytes.Length >= signature.Length && bytes.AsSpan(0, signature.Length).SequenceEqual(signature);
        bool? matches = ext.ToLowerInvariant() switch
        {
            ".png" => Has(137, 80, 78, 71, 13, 10, 26, 10),
            ".jpg" or ".jpeg" => Has(255, 216, 255),
            ".gif" => Has(71, 73, 70, 56),
            ".pdf" => Has(37, 80, 68, 70, 45),
            ".zip" => Has(80, 75, 3, 4) || Has(80, 75, 5, 6),
            ".exe" or ".dll" or ".scr" => mz,
            _ => null
        };
        if (matches == false && !findings.Any(f => f.Code == "FORMAT_MISMATCH"))
            Add(Severity.Warning, "FORMAT_MISMATCH", "拡張子の代表的なシグネチャと一致しません。破損または偽装の可能性があります。");
        if (!IsText(path)) return (findings, null);
        string text = TextDecoder.Decode(bytes, length > bytes.Length, out bool uncertainEncoding);
        foreach (var rule in Rules)
        {
            if (Regex.IsMatch(text, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                Add(Severity.Warning, rule.Code, rule.Message);
        }
        if (length > bytes.Length) Add(Severity.Warning, "SCAN_PARTIAL", $"テキスト解析は先頭{bytes.Length:N0}バイトのみです。残りは未解析です。");
        if (uncertainEncoding) Add(Severity.Warning, "TEXT_ENCODING", "UTF-8/UTF-16/Shift-JISで解釈できない文字があります。テキスト検出が不完全な可能性があります。");
        return (findings, text.Length > 32_768 ? text[..32_768] + "\n…（表示省略）" : text);
    }
}

