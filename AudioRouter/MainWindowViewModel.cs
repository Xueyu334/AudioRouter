using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioRouter;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AudioForwarder _audioForwarder = new();
    private readonly AudioDeviceService _audioDeviceService = new();
    private readonly AudioConfigService _audioConfigService = new();
    private readonly DispatcherTimer _diagnosticsTimer;
    private readonly DispatcherTimer _recoveryTimer;
    private long _forwardingStartedTimestamp;
    private long _lastDiagnosticsTimestamp;
    private long _lastCaptureCallbackCount;
    private long _lastCapturedBytes;
    private string? _recoverySourceDeviceId;
    private string? _recoveryOutputDeviceId;
    private int _recoveryLatencyMilliseconds;
    private string? _configuredSourceDeviceId;
    private string? _configuredOutputDeviceId;

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
    private string _targetLatencyText = "--";

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfigurationEnabled))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    private bool _isRecovering;

    public MainWindowViewModel()
    {
        var config = _audioConfigService.Load();
        _configuredSourceDeviceId = config.SourceDeviceId;
        _configuredOutputDeviceId = config.OutputDeviceId;
        if (LatencyOptions.Contains(config.LatencyMilliseconds))
        {
            _selectedLatency = config.LatencyMilliseconds;
        }

        _audioForwarder.ForwardingFailed += AudioForwarder_ForwardingFailed;
        _diagnosticsTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            DiagnosticsTimer_Tick,
            Application.Current.Dispatcher);
        _diagnosticsTimer.Stop();
        _recoveryTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            RecoveryTimer_Tick,
            Application.Current.Dispatcher);
        _recoveryTimer.Stop();
        RefreshDevices();
    }

    public IReadOnlyList<int> LatencyOptions { get; } = [20, 50, 100, 150, 200];

    public bool IsConfigurationEnabled => !IsForwarding && !IsRecovering;

    public string StatusText => IsRecovering
        ? "等待设备恢复"
        : IsForwarding
            ? "正在转发"
            : "已停止";

    [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
    private void RefreshDevices()
    {
        var listeningDeviceId =
            SelectedListeningDevice?.Id ?? _configuredSourceDeviceId;
        var outputDeviceId =
            SelectedOutputDevice?.Id ?? _configuredOutputDeviceId;

        try
        {
            var systemDefaultDeviceId =
                _audioDeviceService.GetDefaultRenderDeviceId();
            var refreshedDevices =
                _audioDeviceService.GetActiveRenderDevices();

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
        return !IsForwarding && !IsRecovering;
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
            _recoverySourceDeviceId = SelectedListeningDevice.Id;
            _recoveryOutputDeviceId = SelectedOutputDevice.Id;
            _recoveryLatencyMilliseconds = SelectedLatency;
            _configuredSourceDeviceId = SelectedListeningDevice.Id;
            _configuredOutputDeviceId = SelectedOutputDevice.Id;
            _audioConfigService.Save(new AudioRouterConfig(
                _configuredSourceDeviceId,
                _configuredOutputDeviceId,
                SelectedLatency));
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
               !IsRecovering &&
               SelectedListeningDevice != null &&
               SelectedOutputDevice != null;
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        CancelRecovery();
        _diagnosticsTimer.Stop();
        _audioForwarder.Stop();
        IsForwarding = false;
        ResetDiagnosticsDisplay();
    }

    private bool CanStop()
    {
        return IsForwarding || IsRecovering;
    }

    public void Dispose()
    {
        _diagnosticsTimer.Stop();
        _diagnosticsTimer.Tick -= DiagnosticsTimer_Tick;
        _recoveryTimer.Stop();
        _recoveryTimer.Tick -= RecoveryTimer_Tick;
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
            $"{diagnostics.EstimatedLatency.TotalMilliseconds:F0} ms · " +
            $"{(diagnostics.IsExclusiveOutput ? "独占" : "共享")}";
        TargetLatencyText =
            $"{diagnostics.TargetLatency.TotalMilliseconds:F0} ms";
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
        TargetLatencyText = "--";
        BufferStatusText = "--";
        CaptureRateText = "--";
        CallbackRateText = "--";
        ForwardingDurationText = "00:00:00";
    }

    private void AudioForwarder_ForwardingFailed(object? sender, Exception exception)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _diagnosticsTimer.Stop();
            IsForwarding = false;
            ResetDiagnosticsDisplay();
            BeginRecovery();
        });
    }

    private void BeginRecovery()
    {
        if (_recoverySourceDeviceId == null ||
            _recoveryOutputDeviceId == null)
        {
            return;
        }

        IsRecovering = true;
        _recoveryTimer.Start();
    }

    private void RecoveryTimer_Tick(object? sender, EventArgs e)
    {
        if (_recoverySourceDeviceId == null ||
            _recoveryOutputDeviceId == null)
        {
            CancelRecovery();
            return;
        }

        try
        {
            if (!_audioDeviceService.AreActive(
                    _recoverySourceDeviceId,
                    _recoveryOutputDeviceId))
            {
                return;
            }

            _audioForwarder.Start(
                _recoverySourceDeviceId,
                _recoveryOutputDeviceId,
                _recoveryLatencyMilliseconds);
            _recoveryTimer.Stop();
            IsRecovering = false;
            IsForwarding = true;
            StartDiagnostics();
        }
        catch
        {
            // 设备可能刚重新出现但尚未完成初始化，下个周期继续重试。
        }
    }

    private void CancelRecovery()
    {
        _recoveryTimer.Stop();
        IsRecovering = false;
        _recoverySourceDeviceId = null;
        _recoveryOutputDeviceId = null;
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
