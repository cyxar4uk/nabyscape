using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NabyScape;

// Native adaptation of frontend-v2/src/proto/feed.jsx (DayPoster + Digest).
// Weather is absent. The original Anyway brand opens the mini app.
internal sealed class NewsView : UserControl
{
    private readonly App app;
    private readonly Action<Uri> openLink;
    private readonly StackPanel root=new(),stories=new(),connection=new();
    private readonly TextBlock status=UI.Text("",10,UI.Muted),edition=UI.Text("Сводка",12);
    private readonly Button refresh,feedButton;
    private readonly ScrollViewer storyScroll;
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMinutes(1)};
    private CancellationTokenSource? request;
    private bool feed,busy;
    private string configuration="";
    private Border? poster;
    private Image? cover;
    private TextBlock? posterTitle;
    internal event Action? ContentSizeChanged;
    internal void FitTo(double height){storyScroll.MaxHeight=Math.Clamp(height-165,110,500);FitPoster();}
    internal bool Busy=>busy;
    internal NewsSnapshot? Shown {get;private set;}
    internal Action<Uri>? LinkObserver {get;set;}
    private void Navigate(Uri uri){if(LinkObserver!=null)LinkObserver(uri);else openLink(uri);}

    internal NewsView(App app,Action<Uri>? open=null)
    {
        this.app=app;openLink=open??OpenBrowser;
        var header=new Grid{Margin=new(0,0,0,12)};header.ColumnDefinitions.Add(new());header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var bolt=Icons.Create("news-bolt",13,UI.Brush("#91BFFF"));bolt.Margin=new(0,0,6,0);var label=UI.Row(bolt,edition);label.VerticalAlignment=VerticalAlignment.Center;
        header.Children.Add(new Border{Child=label,Background=UI.Brush("#24272D"),CornerRadius=new(100),Padding=new(11,5,11,5),HorizontalAlignment=HorizontalAlignment.Left});
        refresh=UI.Button("Обновить сводку",()=>_=Reload(true));refresh.Content=UI.Text("↻",19);refresh.Width=refresh.Height=30;refresh.MinHeight=30;refresh.Padding=new(0);refresh.Tag=new CornerRadius(100);refresh.ToolTip="Обновить сводку";Grid.SetColumn(refresh,1);header.Children.Add(refresh);root.Children.Add(header);
        storyScroll=UI.Scroll(stories);storyScroll.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;storyScroll.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;storyScroll.MaxHeight=360;UI.Named(storyScroll,"Сводка Anyway News");root.Children.Add(storyScroll);
        var footer=new Grid{Margin=new(0,12,0,0)};footer.ColumnDefinitions.Add(new());footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var logo=new Image{Width=27,Height=27,Source=new BitmapImage(new Uri("pack://application:,,,/Assets/anyway-logo.jpg")),Stretch=Stretch.Uniform};ThemeInk.SetTone(logo,Tone.Fixed);
        var brandLabel=UI.Text("Anyway News",12);brandLabel.FontWeight=FontWeights.SemiBold;brandLabel.Margin=new(8,0,0,0);
        var brand=UI.Button("Открыть приложение Anyway News",()=>Navigate(NewsService.AppLink));brand.Content=UI.Row(logo,brandLabel);brand.Background=Brushes.Transparent;brand.BorderThickness=new(0);brand.Padding=new(0);brand.HorizontalAlignment=HorizontalAlignment.Left;brand.ToolTip="Открыть приложение Anyway News";footer.Children.Add(brand);
        feedButton=UI.Button("Лента →",()=>Select(!feed));feedButton.Background=Brushes.Transparent;feedButton.BorderThickness=new(0);feedButton.FontSize=11;feedButton.Padding=new(5,0,0,0);Grid.SetColumn(feedButton,1);footer.Children.Add(feedButton);root.Children.Add(footer);
        status.Margin=new(0,8,0,0);root.Children.Add(status);
        var settings=UI.Button("Подключение",()=>{connection.Visibility=connection.IsVisible?Visibility.Collapsed:Visibility.Visible;});settings.Background=Brushes.Transparent;settings.BorderThickness=new(0);settings.FontSize=10;settings.Padding=new(0);settings.MinHeight=20;settings.HorizontalAlignment=HorizontalAlignment.Left;root.Children.Add(settings);
        BuildConnection();root.Children.Add(connection);
        Content=new Border{Child=root,Background=UI.Brush("#15171B"),BorderBrush=UI.Brush("#30343D"),BorderThickness=new(.7),CornerRadius=new(22),Padding=new(14,12,14,12)};
        root.SizeChanged+=(_,_)=>{FitPoster();ContentSizeChanged?.Invoke();};
        Loaded+=(_,_)=>{configuration=Configuration;app.SettingsChanged-=Preferences;app.SettingsChanged+=Preferences;if(IsVisible){timer.Start();_=Reload();}};
        IsVisibleChanged+=(_,_)=>{if(IsVisible && IsLoaded){timer.Start();_=Reload();}else Stop();};
        Unloaded+=(_,_)=>{app.SettingsChanged-=Preferences;Stop();};timer.Tick+=(_,_)=>{if(!busy && IsVisible)_=Reload();};
    }
    private double PosterHeight=>Math.Min(Math.Clamp((root.ActualWidth>0?root.ActualWidth:300)*.75,155,260),Math.Max(100,storyScroll.MaxHeight-8));
    private void FitPoster()
    {
        if(poster==null || posterTitle==null)return;poster.Height=PosterHeight;
        double width=Math.Max(130,(root.ActualWidth>0?root.ActualWidth:300)-24);
        foreach(double size in new[]{23d,20d,17d,15d})
        {posterTitle.FontSize=size;posterTitle.Measure(new Size(width,double.PositiveInfinity));if(posterTitle.DesiredSize.Height+62<=PosterHeight)break;}
    }
    private void Stop(){timer.Stop();request?.Cancel();}
    private string Configuration=>app.Settings.NewsConnected+"|"+app.Settings.NewsApiBaseUrl;
    private void Preferences(){if(configuration==Configuration)return;configuration=Configuration;BuildConnection();if(IsVisible && IsLoaded)_=Reload();}
    private void OpenBrowser(Uri uri)
    {try{Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){status.Text="Не удалось открыть приложение.";}}
    internal void Select(bool value){if(feed==value)return;feed=value;feedButton.Content=feed?"← Сводка":"Лента →";_=Reload();}
    private void BuildConnection()
    {
        connection.Children.Clear();connection.Visibility=app.Settings.NewsConnected?Visibility.Collapsed:Visibility.Visible;connection.Margin=new(0,12,0,0);
        connection.Children.Add(UI.Text("Адрес API",11,UI.Muted));var input=UI.Input("Адрес API Anyway",app.Settings.NewsApiBaseUrl);input.Margin=new(0,6,0,8);connection.Children.Add(input);
        connection.Children.Add(UI.Text("Общая сводка и публичная лента. Токен бота не нужен.",11,UI.Muted));
        var connect=UI.Button("Подключить",()=>{try{app.Settings.NewsApiBaseUrl=NewsService.BaseUri(input.Text).AbsoluteUri.TrimEnd('/');app.Settings.NewsConnected=true;app.ChangeSettings();}catch(ArgumentException e){status.Text=e.Message;}});connect.Margin=new(0,8,0,0);connection.Children.Add(connect);
        if(app.Settings.NewsConnected)connection.Children.Add(UI.Button("Отключить",()=>{app.Settings.NewsConnected=false;app.ChangeSettings();}));
    }
    internal async Task Reload(bool force=false)
    {
        request?.Cancel();request?.Dispose();request=new();var current=request;
        if(!app.Settings.NewsConnected){busy=false;Shown=null;stories.Children.Clear();status.Text="Подключите API, чтобы получать сводку.";refresh.IsEnabled=false;return;}
        bool selected=feed;string address=app.Settings.NewsApiBaseUrl;busy=true;refresh.IsEnabled=false;
        var saved=app.News.Saved(address,selected);Render(saved);status.Text="Обновляем сводку…";
        try
        {
            var snapshot=await app.News.Get(address,selected,force,current.Token);
            if(current!=request || !IsLoaded || !IsVisible)return;
            Render(snapshot);status.Text="Обновлено в "+snapshot.FetchedAt.ToLocalTime().ToString("HH:mm")+" · общая подборка";
            if(!selected && snapshot.Items.FirstOrDefault() is {} first && cover!=null)
            {
                var target=cover;
                try
                {
                    var bytes=await app.News.Cover(address,first.Cover,current.Token);
                    if(bytes!=null && current==request && ReferenceEquals(cover,target))
                    {using var stream=new MemoryStream(bytes);var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=800;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();target.Source=bitmap;}
                }
                catch(OperationCanceledException) when(!current.IsCancellationRequested){/* Cover timeout does not invalidate a loaded digest. */}
                catch(Exception ex) when(ex is System.Net.Http.HttpRequestException or IOException or NotSupportedException or System.Runtime.InteropServices.COMException){/* Keep the headline usable without an image. */}
            }
        }
        catch(OperationCanceledException){if(current==request && IsVisible)status.Text="Не удалось дождаться API. Нажмите обновление.";}
        catch(Exception ex) when(ex is System.Net.Http.HttpRequestException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {if(current==request && IsVisible)status.Text=(saved!=null?"Показаны сохранённые новости. ":"")+ex.Message;}
        finally{if(current==request){busy=false;refresh.IsEnabled=app.Settings.NewsConnected;_ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>ContentSizeChanged?.Invoke());}}
    }
    internal void Render(NewsSnapshot? snapshot)
    {
        Shown=snapshot;stories.Children.Clear();poster=null;cover=null;posterTitle=null;edition.Text=feed?"Лента":"Сводка";
        if(snapshot==null)return;
        if(snapshot.Items.Length==0){stories.Children.Add(UI.Text("Свежей сводки пока нет",13,UI.Muted));return;}
        if(!feed)stories.Children.Add(Poster(snapshot.Items[0]));
        foreach(var item in snapshot.Items.Skip(feed?0:1).Take(feed?20:3))stories.Children.Add(CompactStory(item));
    }
    private FrameworkElement Poster(NewsItem item)
    {
        var scene=new Grid{Background=UI.Brush("#243D64")};ThemeInk.SetTone(scene,Tone.Fixed);cover=new Image{Stretch=Stretch.UniformToFill};scene.Children.Add(cover);
        scene.Children.Add(new Border{Background=new LinearGradientBrush(new GradientStopCollection{new(Color.FromArgb(0,0,0,0),0),new(Color.FromArgb(30,0,0,0),.3),new(Color.FromArgb(220,0,0,0),1)},new Point(0,0),new Point(0,1))});
        var tag=UI.Text("ГЛАВНОЕ",9,Brushes.White);tag.FontWeight=FontWeights.SemiBold;
        scene.Children.Add(new Border{Child=tag,Background=UI.Brush("#B0000000"),CornerRadius=new(100),Padding=new(9,5,9,5),Margin=new(10),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top});
        var caption=new StackPanel{Margin=new(12,10,12,12),VerticalAlignment=VerticalAlignment.Bottom};
        var title=UI.Text(item.Title,23,Brushes.White);posterTitle=title;title.FontWeight=FontWeights.Bold;caption.Children.Add(title);
        var sources=UI.Text(item.Source,10,UI.Brush("#E0FFFFFF"));sources.Margin=new(0,7,0,0);caption.Children.Add(sources);
        foreach(var note in item.Notes)caption.Children.Add(UI.Text(note,10,Brushes.White));scene.Children.Add(caption);
        var frame=new Border{Child=scene,Height=PosterHeight,CornerRadius=new(16),Margin=new(0,0,0,8)};poster=frame;
        FitPoster();
        frame.SizeChanged+=(_,_)=>frame.Clip=new RectangleGeometry(new Rect(frame.RenderSize),16,16);
        return StoryButton(item,frame);
    }
    private Button StoryButton(NewsItem item,FrameworkElement content)
    {var button=UI.Button(item.Title,()=>{if(item.Url!=null)Navigate(item.Url);});button.Content=content;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;button.Background=Brushes.Transparent;button.BorderThickness=new(0);button.Padding=new(0);button.Margin=new(0);button.Tag=new CornerRadius(16);button.ToolTip=item.Title;return button;}
    private FrameworkElement CompactStory(NewsItem item)
    {
        var row=new Grid{Margin=new(0,8,0,8)};row.ColumnDefinitions.Add(new(){Width=new GridLength(31)});row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=new GridLength(15)});
        var dot=Icons.Create("news-bolt",14,UI.Brush("#8CBFFF"));row.Children.Add(new Border{Child=dot,Background=UI.Brush("#203048"),Width=25,Height=28,CornerRadius=new(9),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center});dot.HorizontalAlignment=HorizontalAlignment.Center;
        var copy=new StackPanel();var title=UI.Text(item.Title,12);title.FontWeight=FontWeights.SemiBold;copy.Children.Add(title);var sources=UI.Text(item.Source,10,UI.Muted);sources.Margin=new(0,4,0,0);copy.Children.Add(sources);
        foreach(var note in item.Notes)copy.Children.Add(UI.Text(note,10,UI.Muted));Grid.SetColumn(copy,1);row.Children.Add(copy);var arrow=UI.Text("›",18,UI.Muted);arrow.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(arrow,2);row.Children.Add(arrow);
        return new Border{Child=StoryButton(item,row),BorderBrush=UI.Brush("#30343D"),BorderThickness=new(0,0,0,.5)};
    }
}
