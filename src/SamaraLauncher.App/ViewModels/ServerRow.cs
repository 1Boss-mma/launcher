using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SamaraLauncher.App.Controls;
using SamaraLauncher.Core.Servers;

namespace SamaraLauncher.App.ViewModels;

/// <summary>Строка списка серверов с состоянием ping. Обновляется из Dispatcher.</summary>
public sealed class ServerRow : INotifyPropertyChanged
{
    private static readonly Brush OnlineBrush = PixelPalette.GreenHi;
    private static readonly Brush OfflineBrush = PixelPalette.Red;
    private static readonly Brush PendingBrush = PixelPalette.Muted;
    private static readonly Brush SelectedFrame = PixelPalette.Green;
    private static readonly Brush NormalFrame = PixelPalette.Line;

    private bool _isSelected;
    private string _motd = "—";
    private string _versionText = "—";
    private string _playersText = "игроки —";
    private string _pingText = "—";
    private Brush _statusBrush = PendingBrush;
    private Brush _pingBrush = PendingBrush;
    private bool _isOnline;
    private string _tooltip;

    public ServerRow(ServerEntry entry)
    {
        Entry = entry;
        _tooltip = $"{Name}\n{Address}";
    }

    public ServerEntry Entry { get; }

    public string Name => string.IsNullOrWhiteSpace(Entry.Name) ? Entry.Address : Entry.Name;

    public string Address => Entry.Address;

    public string Motd
    {
        get => _motd;
        private set => SetField(ref _motd, value);
    }

    public string VersionText
    {
        get => _versionText;
        private set => SetField(ref _versionText, value);
    }

    public string PlayersText
    {
        get => _playersText;
        private set => SetField(ref _playersText, value);
    }

    public string PingText
    {
        get => _pingText;
        private set => SetField(ref _pingText, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => SetField(ref _statusBrush, value);
    }

    public Brush PingBrush
    {
        get => _pingBrush;
        private set => SetField(ref _pingBrush, value);
    }

    public string Tooltip
    {
        get => _tooltip;
        private set => SetField(ref _tooltip, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        private set => SetField(ref _isOnline, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FrameBrush));
            OnPropertyChanged(nameof(BarBrush));
        }
    }

    public Brush FrameBrush => _isSelected ? SelectedFrame : NormalFrame;

    public Brush BarBrush => _isSelected ? SelectedFrame : Brushes.Transparent;

    public string AutomationName => $"Сервер {Name}, {Address}";

    public string QuickJoinName => $"Играть на сервере {Name}, {Address}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void MarkPinging()
    {
        IsOnline = false;
        Motd = "—";
        VersionText = "—";
        PlayersText = "игроки —";
        PingText = "—";
        StatusBrush = PendingBrush;
        PingBrush = PendingBrush;
    }

    public void ApplyPing(ServerPingResult result)
    {
        if (result.Online)
        {
            IsOnline = true;
            Motd = string.IsNullOrWhiteSpace(result.Motd) ? "—" : result.Motd;
            VersionText = string.IsNullOrWhiteSpace(result.VersionName) ? "—" : result.VersionName;
            PlayersText = $"игроки {result.PlayersOnline}/{result.PlayersMax}";
            PingText = $"{result.LatencyMs} мс";
            StatusBrush = OnlineBrush;
            PingBrush = OnlineBrush;
            Tooltip = $"{Name}\n{Address}\nонлайн · {result.PlayersOnline}/{result.PlayersMax}\n{result.LatencyMs} мс";
            return;
        }

        Motd = "—";
        VersionText = "—";
        PlayersText = "игроки —";
        PingText = "офлайн";
        StatusBrush = OfflineBrush;
        PingBrush = OfflineBrush;
        IsOnline = false;
        Tooltip = string.IsNullOrWhiteSpace(result.Error)
            ? $"{Name}\n{Address}\nофлайн"
            : $"{Name}\n{Address}\nофлайн\n{result.Error}";
    }

    public void ApplyError(string message)
    {
        Motd = "—";
        VersionText = "—";
        PlayersText = "игроки —";
        PingText = "офлайн";
        StatusBrush = OfflineBrush;
        PingBrush = OfflineBrush;
        IsOnline = false;
        Tooltip = $"{Name}\n{Address}\nофлайн\n{message}";
    }

    private void SetField(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void SetField(ref Brush field, Brush value, [CallerMemberName] string? name = null)
    {
        if (ReferenceEquals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void SetField(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
