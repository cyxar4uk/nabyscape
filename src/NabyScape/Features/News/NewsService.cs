using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NabyScape;

public sealed partial class Settings
{
    public string NewsApiBaseUrl {get;set;}="https://news.anyway-community.ru/api";
    public bool NewsConnected {get;set;}
}

internal sealed record NewsItem(string Id,string Title,string Summary,string Source,string PublishedAt,Uri? Url,string[] Notes,string Cover="")
{internal string[] SourceIds {get;init;}=Array.Empty<string>();}
internal sealed record NewsSource(string Id,string Name,string Avatar);
internal sealed record NewsSnapshot(string Title,NewsItem[] Items,DateTimeOffset FetchedAt);

// Public read-only API: never reuse Telegram sessions, bot tokens or dev/admin auth.
internal sealed class NewsService : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Dictionary<string,NewsSnapshot> cache=new();
    private readonly Dictionary<string,DateTimeOffset> attempts=new();
    private readonly Dictionary<string,byte[]> images=new();
    private readonly Dictionary<string,NewsSource> sources=new();
    private string sourceOrigin="";
    internal void LoadSources(string path,string address="https://news.anyway-community.ru/api")
    {
        if(!File.Exists(path))return;
        using var doc=JsonDocument.Parse(File.ReadAllBytes(path));if(doc.RootElement.ValueKind!=JsonValueKind.Array)return;
        sources.Clear();sourceOrigin=BaseUri(address).GetLeftPart(UriPartial.Authority);
        foreach(var row in doc.RootElement.EnumerateArray().Take(2000))
        {
            if(!row.TryGetProperty("id",out var id) || !row.TryGetProperty("name",out var name))continue;
            string key=id.ToString(),label=name.GetString()??"",avatar=row.TryGetProperty("avatar",out var photo) && photo.ValueKind==JsonValueKind.String?photo.GetString()??"":"";
            if(avatar.Length>0 && !avatar.StartsWith('/') && !Uri.TryCreate(avatar,UriKind.Absolute,out _))avatar="/media/"+avatar;
            sources[key]=new(key,label,avatar);
        }
    }
    internal NewsSource[] SourcesFor(NewsItem item,string address)
    {
        if(sourceOrigin!=BaseUri(address).GetLeftPart(UriPartial.Authority))return Array.Empty<NewsSource>();
        return item.SourceIds.Where(sources.ContainsKey).Select(id=>sources[id]).Take(2).ToArray();
    }
    internal static readonly Uri AppLink=new("https://t.me/anyway_news_bot?startapp");
    internal NewsService(HttpMessageHandler? handler=null)
    {
        http=new(handler??new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(15)};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NabyScape-Anyway/1.0");
    }
    internal static Uri BaseUri(string value)
    {
        if(!Uri.TryCreate(value?.Trim(),UriKind.Absolute,out var uri) ||
           uri.Scheme!="https" && !(uri.Scheme=="http" && uri.IsLoopback) ||
           uri.UserInfo.Length>0 || uri.Query.Length>0 || uri.Fragment.Length>0)
            throw new ArgumentException("Укажите HTTPS-адрес API или HTTP на localhost, без ключей и параметров.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/')+"/");
    }
    private static string Key(string address,bool feed)=>BaseUri(address).AbsoluteUri+(feed?"feed":"digest");
    internal NewsSnapshot? Saved(string address,bool feed)
    {try{return cache.GetValueOrDefault(Key(address,feed));}catch(ArgumentException){return null;}}
    internal async Task<NewsSnapshot> Get(string address,bool feed,bool force,CancellationToken token)
    {
        var root=BaseUri(address);string key=Key(address,feed);
        await gate.WaitAsync(token);
        try
        {
            var now=DateTimeOffset.UtcNow;
            if(cache.TryGetValue(key,out var saved) && (!force && now-saved.FetchedAt<TimeSpan.FromMinutes(5)))return saved;
            if(attempts.TryGetValue(key,out var last) && now-last<TimeSpan.FromSeconds(15))
            {if(saved!=null)return saved;throw new HttpRequestException("Повторите через несколько секунд.");}
            attempts[key]=now;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(15));token=timeout.Token;
            var endpoint=new Uri(root,feed?"v2/integrations/news/feed":"v2/digest");
            using var response=await http.GetAsync(endpoint,HttpCompletionOption.ResponseHeadersRead,token);
            if(response.StatusCode==System.Net.HttpStatusCode.NotFound && feed)
                throw new HttpRequestException("API ленты ещё не подключён на сервере. Сводка доступна в соседней вкладке.");
            if(!response.IsSuccessStatusCode)throw new HttpRequestException("API новостей недоступен ("+(int)response.StatusCode+"). Повторите позже.");
            using var stream=await response.Content.ReadAsStreamAsync(token);
            using var data=new MemoryStream();var buffer=new byte[8192];int count;
            while((count=await stream.ReadAsync(buffer,token))>0)
            {if(data.Length+count>1024*1024)throw new InvalidDataException("Ответ API слишком большой.");await data.WriteAsync(buffer.AsMemory(0,count),token);}
            var snapshot=Parse(data.ToArray(),feed,DateTimeOffset.UtcNow);cache[key]=snapshot;return snapshot;
        }
        finally{gate.Release();}
    }
    internal static Uri? SafeLink(string? value)
        =>Uri.TryCreate(value,UriKind.Absolute,out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length==0?uri:null;
    internal static Uri? CoverUri(string address,string cover)
    {
        if(string.IsNullOrWhiteSpace(cover))return null;
        var root=BaseUri(address);
        if(!Uri.TryCreate(root,cover,out var uri) || uri.Scheme!=root.Scheme || uri.Authority!=root.Authority || uri.UserInfo.Length>0)return null;
        return uri;
    }
    internal async Task<byte[]?> Cover(string address,string cover,CancellationToken token)
    {
        var uri=CoverUri(address,cover);if(uri==null)return null;
        if(images.TryGetValue(uri.AbsoluteUri,out var saved))return saved;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(15));token=timeout.Token;
        using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token);
        if(!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/",StringComparison.OrdinalIgnoreCase)!=true)return null;
        using var source=await response.Content.ReadAsStreamAsync(token);using var bytes=new MemoryStream();var buffer=new byte[8192];int read;
        while((read=await source.ReadAsync(buffer,token))>0)
        {if(bytes.Length+read>2*1024*1024)return null;await bytes.WriteAsync(buffer.AsMemory(0,read),token);}
        var data=bytes.ToArray();if(images.Count>=32)images.Remove(images.Keys.First());images[uri.AbsoluteUri]=data;return data;
    }
    internal static NewsSnapshot Parse(byte[] json,bool feed,DateTimeOffset fetchedAt)
    {
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("items",out var items) || items.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("API вернул неизвестный формат новостей.");
        static string Text(JsonElement e,string name,int limit=8000)
        {if(!e.TryGetProperty(name,out var v))return "";var s=v.ValueKind==JsonValueKind.String?v.GetString()??"":v.ValueKind==JsonValueKind.Number?v.GetRawText():"";return s.Length>limit?s[..limit]:s;}
        var result=new List<NewsItem>();var seen=new HashSet<string>();
        foreach(var item in items.EnumerateArray())
        {
            if(item.ValueKind!=JsonValueKind.Object)continue;
            string id=Text(item,feed?"id":"postId",80),title=Text(item,feed?"title":"head",2000);
            if(title.Length==0 || !seen.Add(id.Length>0?id:title))continue;
            var notes=item.TryGetProperty("notes",out var n) && n.ValueKind==JsonValueKind.Array?n.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Select(x=>x.GetString()!).ToArray():Array.Empty<string>();
            string source=Text(item,"source",200);
            if(!feed && item.TryGetProperty("nsrc",out var ns) && ns.ValueKind==JsonValueKind.Number && ns.TryGetInt32(out int sources) && sources>0)source="Источников: "+sources;
            var url=SafeLink(Text(item,"url"));
            if(!feed && id.Length>0 && id.All(char.IsAsciiDigit))url=new Uri("https://t.me/anyway_news_bot/app?startapp=post_"+id);
            var sourceIds=item.TryGetProperty("srcs",out var ids) && ids.ValueKind==JsonValueKind.Array?ids.EnumerateArray().Where(v=>v.ValueKind is JsonValueKind.String or JsonValueKind.Number).Select(v=>v.ToString()).Take(4).ToArray():Array.Empty<string>();
            result.Add(new(id,title,Text(item,"summary"),source,Text(item,"publishedAt",80),url,notes,Text(item,"cover",2000)){SourceIds=sourceIds});
            if(result.Count==(feed?20:5))break;
        }
        return new(Text(root,"title",200),result.ToArray(),fetchedAt);
    }
    public void Dispose(){http.Dispose();}
}
