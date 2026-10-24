using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ServerTray.Core;
using Forms = System.Windows.Forms;

namespace ServerTray.App;

public partial class App : System.Windows.Application
{
    public static App Instance => (App)Current;
    public AppConfiguration Config { get; private set; } = new();
    public MonitorEngine Engine { get; private set; } = null!;
    public CredentialVault Vault { get; private set; } = null!;
    public KnownHosts Hosts { get; private set; } = null!;
    public HistoryStore History { get; private set; } = null!;
    public AlertEngine Alerts { get; private set; } = null!;
    public RollingLog Log { get; private set; } = null!;
    public string DataRoot { get; private set; } = "";
    public MainWindow Window { get; private set; } = null!;
    public bool Demo { get; private set; }
    public bool Fixture { get; private set; }
    private FixtureInfo? fixture;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? normalIcon, warningIcon, disconnectedIcon;
    private int iconState = -1;
    private System.Windows.Threading.DispatcherTimer? clickTimer;
    private Window? quick;
    private List<ServerViewModel>? quickRows;
    private Mutex? mutex;
    private EventWaitHandle? signal;
    private RegisteredWaitHandle? signalWait;
    private readonly SemaphoreSlim configLock = new(1);
    private CancellationTokenSource lifetime = new();
    private bool exiting;
    private string? notificationTarget;
    private Task? maintenance;
    private BenchmarkRecorder? benchmark;
    private System.Windows.Threading.DispatcherTimer? notificationTimer;
    private readonly List<AlertEvent> pendingNotifications = [];
    private long summarizeUntil;
    private bool notificationsSuspended;
    public event Action<ServerState>? StateChanged;
    protected override void OnStartup(StartupEventArgs e)
    {
        // Charts update only on new data. Software composition avoids loading a
        // large GPU driver into a small tray process and keeps memory predictable.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        try
        {
            Demo = e.Args.Contains("--demo"); var fixturePath = Argument(e.Args, "--fixture"); Fixture = fixturePath is not null;
            DataRoot = Path.GetFullPath(Argument(e.Args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerTray")); Directory.CreateDirectory(DataRoot);
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataRoot.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant() + Environment.UserName)))[..24];
            mutex = new Mutex(true, "Local\\ServerTray-" + identity, out var first);
            signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ServerTray-show-" + identity);
            if (!first) { signal.Set(); Shutdown(); return; }
            signalWait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => Dispatcher.BeginInvoke(() => ShowMain()), null, Timeout.Infinite, false);
            Vault = new(DataRoot); Hosts = new(DataRoot); Log = new(DataRoot); History = new(Path.Combine(DataRoot, "history.sqlite")); Alerts = new(Path.Combine(DataRoot, "alert-state.json"));
            Config = ConfigFile.Load(Path.Combine(DataRoot, "config.json"));
            if (Demo || Fixture)
            {
                if (Fixture) fixture = JsonSerializer.Deserialize<FixtureInfo>(File.ReadAllText(fixturePath!), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var count = int.TryParse(Argument(e.Args, "--count"), out var n) ? Math.Clamp(n, 1, 20) : 5;
                if (Fixture && (fixture is null || fixture.Port < 1024 || string.IsNullOrEmpty(fixture.Fingerprint))) throw new InvalidDataException("Invalid loopback fixture receipt.");
                Config = Config with { DefaultServerId = null, Servers = Enumerable.Range(1, count).Select(i => new ServerProfile { Id = "fixture-" + i, Name = "Server " + i.ToString("00"), Host = Fixture ? "127.0.0.1" : "server-" + i.ToString("00") + ".example.invalid", Port = fixture?.Port ?? 22, Username = "monitor", Group = i % 2 == 0 ? "Production" : "Development", Order = i, ExpiresOn = DateOnly.FromDateTime(DateTime.Today.AddDays(45)) }).ToList() };
            }
            ApplyTheme(); StartEngine(false);
            Window = new(int.TryParse(Argument(e.Args, "--history-hours"), out var hours) ? hours : 1); MainWindow = Window;
            InitializeTray(); InitializeNotifications(); Alerts.Raised += Notify;
            NetworkChange.NetworkAvailabilityChanged += NetworkChanged; SystemEvents.PowerModeChanged += PowerChanged; SystemEvents.UserPreferenceChanged += PreferencesChanged;
            if (!NetworkInterface.GetIsNetworkAvailable() && !Demo && !Fixture) Engine.SetNetworkAvailable(false);
            maintenance = Task.Run(Maintain);
            if (!e.Args.Contains("--minimized")) Window.Show();
            // Let WPF paint before SSH authentication and key exchange compete
            // for CPU, disk pages and the thread pool during a cold launch.
            Dispatcher.BeginInvoke(() => Engine.Start(), System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (int.TryParse(Argument(e.Args, "--bench-seconds"), out var seconds))
            {
                benchmark = new(this, Math.Max(10, seconds), Argument(e.Args, "--bench-output") ?? Path.Combine(DataRoot, "benchmark.json")); benchmark.Start();
            }
            Log.Write("started");
        }
        catch (Exception ex) { MessageBox.Show(Text.Get("StartupError") + "\n" + ex.GetType().Name + "\n" + (ex is ConfigurationException ? ex.Message : ""), "ServerTray", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    public void ContentReady(double milliseconds) { benchmark?.RecordStartup(milliseconds); }
    public static string? Argument(string[] args, string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    private void StartEngine(bool activate = true)
    {
        ICredentialStore credentialStore = Fixture ? new FixtureCredentials() : Vault;
        IHostKeyStore pins = Fixture ? new FixturePins(fixture!.Fingerprint) : Hosts;
        Engine = new(Config, p => Demo ? new DemoSource(p.Order) : new SshMonitorSource(p, Config, credentialStore, pins));
        Engine.Updated += state =>
        {
            try
            {
                if (state.Status == ConnectionStatus.Online && state.Sample is { } sample) History.Add(sample);
                Alerts.Evaluate(state, state.Profile.Alerts ?? Config.Alerts, Zone, DateTimeOffset.UtcNow, Config.Monitoring.IntervalSeconds);
            }
            catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException) { Log.Write("storage-error"); }
            Dispatcher.BeginInvoke(() => { StateChanged?.Invoke(state); quickRows?.FirstOrDefault(row => row.Id == state.Profile.Id)?.Update(state); UpdateTray(); });
        };
        Engine.SystemEvent += kind => { Alerts.ResetContinuity(); Alerts.System(kind, DateTimeOffset.UtcNow); };
        if (activate) Engine.Start();
    }
    public TimeZoneInfo Zone => Config.TimeZoneId == "Local" ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(Config.TimeZoneId);
    public async Task SaveConfiguration(AppConfiguration next, string[]? clearCredentials = null)
    {
        next.Validate(); await configLock.WaitAsync();
        try
        {
            if (!Demo && !Fixture) ConfigFile.Save(Path.Combine(DataRoot, "config.json"), next);
            if (!Demo && !Fixture && next.AutoStart != Config.AutoStart) ApplyAutoStart(next.AutoStart);
            quick?.Close(); quick = null;
            await Engine.DisposeAsync(); if (clearCredentials is not null) foreach (var id in clearCredentials) Vault.Remove(id); Alerts.ResetContinuity(); Config = next; ApplyTheme(); StartEngine(); Window.Reload();
        }
        finally { configLock.Release(); }
    }
    private static void ApplyAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
        if (enabled) key.SetValue("ServerTray", "\"" + Environment.ProcessPath + "\" --minimized"); else key.DeleteValue("ServerTray", false);
    }
    private void ApplyTheme()
    {
        var dark = Config.Theme == ThemeKind.Dark;
        if (Config.Theme == ThemeKind.System) { using var k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"); dark = k?.GetValue("AppsUseLightTheme") is int v && v == 0; }
        var palette = dark ? new[] { "#111A23", "#192632", "#14212C", "#E3EDF4", "#9DAFBF", "#30414E", "#4BC99A", "#203D35" } : new[] { "#F4F6F9", "#FFFFFF", "#F9FAFC", "#1C2D3C", "#697988", "#E1E7EC", "#198B68", "#E8F6EF" };
        string[] names = ["Bg", "Surface", "Sidebar", "Ink", "Muted", "Line", "Accent", "AccentSoft"];
        for (var i = 0; i < names.Length; i++) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[i])); brush.Freeze(); Resources[names[i]] = brush; }
        Resources["AccentInk"] = dark ? new SolidColorBrush(Color.FromRgb(16, 36, 28)) : Brushes.White;
    }
    private void InitializeTray()
    {
        System.Drawing.Icon LoadIcon(string name) { using var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/" + name)).Stream; return new System.Drawing.Icon(resource); }
        normalIcon = LoadIcon("servertray.ico"); warningIcon = LoadIcon("servertray-warning.ico"); disconnectedIcon = LoadIcon("servertray-disconnected.ico");
        tray = new Forms.NotifyIcon { Icon = normalIcon, Visible = true, Text = "ServerTray" };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add(Text.Get("OpenWindow"), null, (_, _) => ShowMain()); menu.Items.Add(Text.Get("Refresh"), null, (_, _) => Engine.Retry()); menu.Items.Add(Text.Get("Settings"), null, (_, _) => { ShowMain(); Window.ShowSettings(); }); menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add(Text.Get("Exit"), null, async (_, _) => await ExitAsync()); tray.ContextMenuStrip = menu;
        clickTimer = new() { Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime) }; clickTimer.Tick += (_, _) => { clickTimer.Stop(); ToggleQuick(); };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) clickTimer.Start(); };
        tray.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) { clickTimer.Stop(); ShowMain(); } };
        tray.BalloonTipClicked += (_, _) => ShowMain(notificationTarget);
    }
    public void ShowMain(string? id = null) { quick?.Close(); quick = null; if (Window is null) return; Window.Show(); Window.WindowState = WindowState.Normal; Window.Activate(); if (id is not null) Window.SelectServer(id); }
    private void ToggleQuick()
    {
        if (quick is not null) { quick.Close(); quick = null; return; }
        quick = new Window { Title = "ServerTray", Width = 360, Height = Math.Min(560, 150 + Config.Servers.Count * 58), WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true };
        quickRows = [];
        var panel = new StackPanel { Margin = new(14) }; panel.Children.Add(new TextBlock { Text = Text.Get("QuickTitle"), FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new(4, 2, 0, 12) });
        foreach (var state in Engine.States.Where(s => s.Profile.Enabled).OrderBy(s => s.Profile.Order))
        {
            var id = state.Profile.Id; var row = new ServerViewModel(state); quickRows.Add(row);
            var content = new StackPanel(); var title = new TextBlock { FontWeight = FontWeights.SemiBold }; title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ServerViewModel.Name))); content.Children.Add(title);
            var status = new TextBlock { FontSize = 10 }; status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ServerViewModel.Status))); content.Children.Add(status);
            var rates = new TextBlock { FontSize = 10 }; rates.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ServerViewModel.Network))); content.Children.Add(rates);
            var button = new System.Windows.Controls.Button { DataContext = row, Content = content, HorizontalContentAlignment = HorizontalAlignment.Left }; button.Click += (_, _) => ShowMain(id); panel.Children.Add(button);
        }
        var open = new System.Windows.Controls.Button { Content = Text.Get("OpenWindow") }; open.Click += (_, _) => ShowMain(); panel.Children.Add(open); quick.Content = new System.Windows.Controls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
        var area = SystemParameters.WorkArea; quick.Left = Math.Max(area.Left, area.Right - quick.Width - 12); quick.Top = Math.Max(area.Top, area.Bottom - quick.Height - 12); quick.Closed += (_, _) => quickRows = null; quick.Deactivated += (_, _) => { quick?.Close(); quick = null; }; quick.Show();
    }
    private void UpdateTray()
    {
        if (tray is null) return; var states = Engine.States.Where(s => s.Profile.Enabled).ToArray(); var online = states.Count(s => s.Status == ConnectionStatus.Online);
        var detail = Config.TraySummary == "Selected" ? states.FirstOrDefault(s => s.Profile.Id == (Window?.SelectedId ?? Config.DefaultServerId)) : null;
        var title = detail is null ? $"ServerTray · {online}/{states.Length} {Text.Get("OnlineShort")}" : $"ServerTray · {detail.Profile.Name} CPU {Display.Percent(detail.Sample?.Cpu)}";
        tray.Text = title.Length <= 63 ? title : title[..62];
        var nextIcon = !Engine.IsNetworkAvailable ? 2 : states.Any(NeedsAttention) ? 1 : 0;
        if (nextIcon != iconState) { iconState = nextIcon; tray.Icon = nextIcon == 2 ? disconnectedIcon : nextIcon == 1 ? warningIcon : normalIcon; }
    }
    public string AlertMessage(AlertEvent record)
    {
        var name = Config.Servers.FirstOrDefault(s => s.Id == record.ServerId)?.Name ?? "ServerTray";
        return record.Kind switch { "offline" => name + " · " + Text.Get("NotificationOffline"), "recovered" => name + " · " + Text.Get("NotificationRecovered"), "resource-high" => name + " · " + Text.Get("Metric" + record.Metric) + " " + Display.Percent(record.Value), "resource-recovered" => name + " · " + Text.Get("Metric" + record.Metric) + " · " + Text.Get("NotificationRecovered"), "expiry" => name + " · " + Text.Get("NotificationExpiry") + " " + record.Value?.ToString("0") + "d", "network-lost" => Text.Get("NetworkLost"), "network-restored" => Text.Get("NetworkRestored"), _ => record.Kind };
    }
    private bool NeedsAttention(ServerState s)
    {
        var r = s.Profile.Alerts ?? Config.Alerts;
        return s.IsOffline || s.Status is not (ConnectionStatus.Online or ConnectionStatus.Disabled or ConnectionStatus.Pending or ConnectionStatus.Connecting or ConnectionStatus.Paused)
            || (s.Status == ConnectionStatus.Online && r.Resources && ((r.Cpu && s.Sample?.Cpu >= r.CpuHigh) || (r.Memory && s.Sample?.Memory >= r.MemoryHigh) || (r.Disk && s.Sample?.Disk >= r.DiskHigh)))
            || (r.Expiry && s.Profile.ExpiresOn is { } date && date.DayNumber - DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).DateTime).DayNumber <= 30);
    }
    private void InitializeNotifications()
    {
        notificationTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        notificationTimer.Tick += (_, _) => FlushNotifications(); BeginCurrentSummary();
    }
    private void BeginCurrentSummary()
    {
        pendingNotifications.Clear(); notificationsSuspended = false;
        summarizeUntil = Environment.TickCount64 + 60000; notificationTimer?.Start();
    }
    private void FlushNotifications()
    {
        if (notificationsSuspended || exiting || !Engine.IsNetworkAvailable) { pendingNotifications.Clear(); notificationTimer?.Stop(); return; }
        if (summarizeUntil > 0)
        {
            if (Environment.TickCount64 < summarizeUntil) return;
            summarizeUntil = 0; pendingNotifications.Clear(); notificationTimer?.Stop();
            var current = Engine.States.Where(s => s.Profile.Enabled && NeedsAttention(s)).ToArray();
            if (current.Length == 0) return;
            notificationTarget = current[0].Profile.Id;
            ShowNotification(string.Format(Text.Get("CurrentSummary"), current.Length) + "\n" + string.Join("、", current.Take(5).Select(s => s.Profile.Name)), Forms.ToolTipIcon.Warning); return;
        }
        notificationTimer?.Stop(); if (pendingNotifications.Count == 0) return;
        notificationTarget = pendingNotifications[0].ServerId;
        var message = pendingNotifications.Count == 1 ? AlertMessage(pendingNotifications[0]) : string.Format(Text.Get("NotificationBatch"), pendingNotifications.Count) + "\n" + string.Join("\n", pendingNotifications.Take(3).Select(AlertMessage));
        var warning = pendingNotifications.Any(r => !r.Kind.Contains("recovered", StringComparison.Ordinal) && r.Kind != "network-restored");
        pendingNotifications.Clear(); ShowNotification(message, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }
    private void ShowNotification(string message, Forms.ToolTipIcon icon) { if (!Demo && !Fixture && !notificationsSuspended && !exiting) tray?.ShowBalloonTip(6000, "ServerTray", message, icon); }
    private void Notify(AlertEvent record) => Dispatcher.BeginInvoke(() =>
    {
        Window?.RefreshAlerts(); if (notificationsSuspended || exiting) return;
        if (record.Kind == "network-lost") { pendingNotifications.Clear(); notificationTimer?.Stop(); notificationTarget = null; ShowNotification(AlertMessage(record), Forms.ToolTipIcon.Warning); return; }
        if (record.Kind == "network-restored") { BeginCurrentSummary(); return; }
        if (pendingNotifications.Count < 1000) pendingNotifications.Add(record); notificationTimer?.Start();
    });
    private void NetworkChanged(object? sender, NetworkAvailabilityEventArgs e) { if (!Demo && !Fixture) Engine.SetNetworkAvailable(e.IsAvailable); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) { notificationsSuspended = true; Dispatcher.BeginInvoke(() => { notificationTimer?.Stop(); pendingNotifications.Clear(); }); Alerts.ResetContinuity(); Engine.Pause(); }
        else if (e.Mode == PowerModes.Resume) { Dispatcher.BeginInvoke(BeginCurrentSummary); Alerts.ResetContinuity(); Engine.Resume(); }
    }
    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e) { if (Config.Theme == ThemeKind.System) Dispatcher.BeginInvoke(ApplyTheme); }
    private async Task Maintain()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) { try { History.Flush(Config.Monitoring.RetentionDays, DateTimeOffset.UtcNow); Alerts.Prune(DateTimeOffset.UtcNow); } catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException) { Log.Write("history-flush-error"); } } }
        catch (OperationCanceledException) { }
    }
    public async Task ExitAsync()
    {
        if (exiting) return; exiting = true; notificationTimer?.Stop(); await lifetime.CancelAsync();
        if (Engine is not null) await Engine.DisposeAsync(); if (maintenance is not null) await maintenance;
        try { History?.Dispose(); } catch (Microsoft.Data.Sqlite.SqliteException) { Log?.Write("history-close-error"); }
        benchmark?.Finish(); tray?.Dispose(); quick?.Close(); Log?.Write("stopped"); Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        NetworkChange.NetworkAvailabilityChanged -= NetworkChanged; SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.UserPreferenceChanged -= PreferencesChanged;
        signalWait?.Unregister(null); signal?.Dispose(); try { mutex?.ReleaseMutex(); } catch (ApplicationException) { } mutex?.Dispose(); tray?.Dispose(); normalIcon?.Dispose(); warningIcon?.Dispose(); disconnectedIcon?.Dispose(); base.OnExit(e);
    }
}
internal sealed record FixtureInfo(int Port, string Fingerprint);
internal sealed class FixtureCredentials : ICredentialStore { public Credential Get(string id) => new("fixture-only"); }
internal sealed class FixturePins(string pin) : IHostKeyStore { public string Get(string host, int port) => pin; }
internal sealed class DemoSource(int seed) : IMonitorSource
{
    private readonly Stopwatch time = Stopwatch.StartNew();
    public StaticInfo Info => new(seed % 2 == 0 ? "Ubuntu 24.04 LTS" : "Debian GNU/Linux 13", "x86_64", seed % 2 == 0 ? 4 : 2);
    public Task<RawMetrics> CollectAsync(bool includeDisk, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var t = time.Elapsed.TotalSeconds + seed * 100; var total = (ulong)(t * 100); var busy = .12 + seed * .05;
        return Task.FromResult(new RawMetrics { At = DateTimeOffset.UtcNow, BootId = "00000000-0000-0000-0000-000000000001", Uptime = 86400 * 12 + t, CpuTotal = total, CpuIdle = (ulong)(total * (1 - busy)), MemoryTotal = 4L << 30, MemoryAvailable = (long)((4L << 30) * (.8 - seed * .05)), SwapTotal = 1L << 30, SwapFree = 1L << 30, DiskTotal = 80L << 30, DiskUsed = (18L + seed * 3) << 30, Load = [.12 + seed * .1, .22, .18], DiskRead = (ulong)(t * 40960), DiskWrite = (ulong)(t * 20480), Network = new() { ["eth0"] = new((ulong)(t * (3 + seed) * 40000), (ulong)(t * (2 + seed) * 16000)) } });
    }
    public void Abort() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
