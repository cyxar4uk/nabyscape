using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NabyScape;

internal static class NewsTests
{
    private sealed class Network : HttpMessageHandler
    {
        internal int Calls;
        internal string Path="";
        internal HttpStatusCode Status=HttpStatusCode.OK;
        internal string Body="{\"title\":\"Главное\",\"items\":[{\"postId\":\"123\",\"head\":\"Новость\",\"notes\":[\"Полная сноска\"]}]}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Calls++;Path=request.RequestUri!.AbsolutePath;return Task.FromResult(new HttpResponseMessage(Status){Content=new StringContent(Body,Encoding.UTF8,"application/json")});}
    }
    internal static void Run(Action<string,Action> test)
    {
        void Assert(bool pass){if(!pass)throw new Exception("News invariant failed");}
        test("News accepts HTTPS and loopback, rejects credentials and unsafe addresses",()=>
        {
            Assert(NewsService.BaseUri("https://example.org/api").AbsoluteUri=="https://example.org/api/");
            Assert(NewsService.BaseUri("http://127.0.0.1:8766/api").IsLoopback);
            foreach(string input in new[]{"file:///tmp","http://example.org/api","https://user:secret@example.org","https://example.org?key=secret","javascript:alert(1)",""})
            {bool rejected=false;try{NewsService.BaseUri(input);}catch(ArgumentException){rejected=true;}Assert(rejected);}
            Assert(NewsService.SafeLink("file:///C:/secret")==null && NewsService.SafeLink("javascript:alert(1)")==null);
        });
        test("News preserves full legal notes, deduplicates stories and limits digest",()=>
        {
            string item="{\"postId\":123,\"head\":\"Новость\",\"notes\":[\"Не обрезать эту сноску\"]}";
            var parsed=NewsService.Parse(Encoding.UTF8.GetBytes("{\"items\":["+item+","+item+"]}"),false,DateTimeOffset.UtcNow);
            Assert(parsed.Items.Length==1 && parsed.Items[0].Notes[0]=="Не обрезать эту сноску" && parsed.Items[0].Url!.Query=="?startapp=post_123");
            bool rejected=false;try{NewsService.Parse(Encoding.UTF8.GetBytes("{\"error\":\"down\"}"),false,DateTimeOffset.UtcNow);}catch(InvalidDataException){rejected=true;}Assert(rejected);
            var feed=NewsService.Parse(Encoding.UTF8.GetBytes("{\"items\":[{\"id\":\"1\",\"title\":\"Лента\",\"url\":\"file:///secret\",\"summary\":\"Текст\"}]}"),true,DateTimeOffset.UtcNow);
            Assert(feed.Items.Length==1 && feed.Items[0].Url==null && feed.Items[0].Summary=="Текст");
        });
        test("News caches each API and tab separately and never sends credentials",()=>Task.Run(async ()=>
        {
            var network=new Network();using var service=new NewsService(network);
            var first=await service.Get("https://example.org/api",false,false,CancellationToken.None);
            var second=await service.Get("https://example.org/api",false,false,CancellationToken.None);
            Assert(network.Calls==1 && ReferenceEquals(first,second) && network.Path=="/api/v2/digest");
            network.Body="{\"items\":[]}";await service.Get("https://example.org/api",true,false,CancellationToken.None);
            Assert(network.Calls==2 && network.Path=="/api/v2/integrations/news/feed");
            Assert(service.Saved("https://another.org/api",false)==null);
        }).GetAwaiter().GetResult());
        test("News reports unavailable feed and retains cached digest",()=>Task.Run(async ()=>
        {
            var network=new Network();using var service=new NewsService(network);
            await service.Get("https://example.org/api",false,false,CancellationToken.None);
            network.Status=HttpStatusCode.NotFound;bool rejected=false;
            try{await service.Get("https://example.org/api",true,false,CancellationToken.None);}catch(HttpRequestException e){rejected=e.Message.Contains("ещё не подключён");}
            Assert(rejected && service.Saved("https://example.org/api",false)!=null && service.Saved("https://example.org/api",true)==null);
        }).GetAwaiter().GetResult());
        test("News module is opt-in and survives settings normalization",()=>
        {var settings=new Settings();settings.Normalize();Assert(!settings.Widgets.Find(w=>w.Id=="news")!.Enabled && !settings.NewsConnected);settings.Widgets.Find(w=>w.Id=="news")!.Enabled=true;settings.Normalize();Assert(settings.Widgets.Find(w=>w.Id=="news")!.Enabled);});
    }
}
