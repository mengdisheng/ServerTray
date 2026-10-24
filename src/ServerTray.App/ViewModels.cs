using System.Collections.ObjectModel;
using System.Windows.Media;
using ServerTray.Core;

namespace ServerTray.App;

public sealed class ServerViewModel(ServerState state) : Observable
{
    public ServerState State { get; private set; } = state;
    public string Id => State.Profile.Id;
    public string Name => State.Profile.Name;
    public string Subtitle => string.IsNullOrEmpty(State.Profile.Group) ? State.Profile.Host : State.Profile.Group + " · " + State.Profile.Host;
    public string Status => Text.Status(State.Status) + (State.IsOffline && State.Status != ConnectionStatus.Offline ? " · " + Text.Status(ConnectionStatus.Offline) : "");
    public Brush StatusBrush => State.Status switch { ConnectionStatus.Online => new SolidColorBrush(Color.FromRgb(32, 157, 112)), ConnectionStatus.Pending or ConnectionStatus.Disabled or ConnectionStatus.Paused => Brushes.SlateGray, ConnectionStatus.HostKeyRequired or ConnectionStatus.Connecting => Brushes.DarkGoldenrod, _ => Brushes.IndianRed };
    public string Cpu => Display.Percent(State.Sample?.Cpu);
    public string Memory => Display.Percent(State.Sample?.Memory);
    public string Network => State.Status == ConnectionStatus.Online ? "↓ " + Display.Rate(State.Sample?.Rx) + "   ↑ " + Display.Rate(State.Sample?.Tx) : "—";
    public void Update(ServerState s) { State = s; foreach (var key in new[] { nameof(Name), nameof(Subtitle), nameof(Status), nameof(StatusBrush), nameof(Cpu), nameof(Memory), nameof(Network) }) Changed(key); }
}
public sealed class MainViewModel : Observable
{
    public ObservableCollection<ServerViewModel> Servers { get; } = [];
    public ObservableCollection<string> AlertLines { get; } = [];
    public ServerViewModel? Selected { get; set; }
    public string Online => $"{Servers.Count(s => s.State.Status == ConnectionStatus.Online)} / {Servers.Count(s => s.State.Profile.Enabled)}";
    public string Network
    {
        get
        {
            var values = Servers.Where(s => s.State.Status == ConnectionStatus.Online).SelectMany(s => new[] { s.State.Sample?.Rx, s.State.Sample?.Tx }).Where(v => v.HasValue).ToArray();
            return Display.Rate(values.Length == 0 ? null : values.Sum(v => v!.Value));
        }
    }
    public string Attention => Servers.Count(s => s.State.Profile.Enabled && s.State.Status is not (ConnectionStatus.Online or ConnectionStatus.Pending or ConnectionStatus.Connecting)).ToString();
    public void Reload(IEnumerable<ServerState> states) { Servers.Clear(); foreach (var state in states.OrderBy(s => s.Profile.Order)) Servers.Add(new(state)); Changed(nameof(Online)); Changed(nameof(Network)); Changed(nameof(Attention)); }
    public void Update(ServerState state) { Servers.FirstOrDefault(s => s.Id == state.Profile.Id)?.Update(state); Changed(nameof(Online)); Changed(nameof(Network)); Changed(nameof(Attention)); }
}
