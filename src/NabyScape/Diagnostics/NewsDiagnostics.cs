using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NabyScape;

internal static class NewsDiagnostics
{
    private sealed class Fixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            bool feed=request.RequestUri!.AbsolutePath.EndsWith("/feed",StringComparison.Ordinal);
            var items=Enumerable.Range(1,feed?20:5).Select(i=>feed?(object)new{id=i.ToString(),title="Тестовая новость "+i+": городские библиотеки открывают новые читальные залы",summary="Пример текста для проверки раскрытия новости. Это тестовые данные интерфейса, а не новостное сообщение.",source="Тестовая редакция",publishedAt="2026-11-24 20:00",url="https://example.org/story",notes=new[]{"Тестовая сноска источника, которую нужно сохранить целиком."}}:new{postId=i.ToString(),head="Тестовая сводка "+i+": новые маршруты и события недели",nsrc=i+2,notes=Array.Empty<string>()});
            string body=System.Text.Json.JsonSerializer.Serialize(new{title=feed?"Лента · тестовые данные":"Главное · тестовые данные",items});
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
    internal static async Task Run(App app,OverlayWindow island,string directory,Action<string,bool> check)
    {
        var settings=app.Settings;settings.ReduceMotion=true;settings.NewsConnected=true;settings.GlassStyle=false;settings.Background="#000000";
        bool live=Environment.GetCommandLineArgs().Contains("--news-live");
        settings.NewsApiBaseUrl=live?Environment.GetEnvironmentVariable("NABYSCAPE_NEWS_API")??"https://news.anyway-community.ru/api":"https://example.org/api";
        if(!live){app.News.Dispose();app.News=new NewsService(new Fixture());}
        settings.Widgets.Single(w=>w.Id=="news").Enabled=true;settings.CompactLayout=true;settings.PanelWidth=420;settings.PanelMaxHeight=650;settings.PanelAutoHeight=true;
        app.ChangeSettings();island.Show();island.Reveal(false);island.SelectPage("news",false);await Task.Delay(150);
        var view=Diagnostics.Descendants(island).OfType<NewsView>().Single();
        async Task Wait()
        {var start=DateTime.UtcNow;while(view.Busy && DateTime.UtcNow-start<TimeSpan.FromSeconds(25))await Task.Delay(50);island.UpdateLayout();}
        void Capture(string name)
        {
            island.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(island.ActualWidth),(int)Math.Ceiling(island.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(island);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(directory,name+".png"));encoder.Save(stream);
        }
        await Wait();check("News digest loads through configured HTTP API",view.Shown?.Items.Length>0);Capture("news-digest-dark");
        var connection=Diagnostics.Descendants(view).OfType<Button>().Single(b=>Equals(b.Content,"Подключение"));
        var connectionBounds=connection.TransformToAncestor(island).TransformBounds(new Rect(connection.RenderSize));
        check("News footer remains inside the island contour",connectionBounds.Bottom<=island.PaintedHeight-8);
        view.Select(true);await Wait();check("News feed loads through read-only HTTP API",view.Shown?.Items.Length>0);Capture("news-feed-dark");
        var row=Diagnostics.Descendants(view).OfType<Expander>().FirstOrDefault();if(row!=null)row.IsExpanded=true;await Task.Delay(80);Capture("news-story-expanded");
        foreach(double width in new[]{300d,526d})
        {
            settings.PanelWidth=width;app.ChangeSettings();island.SelectPage("news",false);await Task.Delay(100);
            view=Diagnostics.Descendants(island).OfType<NewsView>().Single();await Wait();
            check("News fits panel width "+width,view.ActualWidth>0 && view.ActualWidth<=island.PaintedWidth);
            var footer=Diagnostics.Descendants(view).OfType<Button>().Single(b=>Equals(b.Content,"Подключение"));
            check("News footer fits at width "+width,footer.TransformToAncestor(island).TransformBounds(new Rect(footer.RenderSize)).Bottom<=island.PaintedHeight-8);
            Capture("news-width-"+width);
        }
        settings.ColorTheme="Light";app.ChangeSettings();island.SelectPage("news",false);await Task.Delay(120);view=Diagnostics.Descendants(island).OfType<NewsView>().Single();await Wait();Capture("news-digest-light");
        check("News renders with the native Light theme",ThemeInk.IsLight && view.Shown?.Items.Length>0);
        check("News diagnostics did not enable microphone or capture",!app.Assistant.VoiceConnected && !app.Assistant.Desktop.Sharing);
    }
}
