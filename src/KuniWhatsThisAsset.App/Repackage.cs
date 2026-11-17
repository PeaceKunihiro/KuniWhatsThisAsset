using System.Windows;
namespace KuniWhatsThisAsset.App;
public partial class MainWindow
{
    private void Repackage_Click(object sender, RoutedEventArgs e)
    {
        if (report == null || scanCancellation != null) return;
        var dialog = new RepackageWindow(report.SourcePath, activeLimits) { Owner = this };
        if (dialog.ShowDialog() == true) Status.Text = "リパッケージを保存しました: " + dialog.SavedPath;
    }
}
