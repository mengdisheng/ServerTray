using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using ServerTray.Core;

namespace ServerTray.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel model = new();
    private App Service => App.Instance;
    private int historyHours = 1, historyRevision;
    private DateTimeOffset lastHistory = DateTimeOffset.MinValue;
    private bool historyBusy;
    public string? SelectedId => (ServerList.SelectedItem as ServerViewModel)?.Id;
    public MainWindow(int initialHistoryHours = 1)
    {
        historyHours = initialHistoryHours is 1 or 6 or 24 or 168 ? initialHistoryHours : 1;
        InitializeComponent(); DataContext = model;
        ServersHeading.Text = Text.Get("ServersHeading"); PageTitle.Text = Text.Get("Overview"); PageSubtitle.Text = Text.Get("Tagline"); OnlineCaption.Text = Text.Get("OnlineCaption"); NetworkCaption.Text = Text.Get("NetworkCaption"); AttentionCaption.Text = Text.Get("AttentionCaption"); EmptyTitle.Text = Text.Get("EmptyTitle"); EmptyDescription.Text = Text.Get("EmptyDescription"); RxCaption.Text = Text.Get("RxCaption"); TxCaption.Text = Text.Get("TxCaption"); IoCaption.Text = Text.Get("IoCaption"); UptimeCaption.Text = Text.Get("UptimeCaption"); HistoryCaption.Text = Text.Get("HistoryCaption"); NetworkHistoryCaption.Text = Text.Get("NetworkHistoryCaption"); ChartLegend.Text = Text.Get("ChartLegend"); Footer.Text = Text.Get("Footer"); NetworkChart.Network = true;
        DemoBanner.Visibility = Service.Demo ? Visibility.Visible : Visibility.Collapsed;
        Service.StateChanged += OnState;
        IsVisibleChanged += async (_, _) => { if (IsVisible) { RenderDetails(); await DrawHistory(); } };
        ContentRendered += (_, _) => Service.ContentReady((DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds);
        Reload();
    }
    protected override void OnClosing(CancelEventArgs e) { e.Cancel = true; Hide(); }
    public void Reload()
    {
        var selected = SelectedId ?? Service.Config.DefaultServerId;
        model.Reload(Service.Engine.States); SelectServer(selected ?? "");
        if (ServerList.SelectedItem is null && model.Servers.Count > 0) ServerList.SelectedIndex = 0;
        EmptyPanel.Visibility = model.Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed; RefreshAlerts(); ApplyFilter();
    }
    public void SelectServer(string id) { ServerList.SelectedItem = model.Servers.FirstOrDefault(s => s.Id == id); }
    private void OnState(ServerState state)
    {
        model.Update(state);
        if (IsVisible && SelectedId == state.Profile.Id) { RenderDetails(); if (DateTimeOffset.UtcNow - lastHistory > TimeSpan.FromSeconds(60)) _ = DrawHistory(); }
    }
    private void RenderDetails()
    {
        if (ServerList.SelectedItem is not ServerViewModel vm) { DetailsPanel.Visibility = Visibility.Collapsed; return; }
        DetailsPanel.Visibility = Visibility.Visible; var state = vm.State; var sample = state.Sample; var fresh = state.Status == ConnectionStatus.Online;
        ServerTitle.Text = vm.Name; ServerStatus.Text = vm.Status; ServerStatus.Foreground = vm.StatusBrush;
        ServerMeta.Text = $"{state.Profile.Host}:{state.Profile.Port}  ·  {state.Info?.OperatingSystem ?? "Linux"}  ·  {state.Info?.Cores.ToString() ?? "—"} vCPU  ·  {state.Info?.Architecture ?? "—"}";
        PageSubtitle.Text = sample is null ? Text.Get("Tagline") : (fresh ? Text.Get("LastUpdated") : Text.Get("StaleData")) + " " + TimeZoneInfo.ConvertTime(sample.At, Service.Zone).ToString("MM-dd HH:mm:ss");
        CpuValue.Text = Display.Percent(sample?.Cpu); MemoryValue.Text = Display.Percent(sample?.Memory); DiskValue.Text = Display.Percent(sample?.Disk);
        CpuBar.Value = sample?.Cpu ?? 0; MemoryBar.Value = sample?.Memory ?? 0; DiskBar.Value = sample?.Disk ?? 0;
        CpuDetail.Text = Service.Config.VisibleMetrics.Contains("load") ? "Load  " + (sample is null ? "—" : string.Join(" / ", sample.Load.Select(v => v.ToString("0.00")))) : "";
        MemoryDetail.Text = Display.Bytes(sample?.MemoryUsed) + " / " + Display.Bytes(sample?.MemoryTotal) + (Service.Config.VisibleMetrics.Contains("swap") ? "\nSwap " + Display.Bytes(sample?.SwapUsed) + " / " + Display.Bytes(sample?.SwapTotal) : "");
        DiskDetail.Text = Display.Bytes(sample?.DiskUsed) + " / " + Display.Bytes(sample?.DiskTotal);
        RxValue.Text = Display.Rate(fresh ? sample?.Rx : null); TxValue.Text = Display.Rate(fresh ? sample?.Tx : null); IoValue.Text = Display.Rate(fresh ? sample?.DiskRead : null) + " ↓\n" + Display.Rate(fresh ? sample?.DiskWrite : null) + " ↑"; UptimeValue.Text = Display.Duration(sample?.Uptime);
        TrafficFootnote.Text = Text.Get("TrafficFootnote") + "\n↓ " + Display.Bytes(sample is null ? null : sample.RxTotal) + "   ↑ " + Display.Bytes(sample is null ? null : sample.TxTotal) + "   ·  " + string.Join(", ", sample?.Interfaces ?? []) + (state.Profile.ExpiresOn is { } expiry ? "\n" + Text.Get("ExpiresOn") + " " + expiry.ToString("yyyy-MM-dd") : "");
        TrustButton.Visibility = Service.Engine.Challenge(vm.Id) is not null ? Visibility.Visible : Visibility.Collapsed;
        ShowMetric(CpuValue, "cpu", 2); ShowMetric(MemoryValue, "memory", 2); ShowMetric(DiskValue, "disk", 2); ShowMetric(RxValue, "network", 1); ShowMetric(TxValue, "network", 1); ShowMetric(IoValue, "io", 1);
    }
    private void ShowMetric(FrameworkElement element, string key, int parents)
    {
        DependencyObject parent = element; for (var i = 0; i < parents; i++) parent = VisualTreeHelper.GetParent(parent); if (parent is UIElement ui) ui.Visibility = Service.Config.VisibleMetrics.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task DrawHistory()
    {
        if (!IsVisible || SelectedId is not { } id || historyBusy) return; var revision = ++historyRevision; var hours = historyHours; historyBusy = true;
        try
        {
            var rows = await Task.Run(() => Service.History.Query(id, hours, DateTimeOffset.UtcNow));
            if (!IsVisible || SelectedId != id || revision != historyRevision) return;
            if (rows.Length == 0) rows = Service.Engine.Recent(id).Select(s => new HistoryPoint(s.At.ToUnixTimeSeconds(), s.Cpu, s.Memory, s.Disk, s.Rx, s.Tx, s.DiskRead, s.DiskWrite)).ToArray();
            var bucket = hours <= 6 ? 60 : hours == 24 ? 300 : 600;
            ResourceChart.BucketSeconds = NetworkChart.BucketSeconds = bucket; ResourceChart.Zone = NetworkChart.Zone = Service.Zone;
            ResourceChart.VisibleMetrics = Service.Config.VisibleMetrics; NetworkChart.Visibility = Service.Config.VisibleMetrics.Contains("network") ? Visibility.Visible : Visibility.Collapsed;
            ResourceChart.SetPoints(rows); NetworkChart.SetPoints(rows); lastHistory = DateTimeOffset.UtcNow;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { Service.Log.Write("history-query-error"); }
        finally { historyBusy = false; if (IsVisible && (revision != historyRevision || SelectedId != id)) _ = DrawHistory(); }
    }
    private async void Server_Selected(object sender, SelectionChangedEventArgs e) { if (!IsInitialized) return; historyRevision++; lastHistory = DateTimeOffset.MinValue; RenderDetails(); await DrawHistory(); }
    private async void Range_Click(object sender, RoutedEventArgs e) { historyHours = int.Parse((string)((Button)sender).Tag); historyRevision++; await DrawHistory(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized) ApplyFilter(); }
    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim(); var view = CollectionViewSource.GetDefaultView(model.Servers);
        view.Filter = o => o is ServerViewModel s && (s.Name + " " + s.Subtitle + " " + string.Join(" ", s.State.Profile.Tags)).Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Service.Engine.Retry();
    private async void Add_Click(object sender, RoutedEventArgs e) => await EditServer(null);
    private async void Edit_Click(object sender, RoutedEventArgs e) { if (ServerList.SelectedItem is ServerViewModel vm) await EditServer(vm.State.Profile); }
    private async Task EditServer(ServerProfile? profile)
    {
        var editor = new ServerEditor(Service.Config, profile, profile is null ? null : Service.Vault.Get(profile.Id)) { Owner = this };
        if (editor.ShowDialog() != true) return;
        try
        {
            var list = Service.Config.Servers.ToList();
            if (editor.DeleteRequested && profile is not null)
            {
                if (list.Any(s => s.JumpServerId == profile.Id)) { MessageBox.Show(this, Text.Get("JumpInUse")); return; }
                list.RemoveAll(s => s.Id == profile.Id);
                await Service.SaveConfiguration(Service.Config with { Servers = list, DefaultServerId = Service.Config.DefaultServerId == profile.Id ? null : Service.Config.DefaultServerId }); Service.Vault.Remove(profile.Id); return;
            }
            if (editor.Result is not { } result) return;
            list.RemoveAll(s => s.Id == result.Id); list.Add(result);
            if (editor.Secret is not null) Service.Vault.Put(result.Id, editor.Secret);
            await Service.SaveConfiguration(Service.Config with { Servers = list }); SelectServer(result.Id);
        }
        catch (Exception ex) when (ex is ConfigurationException or IOException or UnauthorizedAccessException) { MessageBox.Show(this, Text.Get("SaveError") + "\n" + ex.GetType().Name); }
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    public async void ShowSettings()
    {
        var editor = new SettingsEditor(Service.Config) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } result) { try { await Service.SaveConfiguration(result); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigurationException) { MessageBox.Show(this, Text.Get("SaveError") + "\n" + ex.GetType().Name); } }
    }
    private async void MoveUp_Click(object sender, RoutedEventArgs e) => await Move(-1);
    private async void MoveDown_Click(object sender, RoutedEventArgs e) => await Move(1);
    private async Task Move(int delta)
    {
        var list = Service.Config.Servers.OrderBy(s => s.Order).ToList(); var index = list.FindIndex(s => s.Id == SelectedId); if (index < 0 || index + delta < 0 || index + delta >= list.Count) return;
        (list[index], list[index + delta]) = (list[index + delta], list[index]); var id = SelectedId;
        await Service.SaveConfiguration(Service.Config with { Servers = list.Select((p, i) => p with { Order = i }).ToList() }); if (id is not null) SelectServer(id);
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "ServerTray configuration|*.json", FileName = "servertray-config.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { ConfigFile.Save(dialog.FileName, Service.Config); } catch (IOException) { MessageBox.Show(this, Text.Get("SaveError")); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ServerTray configuration|*.json" }; if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 1_048_576) throw new ConfigurationException("Configuration too large.");
            var next = ConfigFile.Parse(File.ReadAllText(dialog.FileName)); var previous = Service.Config;
            var added = next.Servers.Count(s => previous.Servers.All(p => p.Id != s.Id)); var removed = previous.Servers.Count(s => next.Servers.All(p => p.Id != s.Id));
            var changed = next.Servers.Count(s => previous.Servers.Any(p => p.Id == s.Id && JsonSerializer.Serialize(p) != JsonSerializer.Serialize(s)));
            var preview = string.Format(Text.Get("ImportPreview"), added, removed, changed);
            var review = new Window { Title = Text.Get("Import"), Width = 760, Height = 650, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new DockPanel { Margin = new(20) }; review.Content = panel;
            var message = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) }; DockPanel.SetDock(message, Dock.Top); panel.Children.Add(message);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
            buttons.Children.Add(new Button { Content = Text.Get("Cancel"), IsCancel = true });
            var accept = new Button { Content = Text.Get("Import"), Style = (Style)FindResource("Primary") }; accept.Click += (_, _) => review.DialogResult = true; buttons.Children.Add(accept);
            panel.Children.Add(new TextBox { Text = string.Join("\n", ConfigurationDiff.Describe(previous, next)), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            if (review.ShowDialog() != true) return;
            var clear = previous.Servers.Where(old => { var same = next.Servers.FirstOrDefault(s => s.Id == old.Id); return same is null || (same.Host, same.Port, same.Username, same.Authentication) != (old.Host, old.Port, old.Username, old.Authentication); }).Select(s => s.Id).ToArray();
            await Service.SaveConfiguration(next, clear);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ConfigurationException or UnauthorizedAccessException) { MessageBox.Show(this, Text.Get("ImportError") + "\n" + ex.Message); }
    }
    private void Trust_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId is not { } id || Service.Engine.Challenge(id) is not { } challenge) return;
        var message = Text.Get("TrustExplanation") + $"\n\n{challenge.Host}:{challenge.Port}\n{challenge.Fingerprint}" + (challenge.Expected is not null ? "\n\n" + Text.Get("PreviousFingerprint") + "\n" + challenge.Expected : "");
        if (MessageBox.Show(this, message, Text.Get("VerifyHost"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes) { Service.Hosts.Trust(challenge); Service.Engine.Retry(id); }
    }
    public void RefreshAlerts() { model.AlertLines.Clear(); foreach (var alert in Service.Alerts.Events) model.AlertLines.Add(TimeZoneInfo.ConvertTime(alert.At, Service.Zone).ToString("MM-dd HH:mm:ss") + "   " + Service.AlertMessage(alert)); }
    private void Alerts_Click(object sender, RoutedEventArgs e)
    {
        RefreshAlerts(); var list = new ListBox { ItemsSource = model.AlertLines, Margin = new(18), Background = Brushes.Transparent, BorderThickness = new(0) }; VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var window = new Window { Title = Text.Get("Alerts"), Width = 760, Height = 500, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = model.AlertLines.Count > 0 ? list : new TextBlock { Text = Text.Get("NoAlerts"), Margin = new(30), FontSize = 17 } }; window.ShowDialog();
    }
}
