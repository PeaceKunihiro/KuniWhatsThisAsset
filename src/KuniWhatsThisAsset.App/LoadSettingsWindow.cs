using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public sealed class LoadSettingsWindow : Window
{
    public UserPreferences Result { get; private set; }
    public LoadSettingsWindow(UserPreferences current)
    {
        Result = current; Title = "設定"; Width = 540; Height = 370; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(238, 223, 203)); Foreground = new SolidColorBrush(Color.FromRgb(56, 41, 30));
        var tabs = new TabControl { Margin = new Thickness(14), Background = Background }; Content = tabs;
        var panel = new StackPanel { Margin = new Thickness(16) }; tabs.Items.Add(new TabItem { Header = "読み込み設定", Content = panel, Background = Background });
        (TextBox box, CheckBox unlimited) Row(string name, long value, bool noLimit)
        {
            panel.Children.Add(new TextBlock { Text = name, Margin = new Thickness(0, 8, 0, 5) });
            var row = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(row);
            var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 150, MaxLength = 13, Padding = new Thickness(4), IsEnabled = !noLimit };
            box.PreviewTextInput += (_, e) => e.Handled = e.Text.Any(c => c is < '0' or > '9');
            var steps = new StackPanel();
            foreach (int delta in new[] { 1, -1 })
            {
                var step = new Button { Content = delta > 0 ? "＋" : "−", Width = 23, Height = 17, FontSize = 10, Padding = new Thickness(0), IsEnabled = !noLimit };
                step.Click += (_, _) => { long.TryParse(box.Text, out long n); n = Math.Clamp(n, 1, UserPreferences.MaxMiB); box.Text = Math.Clamp(n + delta, 1, UserPreferences.MaxMiB).ToString(CultureInfo.InvariantCulture); };
                steps.Children.Add(step);
            }
            var check = new CheckBox { Content = "無制限", IsChecked = noLimit, Margin = new Thickness(16, 4, 0, 0) };
            check.Checked += (_, _) => { box.IsEnabled = false; foreach (Button step in steps.Children) step.IsEnabled = false; }; check.Unchecked += (_, _) => { box.IsEnabled = true; foreach (Button step in steps.Children) step.IsEnabled = true; };
            row.Children.Add(box); row.Children.Add(steps); row.Children.Add(new TextBlock { Text = " MiB", VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(check); return (box, check);
        }
        var package = Row("Unitypackage上限", current.PackageLimitMiB, current.PackageUnlimited);
        var cache = Row("レンダリングキャッシュ上限", current.CacheLimitMiB, current.CacheUnlimited);
        panel.Children.Add(new TextBlock { Text = "初期値は各500 MiB。パッケージ設定は次回の読み込みから適用します。\nキャッシュ縮小時は古い写真から削除します。容量以外の解析制限は維持します。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 8) });
        var error = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var save = new Button { Content = "保存", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(22, 5, 22, 5) }; panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            if (!long.TryParse(package.box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long p) || !long.TryParse(cache.box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long c)) { error.Text = "上限には1以上の整数を入力してください。"; return; }
            try { Result = current with { PackageLimitMiB = p, PackageUnlimited = package.unlimited.IsChecked == true, CacheLimitMiB = c, CacheUnlimited = cache.unlimited.IsChecked == true }; Result.Validate(); DialogResult = true; }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
    }
}
