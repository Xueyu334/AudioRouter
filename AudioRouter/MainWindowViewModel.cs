using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;

namespace AudioRouter;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AudioForwarder _audioForwarder = new();

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> _devices = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AudioDeviceInfo? _selectedListeningDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AudioDeviceInfo? _selectedOutputDevice;

    [ObservableProperty]
    private int _selectedLatency = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfigurationEnabled))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    private bool _isForwarding;

    public MainWindowViewModel()
    {
        _audioForwarder.ForwardingFailed += AudioForwarder_ForwardingFailed;
        RefreshDevices();
    }

    public IReadOnlyList<int> LatencyOptions { get; } = [50, 100, 150, 200];

    public bool IsConfigurationEnabled => !IsForwarding;

    public string StatusText => IsForwarding ? "正在转发" : "已停止";

    [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
    private void RefreshDevices()
    {
        var listeningDeviceId = SelectedListeningDevice?.Id;
        var outputDeviceId = SelectedOutputDevice?.Id;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? systemDefaultDeviceId = null;
            try
            {
                using var systemDefaultDevice = enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Multimedia);
                systemDefaultDeviceId = systemDefaultDevice.ID;
            }
            catch
            {
                // 没有默认播放设备时，继续使用可用设备列表中的第一项。
            }

            var refreshedDevices = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(device => new AudioDeviceInfo(device.ID, device.FriendlyName))
                .OrderBy(device => device.Name)
                .ToList();

            Devices = new ObservableCollection<AudioDeviceInfo>(refreshedDevices);
            SelectedListeningDevice =
                FindDevice(listeningDeviceId) ??
                FindDevice(systemDefaultDeviceId) ??
                Devices.FirstOrDefault();
            SelectedOutputDevice =
                FindDevice(outputDeviceId) ??
                Devices.FirstOrDefault();

            if (Devices.Count == 0)
            {
                ShowMessage("未检测到可用的播放设备。", MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            ShowMessage($"刷新音频设备失败：{exception.Message}", MessageBoxImage.Error);
        }
    }

    private bool CanRefreshDevices()
    {
        return !IsForwarding;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (SelectedListeningDevice == null || SelectedOutputDevice == null)
        {
            ShowMessage("请选择监听设备和输出设备。", MessageBoxImage.Information);
            return;
        }

        if (SelectedListeningDevice.Id == SelectedOutputDevice.Id)
        {
            ShowMessage("监听设备和输出设备不能相同。", MessageBoxImage.Information);
            return;
        }

        try
        {
            _audioForwarder.Start(
                SelectedListeningDevice.Id,
                SelectedOutputDevice.Id,
                SelectedLatency);
            IsForwarding = true;
        }
        catch (Exception exception)
        {
            IsForwarding = false;
            ShowMessage($"无法启动音频转发：{exception.Message}", MessageBoxImage.Error);
        }
    }

    private bool CanStart()
    {
        return !IsForwarding &&
               SelectedListeningDevice != null &&
               SelectedOutputDevice != null;
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _audioForwarder.Stop();
        IsForwarding = false;
    }

    private bool CanStop()
    {
        return IsForwarding;
    }

    public void Dispose()
    {
        _audioForwarder.ForwardingFailed -= AudioForwarder_ForwardingFailed;
        _audioForwarder.Dispose();
    }

    private void AudioForwarder_ForwardingFailed(object? sender, Exception exception)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Stop();
            ShowMessage($"音频转发已停止：{exception.Message}", MessageBoxImage.Error);
        });
    }

    private AudioDeviceInfo? FindDevice(string? deviceId)
    {
        return Devices.FirstOrDefault(device => device.Id == deviceId);
    }

    private static void ShowMessage(string message, MessageBoxImage image)
    {
        MessageBox.Show(
            message,
            "音频转发器",
            MessageBoxButton.OK,
            image);
    }
}
