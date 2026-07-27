using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;

namespace AudioRouter;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AudioForwarder _audioForwarder = new();
    private readonly DispatcherTimer _diagnosticsTimer;
    private long _forwardingStartedTimestamp;
    private long _lastDiagnosticsTimestamp;
    private long _lastCaptureCallbackCount;
    private long _lastCapturedBytes;

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> _devices = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AudioDeviceInfo? _selectedListeningDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AudioDeviceInfo? _selectedOutputDevice;

    [ObservableProperty]
    private int _selectedLatency = 50;

    [ObservableProperty]
    private string _estimatedLatencyText = "--";

    [ObservableProperty]
    private string _bufferStatusText = "--";

    [ObservableProperty]
    private string _captureRateText = "--";

    [ObservableProperty]
    private string _callbackRateText = "--";

    [ObservableProperty]
    private string _forwardingDurationText = "00:00:00";

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
        _diagnosticsTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            DiagnosticsTimer_Tick,
            Application.Current.Dispatcher);
        _diagnosticsTimer.Stop();
        RefreshDevices();
    }

    public IReadOnlyList<int> LatencyOptions { get; } = [20, 50, 100, 150, 200];

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
            StartDiagnostics();
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
        _diagnosticsTimer.Stop();
        _audioForwarder.Stop();
        IsForwarding = false;
        ResetDiagnosticsDisplay();
    }

    private bool CanStop()
    {
        return IsForwarding;
    }

    public void Dispose()
    {
        _diagnosticsTimer.Stop();
        _diagnosticsTimer.Tick -= DiagnosticsTimer_Tick;
        _audioForwarder.ForwardingFailed -= AudioForwarder_ForwardingFailed;
        _audioForwarder.Dispose();
    }

    private void StartDiagnostics()
    {
        var now = Stopwatch.GetTimestamp();
        _forwardingStartedTimestamp = now;
        _lastDiagnosticsTimestamp = now;
        _lastCaptureCallbackCount = 0;
        _lastCapturedBytes = 0;
        UpdateDiagnostics(now);
        _diagnosticsTimer.Start();
    }

    private void DiagnosticsTimer_Tick(object? sender, EventArgs e)
    {
        UpdateDiagnostics(Stopwatch.GetTimestamp());
    }

    private void UpdateDiagnostics(long now)
    {
        var diagnostics = _audioForwarder.GetDiagnostics();
        var elapsedSeconds =
            (now - _lastDiagnosticsTimestamp) /
            (double)Stopwatch.Frequency;

        EstimatedLatencyText =
            $"{diagnostics.EstimatedLatency.TotalMilliseconds:F0} ms";
        BufferStatusText =
            $"{diagnostics.BufferedDuration.TotalMilliseconds:F0} / " +
            $"{diagnostics.PeakBufferedDuration.TotalMilliseconds:F0} ms";

        if (elapsedSeconds > 0)
        {
            var capturedBytes =
                diagnostics.CapturedBytes - _lastCapturedBytes;
            var captureCallbacks =
                diagnostics.CaptureCallbackCount -
                _lastCaptureCallbackCount;
            CaptureRateText =
                $"{capturedBytes / elapsedSeconds / 1024:F0} KB/s";
            CallbackRateText =
                $"{captureCallbacks / elapsedSeconds:F1} 次/s";
        }

        var runningDuration = TimeSpan.FromSeconds(
            (now - _forwardingStartedTimestamp) /
            (double)Stopwatch.Frequency);
        ForwardingDurationText =
            $"{(int)runningDuration.TotalHours:00}:" +
            $"{runningDuration.Minutes:00}:" +
            $"{runningDuration.Seconds:00}";

        _lastDiagnosticsTimestamp = now;
        _lastCapturedBytes = diagnostics.CapturedBytes;
        _lastCaptureCallbackCount = diagnostics.CaptureCallbackCount;
    }

    private void ResetDiagnosticsDisplay()
    {
        EstimatedLatencyText = "--";
        BufferStatusText = "--";
        CaptureRateText = "--";
        CallbackRateText = "--";
        ForwardingDurationText = "00:00:00";
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
