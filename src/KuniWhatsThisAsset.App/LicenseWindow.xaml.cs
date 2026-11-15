using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace KuniWhatsThisAsset.App;

public sealed record LicenseEntry(string Title, string Text);
public partial class LicenseWindow : Window
{
    public static IReadOnlyList<LicenseEntry> LoadLicenses()
    {
        string Read(string name)
        {
            var assembly = typeof(LicenseWindow).Assembly;
            using var stream = assembly.GetManifestResourceStream("KuniWhatsThisAsset.App.Licenses." + name)
                ?? throw new InvalidOperationException("同梱ライセンスが見つかりません: " + name);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        return new LicenseEntry[]
        {
            new("KuniWhatsThisAsset", "KuniWhatsThisAsset\n\n本アプリ自体の公開ライセンスは未定です。\n以下の第三者ライセンスは、該当する依存コンポーネントに適用されます。\n\n現在の対象：.NET 10 / WPF。\nモデル表示用の検討候補（HelixToolkit・Assimp等）はまだ導入していません。"),
            new(".NET 配布ライセンス", Read("NET-LIBRARY-LICENSE.txt")),
            new(".NET Runtime ライセンス", Read("NETCore-LICENSE.txt")),
            new("WPF ライセンス", Read("WindowsDesktop-LICENSE.txt")),
            new(".NET 第三者通知", Read("NETCore-THIRD-PARTY-NOTICES.txt"))
        };
    }
    public LicenseWindow()
    {
        InitializeComponent();
        LicenseList.ItemsSource = LoadLicenses();
        LicenseList.SelectedIndex = 0;
    }
    private void License_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (LicenseList.SelectedItem is LicenseEntry entry) { LicenseText.Text = entry.Text; LicenseText.ScrollToHome(); }
    }
}
