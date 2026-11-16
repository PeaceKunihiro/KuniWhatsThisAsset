using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KuniWhatsThisAsset.App;
using KuniWhatsThisAsset.Core;
internal static class UpdateUiChecks
{
    public static void Run(MainWindow main)
    {
        void Check(bool ok,string why){if(!ok)throw new Exception(why);}
        int requests=0;
        using var handler=new FakeHandler(()=>++requests);using var client=new HttpClient(handler);
        var store=new FileUpdateAttemptStore(Path.GetFullPath("artifacts/update-ui-"+Guid.NewGuid()+".json"));store.TryReserve(DateTimeOffset.Now);
        var updater=new FakeUpdater();var about=new AboutWindow(new(client,store),new(0,11,0),updater){Owner=main};about.Show();FeatureChecks.Pump(Task.Delay(100));
        var check=(Button)about.FindName("CheckUpdateButton");var download=(Button)about.FindName("DownloadUpdateButton");var status=(TextBlock)about.FindName("UpdateStatus");
        Check(requests==0,"Automatic daily limit ignored");
        check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));FeatureChecks.Pump(Task.Delay(100));
        Check(requests==1 && ((StackPanel)about.FindName("UpdatePanel")).Visibility==Visibility.Visible,"Manual check did not bypass daily limit");
        check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));FeatureChecks.Pump(Task.Delay(100));Check(requests==2,"Repeated explicit check was limited");
        download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));FeatureChecks.Pump(Task.Delay(50));
        Check(updater.Started && !check.IsEnabled && !download.IsEnabled && status.Text.Contains("ダウンロード"),"Download progress/buttons missing");
        about.UpdateLayout();var capture=new RenderTargetBitmap((int)about.ActualWidth,(int)about.ActualHeight,96,96,PixelFormats.Pbgra32);capture.Render(about);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(capture));using(var output=File.Create("artifacts/about-update-v0.11.0.png"))encoder.Save(output);
        updater.Completion.SetException(new InvalidDataException("SHA256が一致しません。"));FeatureChecks.Pump(Task.Delay(80));
        Check(about.IsVisible && check.IsEnabled && status.Text.Contains("SHA256"),"Download failure closed dialog or escaped");
        updater.Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));FeatureChecks.Pump(Task.Delay(30));about.Close();FeatureChecks.Pump(Task.Delay(30));Check(updater.Cancelled,"Closing dialog did not cancel download");
        Console.WriteLine("PASS About explicit checks, busy/progress UI, bad-checksum recovery and cancellation (mock HTTP/updater)");
    }
    private sealed class FakeHandler(Func<int> count):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            count();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[{\"type\":\"file\",\"name\":\"KuniWhatsThisAsset-v0.12.0-win-x64.zip\",\"download_url\":\"https://raw.githubusercontent.com/PeaceKunihiro/KuniWhatsThisAsset/master/Stable/KuniWhatsThisAsset-v0.12.0-win-x64.zip\"}]")});
        }
    }
    private sealed class FakeUpdater:IAppUpdater
    {
        public TaskCompletionSource Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Started,Cancelled;
        public async Task InstallAsync(AvailableUpdate update,IProgress<UpdateProgress> progress,CancellationToken token)
        {
            Started=true;progress.Report(new("最新版をダウンロード中…",1048576,2097152));
            try{await Completion.Task.WaitAsync(token);}catch(OperationCanceledException){Cancelled=true;throw;}
        }
    }
}
