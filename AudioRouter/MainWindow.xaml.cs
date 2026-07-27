using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;

namespace AudioRouter;

public partial class MainWindow
{
    private readonly AudioForwarder _audioForwarder = new();

    public MainWindow()
    {
        InitializeComponent();
        _audioForwarder.ForwardingFailed += AudioForwarder_ForwardingFailed;
        RefreshAudioDevices();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAudioDevices();
    }

    private void ListeningDeviceComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        // 预留：监听设备选择发生变化时，可在这里更新配置。
    }

    private void OutputDeviceComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        // 预留：输出设备选择发生变化时，可在这里更新配置。
    }

    private void BufferDelayComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        // 预留：缓冲延迟发生变化时，可在这里更新配置。
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (ListeningDeviceComboBox.SelectedItem is not AudioDeviceInfo sourceDevice ||
            OutputDeviceComboBox.SelectedItem is not AudioDeviceInfo outputDevice)
        {
            ShowInformation("请选择监听设备和输出设备。");
            return;
        }

        if (sourceDevice.Id == outputDevice.Id)
        {
            ShowInformation("监听设备和输出设备不能相同。");
            return;
        }

        try
        {
            _audioForwarder.Start(
                sourceDevice.Id,
                outputDevice.Id,
                GetSelectedLatency());
            SetForwardingState(true);
        }
        catch (Exception exception)
        {
            SetForwardingState(false);
            MessageBox.Show(
                $"无法启动音频转发：{exception.Message}",
                "音频转发器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopForwarding();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _audioForwarder.ForwardingFailed -= AudioForwarder_ForwardingFailed;
        _audioForwarder.Dispose();
    }

    private void AudioForwarder_ForwardingFailed(object? sender, Exception exception)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StopForwarding();
            MessageBox.Show(
                $"音频转发已停止：{exception.Message}",
                "音频转发器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        });
    }

    private void RefreshAudioDevices()
    {
        var selectedSourceId =
            (ListeningDeviceComboBox.SelectedItem as AudioDeviceInfo)?.Id;
        var selectedOutputId =
            (OutputDeviceComboBox.SelectedItem as AudioDeviceInfo)?.Id;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(device => new AudioDeviceInfo(device.ID, device.FriendlyName))
                .OrderBy(device => device.Name)
                .ToList();

            ListeningDeviceComboBox.ItemsSource = devices;
            OutputDeviceComboBox.ItemsSource = devices;

            SelectDevice(ListeningDeviceComboBox, selectedSourceId, 0);
            SelectDevice(
                OutputDeviceComboBox,
                selectedOutputId,
                devices.Count > 1 ? 1 : 0);

            if (devices.Count == 0)
            {
                ShowInformation("未检测到可用的播放设备。");
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"刷新音频设备失败：{exception.Message}",
                "音频转发器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void StopForwarding()
    {
        _audioForwarder.Stop();
        SetForwardingState(false);
    }

    private void SetForwardingState(bool isForwarding)
    {
        ListeningDeviceComboBox.IsEnabled = !isForwarding;
        OutputDeviceComboBox.IsEnabled = !isForwarding;
        BufferDelayComboBox.IsEnabled = !isForwarding;
        RefreshButton.IsEnabled = !isForwarding;
        StartButton.IsEnabled = !isForwarding;
        StopButton.IsEnabled = isForwarding;

        StatusIndicator.Fill = isForwarding
            ? Brushes.MediumSeaGreen
            : Brushes.SlateGray;
        StatusText.Foreground = isForwarding
            ? Brushes.SeaGreen
            : Brushes.SlateGray;
        StatusText.Text = isForwarding ? "正在转发" : "已停止";
    }

    private int GetSelectedLatency()
    {
        if (BufferDelayComboBox.SelectedItem is ComboBoxItem item &&
            item.Content is string content &&
            int.TryParse(content.Replace(" ms", string.Empty), out var latency))
        {
            return latency;
        }

        return 100;
    }

    private static void SelectDevice(
        ComboBox comboBox,
        string? deviceId,
        int fallbackIndex)
    {
        comboBox.SelectedValue = deviceId;
        if (comboBox.SelectedIndex < 0 && comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = Math.Min(fallbackIndex, comboBox.Items.Count - 1);
        }
    }

    private static void ShowInformation(string message)
    {
        MessageBox.Show(
            message,
            "音频转发器",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
