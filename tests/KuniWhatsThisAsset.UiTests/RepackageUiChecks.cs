using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;
internal static class RepackageUiChecks
{
    public static void Run(MainWindow main)
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        string source=Path.GetFullPath("artifacts/test-packages/normal.unitypackage"),target=Path.GetFullPath("artifacts/test-packages/ui-grouped-"+Guid.NewGuid()+".unitypackage");
        var window=new RepackageWindow(source,new()){Owner=main};Exception? failure=null;
        window.Loaded+=async(_,_)=>
        {
            try
            {
                var save=(Button)window.FindName("SaveButton");var name=(TextBox)window.FindName("FolderName");var clock=System.Diagnostics.Stopwatch.StartNew();
                while(!save.IsEnabled&&clock.Elapsed.TotalSeconds<20)await Task.Delay(20);
                Check(save.IsEnabled,"Repackage dialog did not finish analysis");
                name.Text="../invalid";Check(!save.IsEnabled,"Unsafe folder allowed");name.Text="Resources";Check(!save.IsEnabled,"Special wrapper folder allowed");name.Text="UIまとめ";Check(save.IsEnabled,"Valid wrapper rejected");
                Check(((TextBox)window.FindName("PathExamples")).Text.Contains("Assets/UIまとめ"),"Output layout example missing");
                window.UpdateLayout();var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create("artifacts/repackage-dialog-v0.12.0.png"))encoder.Save(stream);
                await window.SaveToAsync(target);
            }
            catch(Exception ex){failure=ex;window.Close();}
        };
        bool? result=window.ShowDialog();if(failure!=null)throw failure;
        Check(result==true&&window.SavedPath==target,"Repackage did not complete");var report=new PackageReader().Read(target);Check(report.Items.Count==4&&report.Items.All(i=>i.Path.StartsWith("Assets/UIまとめ")),"UI grouped output wrong");
        Console.WriteLine("PASS repackage dialog source validation, path preview, invalid/special name refusal and verified save");
    }
}
