using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public static class ModelWorker
{
    public static async Task<int> RunAsync()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            using var input = Console.OpenStandardInput();
            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            string ReadName() { int n = reader.ReadInt32(); if (n < 1 || n > 131072) throw new InvalidDataException("入力パスが不正です。"); var b = reader.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException(); return new UTF8Encoding(false, true).GetString(b); }
            byte[] ReadBytes(int max) { int n = reader.ReadInt32(); if (n < 0 || n > max) throw new InvalidDataException("モデル入力が大きすぎます。"); byte[] b = reader.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException(); return b; }
            string path = ReadName();
            if (Path.GetExtension(path).ToLowerInvariant() is not (".fbx" or ".obj")) throw new InvalidDataException("モデル形式が未対応です。");
            byte[] bytes = ReadBytes(256 * 1024 * 1024);
            int count = reader.ReadInt32();
            if (count < 0 || count > 32) throw new InvalidDataException("依存ファイルが多すぎます。");
            var dependencies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            for (int i = 0; i < count; i++) { var name = ReadName(); var data = ReadBytes(4 * 1024 * 1024); total += data.Length; if (total > 32 * 1024 * 1024) throw new InvalidDataException("依存ファイルが大きすぎます。"); dependencies.Add(name, data); }
            var result = ModelImporter.Import(new(path, bytes, dependencies, Math.Clamp(reader.ReadInt32(), 0, 10000)));
            await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result);
            return 0;
        }
        catch (Exception ex) { await Console.Error.WriteLineAsync("モデルを解析できません: " + ex.Message); return 1; }
    }

    public static async Task<ModelPreviewData> ImportAsync(ModelInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string assembly = Path.Combine(AppContext.BaseDirectory, "KuniWhatsThisAsset.App.dll");
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("起動元を取得できません。");
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = host;
            string entry = Assembly.GetEntryAssembly()!.GetName().Name!;
            start.ArgumentList.Add("exec"); start.ArgumentList.Add("--runtimeconfig"); start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, entry + ".runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile"); start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, entry + ".deps.json")); start.ArgumentList.Add(assembly);
        }
        else start.FileName = Assembly.GetEntryAssembly() == typeof(ModelWorker).Assembly ? host : Path.ChangeExtension(assembly, ".exe");
        start.ArgumentList.Add("--model-worker");
        using var process = Process.Start(start) ?? throw new IOException("解析プロセスを起動できません。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        void Kill() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        using var registration = timeout.Token.Register(Kill);
        try
        {
            var write = Task.Run(() =>
            {
                using var writer = new BinaryWriter(process.StandardInput.BaseStream, Encoding.UTF8, leaveOpen: false);
                void Name(string name) { byte[] b = Encoding.UTF8.GetBytes(name); writer.Write(b.Length); writer.Write(b); }
                Name(input.Path); writer.Write(input.Bytes.Length); writer.Write(input.Bytes); writer.Write(input.Dependencies.Count);
                foreach (var (name, data) in input.Dependencies) { Name(name); writer.Write(data.Length); writer.Write(data); }
                writer.Write(input.ReferencedMaterialCount);
            });
            async Task<byte[]> ReadBounded(Stream stream, int max)
            {
                using var memory = new MemoryStream(); var buffer = new byte[64 * 1024]; int n;
                while ((n = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (memory.Length + n > max) { timeout.Cancel(); throw new InvalidDataException("モデル解析結果が大きすぎます。"); }
                    memory.Write(buffer, 0, n);
                }
                return memory.ToArray();
            }
            var read = ReadBounded(process.StandardOutput.BaseStream, 192 * 1024 * 1024);
            var error = ReadBounded(process.StandardError.BaseStream, 64 * 1024);
            var monitor = Task.Run(async () =>
            {
                while (!process.HasExited)
                {
                    try
                    {
                        process.Refresh();
                        if (process.PrivateMemorySize64 > 2L * 1024 * 1024 * 1024) { timeout.Cancel(); throw new InvalidDataException("モデル解析のメモリ上限（2 GiB）を超えました。"); }
                    }
                    catch (InvalidOperationException) when (process.HasExited) { break; }
                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                }
            });
            await Task.WhenAll(write, read, error, monitor).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidDataException(Encoding.UTF8.GetString(await error));
            var model = JsonSerializer.Deserialize<ModelPreviewData>(await read) ?? throw new InvalidDataException("モデル解析結果が空です。");
            model.Validate();
            return model;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new InvalidDataException("モデル解析を中断しました（60秒またはリソース上限）。"); }
        finally { Kill(); }
    }
}



