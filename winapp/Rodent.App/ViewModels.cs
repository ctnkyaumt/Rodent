using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Rodent.Core.Devices;
using Rodent.Core.Model;

namespace Rodent.App;

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (Equals(field, value)) return false;
        field = value; Raise(n); return true;
    }
}

public sealed class DeviceViewModel : NotifyBase
{
    public LogiDevice Device { get; }
    public string Name => Device.Name;
    public string Kind => Device.Kind;
    public string KindLine => Device.Firmware is { Length: > 0 } fw ? $"{Device.Kind}  ·  fw {fw}" : Device.Kind;

    /// <summary>True for models Rodent hasn't been verified on (everything but the G402).</summary>
    public bool Untested => Device.Support != Rodent.Core.Devices.DeviceSupport.Verified;
    public ushort ProductId => Device.ProductId;
    public ushort VendorId => Device.VendorId;
    public ObservableCollection<SettingViewModel> Settings { get; } = new();
    public IReadOnlyList<InfoItem> Info => Device.Info;

    public DeviceViewModel(LogiDevice device)
    {
        Device = device;
        foreach (var s in device.Settings)
            Settings.Add(SettingViewModel.Create(s));
    }
}

/// <summary>Base VM; concrete subclass per setting kind drives the DataTemplate selection.</summary>
public abstract class SettingViewModel : NotifyBase
{
    protected readonly Setting Model;
    public string Name => Model.Name;
    public string Label => Model.Label;
    public string Description => Model.Description;

    protected SettingViewModel(Setting m) => Model = m;

    public static SettingViewModel Create(Setting s) => s switch
    {
        ChoiceSetting c => new ChoiceSettingViewModel(c),
        RangeSetting r => new RangeSettingViewModel(r),
        ToggleSetting t => new ToggleSettingViewModel(t),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    // Run device I/O off the UI thread so writes never freeze the window.
    protected static void OffThread(Action work) =>
        System.Threading.Tasks.Task.Run(() => { try { work(); } catch { /* device gone */ } });
}

public sealed class ChoiceSettingViewModel : SettingViewModel
{
    private readonly ChoiceSetting _s;
    public IReadOnlyList<Choice> Choices => _s.Choices;

    private Choice _selected;
    public Choice Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value) && !_loading)
                OffThread(() =>
                {
                    _s.Write(value.Value);
                    // Read back: if the device didn't take the value (unplugged,
                    // G HUB fighting us, onboard mode), the UI reverts to the truth.
                    SyncFromDevice();
                });
        }
    }

    private bool _loading;

    public ChoiceSettingViewModel(ChoiceSetting s) : base(s)
    {
        _s = s;
        int? cur = null;
        try { cur = _s.Read(); } catch { }
        SetFrom(cur);
    }

    private void SyncFromDevice()
    {
        int? cur = null;
        try { cur = _s.Read(); } catch { }
        Application.Current?.Dispatcher.Invoke(() => SetFrom(cur));
    }

    private void SetFrom(int? cur)
    {
        _loading = true;
        var match = _s.Choices.FirstOrDefault(c => c.Value == cur);
        _selected = match.Label != null ? match : (_s.Choices.Count > 0 ? _s.Choices[0] : default);
        Raise(nameof(Selected));
        _loading = false;
    }
}

public sealed class RangeSettingViewModel : SettingViewModel
{
    private readonly RangeSetting _s;
    public int Min => _s.Min;
    public int Max => _s.Max;
    public int Step => _s.Step;

    private int _value;
    public int Value
    {
        get => _value;
        set { if (Set(ref _value, value)) OffThread(() => _s.Write(value)); }
    }

    public RangeSettingViewModel(RangeSetting s) : base(s)
    {
        _s = s;
        _value = s.Read() ?? s.Min;
    }
}

public sealed class ToggleSettingViewModel : SettingViewModel
{
    private readonly ToggleSetting _s;
    private bool _loading;

    private bool _isOn;
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (Set(ref _isOn, value) && !_loading)
                OffThread(() =>
                {
                    _s.Write(value);
                    bool? cur = null;
                    try { cur = _s.Read(); } catch { }
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        _loading = true;
                        _isOn = cur ?? value;
                        Raise(nameof(IsOn));
                        _loading = false;
                    });
                });
        }
    }

    public ToggleSettingViewModel(ToggleSetting s) : base(s)
    {
        _s = s;
        _isOn = s.Read() ?? false;
    }
}
