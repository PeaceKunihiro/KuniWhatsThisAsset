using System.IO;
namespace KuniWhatsThisAsset.App;
public static class NativeDependencies
{
    public static string AssimpPath()
    {
        var directories = new[] { AppContext.BaseDirectory }.Concat((AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string relative in new[] { "Libs/assimp.dll", "assimp.dll" })
            {
                string path = Path.Combine(directory, relative);
                if (File.Exists(path)) return path;
            }
        throw new FileNotFoundException("モデル解析DLLを見つけられません。配布ZIPを展開し直してください。");
    }
}
