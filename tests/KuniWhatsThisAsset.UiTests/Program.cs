using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new App(); app.InitializeComponent();
        var main = new MainWindow();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        object? Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(main, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(main)!;
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
        try
        {
            main.Show(); main.UpdateLayout();
            var notice = Field<Expander>("SecurityNotice");
            var host = Field<ContentControl>("WarningContentHost");
            var grid = Field<DataGrid>("FindingsGrid");
            var splitter = Field<GridSplitter>("WarningSplitter");
            Invoke("Notify", (object)new Finding[] { new(Severity.Warning, "TEST", "test", "warning") });
            Check(notice.Visibility == Visibility.Visible && !notice.IsExpanded, "Notification must be passive");
            notice.IsExpanded = true; main.UpdateLayout();
            var row = Field<RowDefinition>("WarningRow");
            Check(splitter.Visibility == Visibility.Visible && row.ActualHeight >= 90, "Resizable row missing");
            row.Height = new GridLength(270); main.UpdateLayout();
            Check(row.ActualHeight >= 260, "Warning row did not resize");
            for (int cycle = 0; cycle < 2; cycle++)
            {
                Invoke("DetachWarnings_Click", main, new RoutedEventArgs(Button.ClickEvent));
                var detached = Field<Window>("warningWindow"); detached.UpdateLayout();
                Check(detached.IsVisible && host.Content == null && splitter.Visibility == Visibility.Collapsed, "Detach failed");
                Check(grid.Items.Count == 1 && grid.IsVisible, "Detached warnings lost");
                Invoke("Notify", (object)Array.Empty<Finding>());
                Check(!detached.IsVisible && notice.Visibility == Visibility.Collapsed, "Normal results must hide warnings");
                Invoke("Notify", (object)new Finding[] { new(Severity.Warning, "TEST", "test", "updated") });
                Check(detached.IsVisible && grid.Items.Count == 1, "Detached results not updated");
                detached.Close(); main.UpdateLayout();
                Check(host.Content == Field<Grid>("WarningContent") && notice.IsExpanded, "Redock failed");
            }
            Invoke("Notify", (object)Array.Empty<Finding>()); main.UpdateLayout();
            Check(notice.Visibility == Visibility.Collapsed && splitter.Visibility == Visibility.Collapsed, "Normal state leaves warning space");
            Console.WriteLine("PASS warning resize, repeated detach/redock, shared results, passive visibility");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { main.Close(); app.Shutdown(); }
    }
}

