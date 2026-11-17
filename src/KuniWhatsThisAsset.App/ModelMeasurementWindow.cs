using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public sealed class ModelMeasurementWindow : Window
{
    private sealed record Row(string Name,string Node,string Local,string Combined,string Width,string Height,string Length,string Basis);
    public ModelMeasurementWindow(ModelMeasurement measurement)
    {
        measurement.Validate(); Title="寸法・Scale詳細";Width=1060;Height=620;MinWidth=700;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.FromRgb(238,223,203));Foreground=new SolidColorBrush(Color.FromRgb(56,41,30));FontFamily=new("Yu Gothic UI");FontSize=14;
        Icon=BitmapFrameIcon();
        var root=new DockPanel{Margin=new Thickness(14)};Content=root;
        var close=new Button{Content="閉じる",IsCancel=true,MinWidth=90,Padding=new Thickness(10,4,10,4),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};DockPanel.SetDock(close,Dock.Bottom);root.Children.Add(close);
        var heading=new StackPanel{Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        heading.Children.Add(new TextBlock{Text="推測寸法："+ModelMeasurements.Size(measurement.Bounds),FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        heading.Children.Add(new TextBlock{Text="Scale（ローカル）："+ModelMeasurements.ScaleSummary(measurement),Margin=new Thickness(0,6,0,6),TextWrapping=TextWrapping.Wrap});
        var bases=measurement.Meshes.Select(m=>m.Basis).Distinct().Take(2).ToArray();
        heading.Children.Add(new TextBlock{Text="換算条件："+(bases.Length==1?bases[0]:"メッシュごとに異なります。下表の換算条件を確認してください。"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,6)});
        heading.Children.Add(new TextBlock{Text="1 Unity単位=1 m。全幅(X)・全高(Y)・全長(Z)は髪・装飾を含む静止姿勢の軸平行外形。\nScaleは解析ノードの行列分解値です。合成Scaleは親・解析ルートの単位倍率を含み、Unity Transformの値とは限りません。\nOBJのScale、シアー等で分解できないScaleは不明。負のScaleの符号配分は元の設定と異なる場合があります。"+(measurement.Basis.Length>0?"\n"+measurement.Basis:""),TextWrapping=TextWrapping.Wrap});
        var grid=new DataGrid{IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,EnableRowVirtualization=true,EnableColumnVirtualization=true,Background=new SolidColorBrush(Color.FromRgb(255,245,232)),ItemsSource=measurement.Meshes.Select(m=>new Row(m.Name,m.NodePath,ModelMeasurements.Scale(m.LocalScale),ModelMeasurements.Scale(m.CombinedScale),ModelMeasurements.Number(m.Bounds.Width),ModelMeasurements.Number(m.Bounds.Height),ModelMeasurements.Number(m.Bounds.Length),m.Basis)).ToArray()};
        foreach(var (title,path,width) in new[]{("メッシュ","Name",160d),("Scale（ローカル）","Local",225d),("解析座標の合成Scale","Combined",225d),("全幅 m","Width",100d),("全高 m","Height",100d),("全長 m","Length",100d),("換算条件","Basis",500d),("ノード","Node",260d)}) grid.Columns.Add(new DataGridTextColumn{Header=title,Binding=new Binding(path),Width=width});
        root.Children.Add(grid);
    }
    private static ImageSource BitmapFrameIcon()=>System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/KuniWhatsThisAsset.App;component/Assets/KWTA_icon.png"));
}
