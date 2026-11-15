using System.Windows;
using System.Windows.Controls;

namespace KuniWhatsThisAsset.App;
public partial class MainWindow
{
    private Window? warningWindow;
    private double warningHeight = 200;

    private void Warning_Expanded(object sender, RoutedEventArgs e)
    {
        if (warningWindow != null) { SecurityNotice.IsExpanded = false; warningWindow.Show(); warningWindow.Activate(); return; }
        if (WarningRow == null || WarningSplitter == null) return;
        WarningRow.MinHeight = 90;
        WarningRow.Height = new GridLength(Math.Clamp(warningHeight, 90, Math.Max(90, ActualHeight - 300)));
        WarningSplitter.Visibility = Visibility.Visible;
    }
    private void Warning_Collapsed(object sender, RoutedEventArgs e)
    {
        if (WarningRow == null || WarningSplitter == null) return;
        if (WarningRow.ActualHeight > 80) warningHeight = WarningRow.ActualHeight;
        WarningRow.MinHeight = 0;
        WarningRow.Height = GridLength.Auto;
        WarningSplitter.Visibility = Visibility.Collapsed;
    }
    private void DetachWarnings_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (warningWindow != null) { warningWindow.Show(); warningWindow.Activate(); return; }
        SecurityNotice.IsExpanded = false;
        WarningContentHost.Content = null;
        var layout = new DockPanel { Margin = new Thickness(10) };
        var dockButton = new Button { Content = "元の画面に戻す", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(dockButton, Dock.Top);
        layout.Children.Add(dockButton);
        layout.Children.Add(WarningContent);
        var detached = new Window
        {
            Title = "KuniWhatsThisAsset — 警告", Owner = this,
            Width = 850, Height = 360, MinWidth = 480, MinHeight = 180,
            Background = SecurityNotice.Background, Foreground = Foreground,
            Icon = Icon, Content = layout, ShowActivated = false
        };
        warningWindow = detached;
        DetachButton.Content = "警告ウィンドウを表示";
        dockButton.Click += (_, _) => detached.Close();
        detached.Closed += (_, _) =>
        {
            layout.Children.Remove(WarningContent);
            WarningContentHost.Content = WarningContent;
            warningWindow = null;
            DetachButton.Content = "別ウィンドウ";
            SecurityNotice.IsExpanded = SecurityNotice.Visibility == Visibility.Visible;
        };
        detached.Show();
    }
}
