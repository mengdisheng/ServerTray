using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ServerTray.Core;

namespace ServerTray.App;

internal sealed class FormPanel
{
    public StackPanel Panel { get; } = new() { Margin = new Thickness(22, 16, 22, 12) };
    public TextBox Field(string label, string value) { Label(label); var b = new TextBox { Text = value }; System.Windows.Automation.AutomationProperties.SetName(b, Text.Get(label)); Panel.Children.Add(b); return b; }
    public CheckBox Check(string label, bool value) { var b = new CheckBox { Content = Text.Get(label), IsChecked = value }; Panel.Children.Add(b); return b; }
    public ComboBox Choice(string label, string[] values, int index) { Label(label); var b = new ComboBox { ItemsSource = values, SelectedIndex = index }; Panel.Children.Add(b); return b; }
    public void Label(string key) => Panel.Children.Add(new TextBlock { Text = Text.Get(key), TextWrapping = TextWrapping.Wrap });
    public void Hint(string key) => Panel.Children.Add(new TextBlock { Text = Text.Get(key), TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Muted"), FontSize = 11, Margin = new(0, 2, 0, 12) });
    public TabItem Tab(string title) => new() { Header = Text.Get(title), Content = new ScrollViewer { Content = Panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    public static int Number(TextBox box) => int.Parse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    public static double Decimal(TextBox box) => double.Parse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
    public static string[] Patterns(TextBox box) => box.Text.Split([',', '，', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
internal sealed class RulesEditor
{
    public FormPanel Form { get; } = new();
    private readonly CheckBox offline, resource, expiry, cpuEnabled, memoryEnabled, diskEnabled;
    private readonly TextBox cpu, memory, disk, hysteresis, duration, recovery;
    public RulesEditor(AlertRules rules)
    {
        offline = Form.Check("OfflineAlerts", rules.Offline); resource = Form.Check("ResourceAlerts", rules.Resources); expiry = Form.Check("ExpiryAlerts", rules.Expiry);
        cpuEnabled = Form.Check("CpuAlerts", rules.Cpu); memoryEnabled = Form.Check("MemoryAlerts", rules.Memory); diskEnabled = Form.Check("DiskAlerts", rules.Disk);
        cpu = Form.Field("CpuThreshold", rules.CpuHigh.ToString(CultureInfo.InvariantCulture)); memory = Form.Field("MemoryThreshold", rules.MemoryHigh.ToString(CultureInfo.InvariantCulture)); disk = Form.Field("DiskThreshold", rules.DiskHigh.ToString(CultureInfo.InvariantCulture));
        hysteresis = Form.Field("Hysteresis", rules.Hysteresis.ToString(CultureInfo.InvariantCulture)); duration = Form.Field("HighDuration", rules.HighSeconds.ToString(CultureInfo.InvariantCulture)); recovery = Form.Field("RecoveryDuration", rules.RecoverySeconds.ToString(CultureInfo.InvariantCulture));
        Form.Hint("RulesHint");
    }
    public AlertRules Read() => new() { Offline = offline.IsChecked == true, Resources = resource.IsChecked == true, Cpu = cpuEnabled.IsChecked == true, Memory = memoryEnabled.IsChecked == true, Disk = diskEnabled.IsChecked == true, Expiry = expiry.IsChecked == true, CpuHigh = FormPanel.Decimal(cpu), MemoryHigh = FormPanel.Decimal(memory), DiskHigh = FormPanel.Decimal(disk), Hysteresis = FormPanel.Decimal(hysteresis), HighSeconds = FormPanel.Number(duration), RecoverySeconds = FormPanel.Number(recovery) };
}
public sealed class ServerEditor : Window
{
    public ServerProfile? Result { get; private set; }
    public Credential? Secret { get; private set; }
    public bool DeleteRequested { get; private set; }
    private readonly ServerProfile source;
    private readonly AppConfiguration config;
    private readonly Credential? old;
    private readonly TextBox name, host, port, user, group, tags, interval, timeout, include, exclude;
    private readonly CheckBox enabled, customMonitoring, customAlerts;
    private readonly ComboBox auth, jump;
    private readonly PasswordBox password = new(), passphrase = new();
    private readonly DatePicker expires = new() { Margin = new(0, 4, 0, 12), MinHeight = 30 };
    private readonly RulesEditor rules;
    private readonly List<ServerProfile> jumps;
    private string? loadedKey;
    private readonly TextBlock keyName = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 12) };
    public ServerEditor(AppConfiguration configuration, ServerProfile? server, Credential? credential)
    {
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Ink"); FontFamily = new("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        config = configuration; source = server ?? new() { Order = config.Servers.Select(s => s.Order).DefaultIfEmpty(-1).Max() + 1 }; old = credential;
        Title = Text.Get(server is null ? "Add" : "EditServer"); Width = 650; Height = 750; MinWidth = 520; MinHeight = 530; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var tabs = new TabControl(); var root = new DockPanel { Margin = new(14) }; Content = root;
        var footer = new DockPanel { Margin = new(8, 12, 8, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        if (server is not null) { var del = new Button { Content = Text.Get("DeleteServer") }; del.Click += (_, _) => { if (MessageBox.Show(this, Text.Get("DeleteConfirm"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { DeleteRequested = true; DialogResult = true; } }; footer.Children.Add(del); }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        var cancel = new Button { Content = Text.Get("Cancel"), IsCancel = true }; actions.Children.Add(cancel);
        var save = new Button { Content = Text.Get("Save"), IsDefault = true, Style = (Style)FindResource("Primary") }; save.Click += Save; actions.Children.Add(save); root.Children.Add(tabs);
        var basic = new FormPanel(); name = basic.Field("ServerName", server?.Name ?? ""); host = basic.Field("Host", source.Host); port = basic.Field("Port", source.Port.ToString(CultureInfo.InvariantCulture)); user = basic.Field("Username", source.Username); group = basic.Field("Group", source.Group); tags = basic.Field("Tags", string.Join(", ", source.Tags)); enabled = basic.Check("Enabled", source.Enabled); basic.Label("ExpiresOn"); expires.SelectedDate = source.ExpiresOn?.ToDateTime(TimeOnly.MinValue); basic.Panel.Children.Add(expires); basic.Hint("ExpiryHint"); tabs.Items.Add(basic.Tab("Basic"));
        var connection = new FormPanel(); auth = connection.Choice("Authentication", [Text.Get("PasswordAuth"), Text.Get("PrivateKeyAuth")], (int)source.Authentication); connection.Label("Password"); connection.Panel.Children.Add(password); connection.Hint("KeepSecretHint");
        var load = new Button { Content = Text.Get("LoadPrivateKey"), HorizontalAlignment = HorizontalAlignment.Left }; load.Click += (_, _) => { var dialog = new OpenFileDialog { Title = Text.Get("LoadPrivateKey"), Filter = "SSH private key|*.*" }; if (dialog.ShowDialog(this) == true) { try { if (new FileInfo(dialog.FileName).Length > 262144) throw new IOException(); loadedKey = File.ReadAllText(dialog.FileName); keyName.Text = Path.GetFileName(dialog.FileName); } catch { MessageBox.Show(this, Text.Get("KeyReadError")); } } }; connection.Panel.Children.Add(load); keyName.Text = old?.PrivateKey is not null ? Text.Get("KeyStored") : Text.Get("NoKey"); connection.Panel.Children.Add(keyName); connection.Label("Passphrase"); connection.Panel.Children.Add(passphrase);
        jumps = config.Servers.Where(s => s.Id != source.Id && s.JumpServerId is null).ToList(); jump = connection.Choice("JumpHost", [Text.Get("NoJump"), .. jumps.Select(s => s.Name)], source.JumpServerId is null ? 0 : jumps.FindIndex(s => s.Id == source.JumpServerId) + 1); connection.Hint("JumpHint"); tabs.Items.Add(connection.Tab("Connection"));
        var monitoring = new FormPanel(); customMonitoring = monitoring.Check("OverrideMonitoring", source.IntervalSeconds is not null || source.TimeoutSeconds is not null || source.IncludeInterfaces is not null || source.ExcludeInterfaces is not null); interval = monitoring.Field("Interval", (source.IntervalSeconds ?? config.Monitoring.IntervalSeconds).ToString(CultureInfo.InvariantCulture)); timeout = monitoring.Field("Timeout", (source.TimeoutSeconds ?? config.Monitoring.TimeoutSeconds).ToString(CultureInfo.InvariantCulture)); include = monitoring.Field("IncludeInterfaces", string.Join(", ", source.IncludeInterfaces ?? config.Monitoring.IncludeInterfaces)); exclude = monitoring.Field("ExcludeInterfaces", string.Join(", ", source.ExcludeInterfaces ?? config.Monitoring.ExcludeInterfaces)); monitoring.Hint("InterfacesHint"); tabs.Items.Add(monitoring.Tab("Monitoring"));
        rules = new(source.Alerts ?? config.Alerts); customAlerts = new CheckBox { Content = Text.Get("OverrideAlerts"), IsChecked = source.Alerts is not null }; rules.Form.Panel.Children.Insert(0, customAlerts); tabs.Items.Add(rules.Form.Tab("Alerts"));
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            Result = source with { Name = name.Text.Trim(), Host = host.Text.Trim(), Port = FormPanel.Number(port), Username = user.Text.Trim(), Group = group.Text.Trim(), Tags = FormPanel.Patterns(tags), Enabled = enabled.IsChecked == true, ExpiresOn = expires.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null, Authentication = (AuthenticationKind)auth.SelectedIndex, JumpServerId = jump.SelectedIndex > 0 ? jumps[jump.SelectedIndex - 1].Id : null, IntervalSeconds = customMonitoring.IsChecked == true ? FormPanel.Number(interval) : null, TimeoutSeconds = customMonitoring.IsChecked == true ? FormPanel.Number(timeout) : null, IncludeInterfaces = customMonitoring.IsChecked == true ? FormPanel.Patterns(include) : null, ExcludeInterfaces = customMonitoring.IsChecked == true ? FormPanel.Patterns(exclude) : null, Alerts = customAlerts.IsChecked == true ? rules.Read() : null };
            var servers = config.Servers.Where(s => s.Id != source.Id).Append(Result).ToList(); (config with { Servers = servers }).Validate();
            if (password.Password.Length > 0 || passphrase.Password.Length > 0 || loadedKey is not null) Secret = new(password.Password.Length > 0 ? password.Password : old?.Password, loadedKey ?? old?.PrivateKey, passphrase.Password.Length > 0 ? passphrase.Password : old?.Passphrase);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ConfigurationException) { MessageBox.Show(this, Text.Get("ValidationError") + "\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
public sealed class SettingsEditor : Window
{
    public AppConfiguration? Result { get; private set; }
    public SettingsEditor(AppConfiguration config)
    {
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Ink"); FontFamily = new("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        Title = Text.Get("Settings"); Width = 650; Height = 750; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(14) }; Content = root; var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 12, 8, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(new Button { Content = Text.Get("Cancel"), IsCancel = true }); var save = new Button { Content = Text.Get("Save"), IsDefault = true, Style = (Style)FindResource("Primary") }; footer.Children.Add(save); var tabs = new TabControl(); root.Children.Add(tabs);
        var monitor = new FormPanel(); var interval = monitor.Field("Interval", config.Monitoring.IntervalSeconds.ToString(CultureInfo.InvariantCulture)); var timeout = monitor.Field("Timeout", config.Monitoring.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)); var concurrency = monitor.Field("Concurrency", config.Monitoring.MaxConcurrency.ToString(CultureInfo.InvariantCulture)); var retention = monitor.Field("Retention", config.Monitoring.RetentionDays.ToString(CultureInfo.InvariantCulture)); var include = monitor.Field("IncludeInterfaces", string.Join(", ", config.Monitoring.IncludeInterfaces)); var exclude = monitor.Field("ExcludeInterfaces", string.Join(", ", config.Monitoring.ExcludeInterfaces)); monitor.Hint("InterfacesHint"); monitor.Hint("PerformanceHint"); tabs.Items.Add(monitor.Tab("Monitoring"));
        var ui = new FormPanel(); var theme = ui.Choice("Theme", [Text.Get("ThemeSystem"), Text.Get("ThemeLight"), Text.Get("ThemeDark")], (int)config.Theme); var zone = ui.Field("TimeZone", config.TimeZoneId); ui.Hint("TimeZoneHint"); var auto = ui.Check("AutoStart", config.AutoStart); var tray = ui.Choice("TraySummary", [Text.Get("FleetSummary"), Text.Get("SelectedSummary")], config.TraySummary == "Selected" ? 1 : 0); var defaults = config.Servers.ToArray(); var defaultServer = ui.Choice("DefaultServer", [Text.Get("FirstServer"), .. defaults.Select(s => s.Name)], config.DefaultServerId is null ? 0 : Array.FindIndex(defaults, s => s.Id == config.DefaultServerId) + 1); ui.Hint("StorageHint"); tabs.Items.Add(ui.Tab("Interface"));
        var rules = new RulesEditor(config.Alerts); tabs.Items.Add(rules.Form.Tab("Alerts"));
        var metrics = new FormPanel(); var checks = new Dictionary<string, CheckBox>(); foreach (var key in new[] { "cpu", "memory", "disk", "network", "io", "swap", "load" }) checks[key] = metrics.Check("Metric" + key, config.VisibleMetrics.Contains(key)); tabs.Items.Add(metrics.Tab("Metrics"));
        save.Click += (_, _) =>
        {
            try
            {
                Result = config with { Monitoring = new() { IntervalSeconds = FormPanel.Number(interval), TimeoutSeconds = FormPanel.Number(timeout), MaxConcurrency = FormPanel.Number(concurrency), RetentionDays = FormPanel.Number(retention), IncludeInterfaces = FormPanel.Patterns(include), ExcludeInterfaces = FormPanel.Patterns(exclude) }, Alerts = rules.Read(), Theme = (ThemeKind)theme.SelectedIndex, TimeZoneId = zone.Text.Trim(), AutoStart = auto.IsChecked == true, TraySummary = tray.SelectedIndex == 1 ? "Selected" : "Fleet", DefaultServerId = defaultServer.SelectedIndex > 0 ? defaults[defaultServer.SelectedIndex - 1].Id : null, VisibleMetrics = checks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToArray() };
                Result.Validate(); DialogResult = true;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ConfigurationException) { MessageBox.Show(this, Text.Get("ValidationError") + "\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
    }
}
