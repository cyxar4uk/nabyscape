using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NabyScape;

internal sealed class NewsView : UserControl
{
    private readonly App app;
    private readonly StackPanel root=new(),stories=new(),connection=new();
    private readonly TextBlock status=UI.Text("",11,UI.Muted),edition=UI.Text("",11,UI.Muted);
    private readonly Button refresh,digestTab,feedTab;
    private readonly ScrollViewer storyScroll;
    internal event Action? ContentSizeChanged;
    internal void FitTo(double height)=>storyScroll.MaxHeight=Math.Clamp(height-230,70,220);
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMinutes(1)};
    private CancellationTokenSource? request;
    private bool feed,busy;
    private string configuration="";
    internal bool Busy=>busy;
    internal bool FeedSelected=>feed;
    internal NewsSnapshot? Shown {get;private set;}
    internal NewsView(App app)
    {
        this.app=app;
        var heading=new StackPanel();var brand=UI.Text("ANYWAY NEWS",10,UI.Muted);brand.Margin=new(0,0,0,5);heading.Children.Add(brand);
        var title=UI.Text("Новости рядом",23);title.FontWeight=FontWeights.SemiBold;heading.Children.Add(title);
        refresh=UI.Button("Обновить",()=>_=Reload(true));refresh.Content=UI.Text("↻",20);refresh.ToolTip="Обновить";refresh.Width=32;refresh.MinHeight=32;refresh.Padding=new(0);
        var header=new Grid{Margin=new(0,0,0,16)};header.ColumnDefinitions.Add(new());header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});header.Children.Add(heading);Grid.SetColumn(refresh,1);header.Children.Add(refresh);root.Children.Add(header);
        digestTab=UI.Button("Сводка",()=>Select(false));feedTab=UI.Button("Лента",()=>Select(true));
        foreach(var tab in new[]{digestTab,feedTab}){tab.FontSize=12;tab.MinWidth=84;tab.Margin=new(0,0,8,0);}
        root.Children.Add(UI.Row(digestTab,feedTab));edition.Margin=new(0,14,0,6);root.Children.Add(edition);
        storyScroll=UI.Scroll(stories);storyScroll.MaxHeight=220;storyScroll.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;storyScroll.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;UI.Named(storyScroll,"Новости Anyway");root.Children.Add(storyScroll);
        status.Margin=new(0,10,0,0);root.Children.Add(status);
        var settings=UI.Button("Подключение",()=>{connection.Visibility=connection.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;});
        settings.Background=Brushes.Transparent;settings.BorderThickness=new(0);settings.FontSize=11;settings.HorizontalAlignment=HorizontalAlignment.Left;settings.Margin=new(0,6,0,0);root.Children.Add(settings);
        BuildConnection();root.Children.Add(connection);Content=root;
        root.SizeChanged+=(_,_)=>ContentSizeChanged?.Invoke();
        Loaded+=(_,_)=>{configuration=Configuration;app.SettingsChanged-=Preferences;app.SettingsChanged+=Preferences;if(IsVisible){timer.Start();_=Reload();}};
        IsVisibleChanged+=(_,_)=>{if(IsVisible && IsLoaded){timer.Start();_=Reload();}else Stop();};
        Unloaded+=(_,_)=>{app.SettingsChanged-=Preferences;Stop();};timer.Tick+=(_,_)=>{if(!busy && IsVisible)_=Reload();};
        Tabs();
    }
    private void Stop(){timer.Stop();request?.Cancel();}
    private string Configuration=>app.Settings.NewsConnected+"|"+app.Settings.NewsApiBaseUrl;
    private void Preferences()
    {if(configuration==Configuration)return;configuration=Configuration;BuildConnection();if(IsVisible && IsLoaded)_=Reload();}
    private void Tabs()
    {
        digestTab.FontWeight=feed?FontWeights.Normal:FontWeights.SemiBold;feedTab.FontWeight=feed?FontWeights.SemiBold:FontWeights.Normal;
        System.Windows.Automation.AutomationProperties.SetItemStatus(digestTab,feed?"":"Выбрано");
        System.Windows.Automation.AutomationProperties.SetItemStatus(feedTab,feed?"Выбрано":"");
    }
    internal void Select(bool value){if(feed==value)return;feed=value;Tabs();_=Reload();}
    private void BuildConnection()
    {
        connection.Children.Clear();
        connection.Visibility=app.Settings.NewsConnected?Visibility.Collapsed:Visibility.Visible;connection.Margin=new(0,12,0,0);
        connection.Children.Add(UI.Text("Адрес API",11,UI.Muted));
        var input=UI.Input("Адрес API Anyway",app.Settings.NewsApiBaseUrl);input.Margin=new(0,6,0,8);connection.Children.Add(input);
        connection.Children.Add(UI.Text("Общая сводка и публичная лента. Доступ к аккаунту и токен бота не нужны.",11,UI.Muted));
        var connect=UI.Button("Подключить",()=>
        {
            try{app.Settings.NewsApiBaseUrl=NewsService.BaseUri(input.Text).AbsoluteUri.TrimEnd('/');app.Settings.NewsConnected=true;app.ChangeSettings();}
            catch(ArgumentException e){status.Text=e.Message;}
        });connect.Margin=new(0,8,0,0);connection.Children.Add(connect);
        if(app.Settings.NewsConnected)connection.Children.Add(UI.Button("Отключить",()=>{app.Settings.NewsConnected=false;app.ChangeSettings();}));
    }
    internal async Task Reload(bool force=false)
    {
        request?.Cancel();request?.Dispose();request=new();var current=request;
        if(!app.Settings.NewsConnected){busy=false;Shown=null;stories.Children.Clear();edition.Text="Главное из нашего бота";status.Text="Подключите API, чтобы получать новости.";refresh.IsEnabled=false;return;}
        bool selected=feed;string address=app.Settings.NewsApiBaseUrl;busy=true;refresh.IsEnabled=false;
        var saved=app.News.Saved(address,selected);Render(saved);status.Text="Загружаем новости…";
        try
        {
            var snapshot=await app.News.Get(address,selected,force,current.Token);
            if(current!=request || !IsLoaded || !IsVisible)return;
            Render(snapshot);status.Text="Обновлено в "+snapshot.FetchedAt.ToLocalTime().ToString("HH:mm")+" · общая подборка";
        }
        catch(OperationCanceledException){if(current==request && IsVisible)status.Text="Не удалось дождаться API. Нажмите «Обновить».";}
        catch(Exception ex) when(ex is System.Net.Http.HttpRequestException or System.IO.InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {if(current==request && IsVisible)status.Text=(saved!=null?"Показаны сохранённые новости. ":"")+ex.Message;}
        finally{if(current==request){busy=false;refresh.IsEnabled=app.Settings.NewsConnected;_ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>ContentSizeChanged?.Invoke());}}
    }
    internal void Render(NewsSnapshot? snapshot)
    {
        Shown=snapshot;stories.Children.Clear();edition.Text=snapshot?.Title??(feed?"Последние новости":"Главное к этому часу");
        if(snapshot==null)return;
        if(snapshot.Items.Length==0){stories.Children.Add(UI.Text("Свежих новостей пока нет. Попробуйте позже.",13,UI.Muted));return;}
        int index=0;
        foreach(var item in snapshot.Items)
        {
            var text=new StackPanel();var meta=UI.Text((++index).ToString("00")+"  ·  "+item.Source,10,UI.Muted);meta.Margin=new(0,0,0,5);text.Children.Add(meta);
            var title=UI.Text(item.Title,14);title.FontWeight=FontWeights.Medium;text.Children.Add(title);
            var details=new StackPanel();
            if(item.Summary.Length>0){var summary=UI.Text(item.Summary,12,UI.Muted);summary.Margin=new(0,8,0,0);details.Children.Add(summary);}
            if(item.PublishedAt.Length>0)details.Children.Add(UI.Text(item.PublishedAt+" МСК",10,UI.Muted));
            if(item.Url!=null)
            {
                var open=UI.Button("Открыть новость ↗",()=>
                {
                    try{Process.Start(new ProcessStartInfo(item.Url.AbsoluteUri){UseShellExecute=true});}
                    catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){status.Text="Не удалось открыть ссылку.";}
                });open.FontSize=11;open.HorizontalAlignment=HorizontalAlignment.Left;open.Margin=new(0,8,0,0);details.Children.Add(open);
            }
            // Legal source labels remain visible even when a story is collapsed.
            foreach(string note in item.Notes){var label=UI.Text(note,10,UI.Muted);label.Margin=new(0,5,0,0);text.Children.Add(label);}
            var row=new Expander{Header=text,Content=details,HorizontalContentAlignment=HorizontalAlignment.Stretch,Margin=new(0,4,8,10)};
            UI.Named(row,item.Title);stories.Children.Add(row);
        }
    }
}
