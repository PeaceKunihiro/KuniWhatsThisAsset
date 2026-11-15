using System.Text.Json;
using System.Text.Json.Serialization;
using KuniWhatsThisAsset.Core;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: KuniWhatsThisAsset.Cli <file.unitypackage>");
    return 2;
}
try
{
    var report = new PackageReader().Read(args[0]);
    var options = new JsonSerializerOptions { WriteIndented = true };
    options.Converters.Add(new JsonStringEnumConverter());
    Console.WriteLine(JsonSerializer.Serialize(report, options));
    return report.Findings.Count == 0 ? 0 : 1;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OperationCanceledException or System.Text.RegularExpressions.RegexMatchTimeoutException)
{
    Console.Error.WriteLine("解析未完了: " + ex.Message);
    return 2;
}
