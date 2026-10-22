using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ControllerAudioSwitcher.Audio;
using ControllerAudioSwitcher.Controller;
using ControllerAudioSwitcher.Core.Codec;
using ControllerAudioSwitcher.Core.Interfaces;
using ControllerAudioSwitcher.Core.Models;
using ControllerAudioSwitcher.Core.Services;
using ControllerAudioSwitcher.Transport;
using Forms = System.Windows.Forms;
using Color = System.Windows.Media.Color;
using LogEntry = ControllerAudioSwitcher.Core.Interfaces.LogEntry;

namespace ControllerAudioSwitcher.App;

public partial class MainWindow : Window
{
    private readonly IDiagnosticService _diagnosticService;
    private readonly ISettingsService _settingsService;
    private readonly IControllerDiscoveryService _discoveryService;
    private readonly WasapiAudioCaptureService _captureService;
    private readonly ISbcAudioEncoder _encoder;
    private readonly IBluetoothAudioTransport _transport;
    private readonly IAudioStreamingController _streamingController;

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _telemetryTimer;
    private bool _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();

        _diagnosticService = new DiagnosticService();
        _settingsService = new SettingsService(_diagnosticService);
        _discoveryService = new ControllerDiscoveryService(_diagnosticService);
        _captureService = new WasapiAudioCaptureService();
        _encoder = new DualShock4SbcEncoder(32000);
        _transport = new DualShock4HidTransport();

        _streamingController = new AudioStreamingController(
            _discoveryService,
            _captureService,
            _encoder,
            _transport,
            _settingsService,
            _diagnosticService);

        // Subscribe to events
        _diagnosticService.LogReceived += OnLogMessageReceived;
        _streamingController.StatusChanged += (s, status) => Dispatcher.Invoke(() => UpdateUiFromStatus(status));
        _discoveryService.ControllerConnected += (s, c) => Dispatcher.Invoke(() => UpdateControllerDetails(c));
        _discoveryService.ControllerDisconnected += (s, c) => Dispatcher.Invoke(() => UpdateControllerDetails(null));

        // Setup System Tray
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "DualShock 4 Bluetooth Audio Streamer",
            Visible = true
        };
        _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();

        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add("Open Dashboard", null, (s, e) => RestoreFromTray());
        contextMenu.Items.Add("Start Streaming", null, async (s, e) => await _streamingController.StartStreamingAsync());
        contextMenu.Items.Add("Stop Streaming", null, async (s, e) => await _streamingController.StopStreamingAsync());
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());
        _notifyIcon.ContextMenuStrip = contextMenu;

        // Telemetry update timer
        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _telemetryTimer.Tick += OnTelemetryTick;
        _telemetryTimer.Start();

        // Initialize UI values from settings
        LoadSettingsIntoUi();
        RefreshAudioDevices();

        _diagnosticService.Info("DualShock 4 Audio Streamer started. Initialized Windows WASAPI capture and HID transport.");

        // Start discovery for connected controllers
        _discoveryService.StartDiscovery();

        var initialController = _discoveryService.GetConnectedControllers().FirstOrDefault();
        UpdateControllerDetails(initialController);
    }

    private void LoadSettingsIntoUi()
    {
        var settings = _settingsService.CurrentSettings;
        SliderVolume.Value = settings.HeadphoneVolume;
        ChkMuteSpeakers.IsChecked = settings.MuteSpeakersWhileStreaming;
        ChkAutoStart.IsChecked = settings.AutoStartOnConnect;
        ChkMinimizeToTray.IsChecked = settings.MinimizeToTray;
        UpdateVolumeLabel(settings.HeadphoneVolume);
        UpdateSpeakerMuteButton(_captureService.IsEndpointMuted);
    }

    private void RefreshAudioDevices()
    {
        try
        {
            var devices = _captureService.EnumerateRenderDevices();
            CmbAudioDevices.Items.Clear();

            int selectedIndex = 0;
            string? preferredId = _settingsService.CurrentSettings.SelectedAudioDeviceId;

            for (int i = 0; i < devices.Count; i++)
            {
                var dev = devices[i];
                var item = new ComboBoxItem
                {
                    Content = dev.IsDefault ? $"{dev.Name} (Default)" : dev.Name,
                    Tag = dev.Id
                };
                CmbAudioDevices.Items.Add(item);

                if (!string.IsNullOrEmpty(preferredId) && dev.Id == preferredId)
                {
                    selectedIndex = i;
                }
                else if (string.IsNullOrEmpty(preferredId) && dev.IsDefault)
                {
                    selectedIndex = i;
                }
            }

            if (CmbAudioDevices.Items.Count > 0)
            {
                CmbAudioDevices.SelectedIndex = selectedIndex;
            }
        }
        catch (Exception ex)
        {
            _diagnosticService.Warn($"Error refreshing audio devices: {ex.Message}");
        }
    }

    private void UpdateControllerDetails(ControllerInfo? controller)
    {
        if (controller == null)
        {
            TxtControllerName.Text = "No DualShock 4 connected";
            TxtControllerName.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            TxtConnectionType.Text = "Disconnected";
            TxtInterfaceHint.Text = "";
            TxtAudioCapability.Text = "Not Detected";
            TxtAudioCapability.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            TxtDevicePath.Text = "-";
            BtnStartStreaming.IsEnabled = false;
            UpdateStatusBadge("Disconnected", Color.FromRgb(39, 39, 42), Color.FromRgb(113, 113, 122));
            return;
        }

        TxtControllerName.Text = controller.DisplayName;
        TxtDevicePath.Text = controller.DevicePath;

        if (controller.ConnectionType == ControllerConnectionType.Bluetooth)
        {
            TxtConnectionType.Text = "Bluetooth (Wireless)";
            TxtConnectionType.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            TxtInterfaceHint.Text = "✓ Ready for 0x17 Audio HID streaming";

            if (controller.IsCompatible)
            {
                TxtAudioCapability.Text = "Compatible (3.5 mm Jack Supported)";
                TxtAudioCapability.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                BtnStartStreaming.IsEnabled = !_streamingController.IsStreaming;
                UpdateStatusBadge("Bluetooth Ready", Color.FromRgb(29, 78, 216), Color.FromRgb(96, 165, 250));
            }
            else
            {
                TxtAudioCapability.Text = "Unsupported configuration";
                TxtAudioCapability.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                BtnStartStreaming.IsEnabled = false;
                UpdateStatusBadge("Unsupported", Color.FromRgb(180, 83, 9), Color.FromRgb(251, 191, 36));
            }
        }
        else if (controller.ConnectionType == ControllerConnectionType.Usb)
        {
            TxtConnectionType.Text = "USB (Wired)";
            TxtConnectionType.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtInterfaceHint.Text = "⚠️ Connect via Bluetooth for wireless audio";
            TxtAudioCapability.Text = "USB Connected (Wireless transport inactive)";
            TxtAudioCapability.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            BtnStartStreaming.IsEnabled = false;
            UpdateStatusBadge("USB Connected", Color.FromRgb(120, 53, 15), Color.FromRgb(245, 158, 11));
        }
    }

    private void UpdateUiFromStatus(StreamingStatus status)
    {
        TxtStatusMessage.Text = status.Message;

        switch (status.State)
        {
            case StreamingState.Streaming:
                BtnStartStreaming.IsEnabled = false;
                BtnStopStreaming.IsEnabled = true;
                UpdateStatusBadge("Streaming Active", Color.FromRgb(6, 78, 59), Color.FromRgb(16, 185, 129));
                break;

            case StreamingState.Starting:
                BtnStartStreaming.IsEnabled = false;
                BtnStopStreaming.IsEnabled = false;
                UpdateStatusBadge("Starting...", Color.FromRgb(30, 58, 138), Color.FromRgb(59, 130, 246));
                break;

            case StreamingState.Failed:
                BtnStartStreaming.IsEnabled = status.ActiveController?.IsCompatible == true;
                BtnStopStreaming.IsEnabled = false;
                UpdateStatusBadge("Failed", Color.FromRgb(127, 29, 29), Color.FromRgb(239, 68, 68));
                break;

            case StreamingState.ConnectedBluetoothReady:
                BtnStartStreaming.IsEnabled = true;
                BtnStopStreaming.IsEnabled = false;
                UpdateStatusBadge("Bluetooth Ready", Color.FromRgb(29, 78, 216), Color.FromRgb(96, 165, 250));
                break;

            case StreamingState.ConnectedUsbOnly:
                BtnStartStreaming.IsEnabled = false;
                BtnStopStreaming.IsEnabled = false;
                UpdateStatusBadge("USB Connected", Color.FromRgb(120, 53, 15), Color.FromRgb(245, 158, 11));
                break;

            case StreamingState.Disconnected:
            default:
                BtnStartStreaming.IsEnabled = false;
                BtnStopStreaming.IsEnabled = false;
                UpdateStatusBadge("Disconnected", Color.FromRgb(39, 39, 42), Color.FromRgb(113, 113, 122));
                break;
        }
    }

    private void UpdateStatusBadge(string label, Color bg, Color dot)
    {
        StatusBadgeText.Text = label;
        StatusBadgeBorder.Background = new SolidColorBrush(bg);
        StatusIndicatorDot.Fill = new SolidColorBrush(dot);
    }

    private void OnTelemetryTick(object? sender, EventArgs e)
    {
        var status = _streamingController.Status;
        if (status.State == StreamingState.Streaming)
        {
            long kbSent = status.BytesSent / 1024;
            TxtPacketsSent.Text = $"{status.PacketsSent:N0} packets ({kbSent:N0} KB)";
            TxtEstimatedLatency.Text = $"{status.EstimatedLatencyMs:F0} ms (Buffer: {status.BufferLevelFrames} frames)";
            PbBufferLevel.Value = Math.Min(96, status.BufferLevelFrames);
            TxtUnderruns.Text = $"{status.UnderrunCount:N0}";
        }
        else
        {
            TxtEstimatedLatency.Text = "Idle";
            PbBufferLevel.Value = 0;
        }

        UpdateSpeakerMuteButton(_captureService.IsEndpointMuted);
    }

    private void OnToggleSpeakerMuteClicked(object sender, RoutedEventArgs e)
    {
        bool currentMute = _captureService.IsEndpointMuted;
        bool newMute = !currentMute;
        _captureService.IsEndpointMuted = newMute;
        UpdateSpeakerMuteButton(newMute);
        _diagnosticService.Info(newMute 
            ? "PC speakers muted (headphone isolation active)." 
            : "PC speakers unmuted.");
    }

    private void UpdateSpeakerMuteButton(bool isMuted)
    {
        if (isMuted)
        {
            BtnToggleSpeakerMute.Content = "🔊 Unmute PC Speakers";
            BtnToggleSpeakerMute.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
        }
        else
        {
            BtnToggleSpeakerMute.Content = "🔇 Mute PC Speakers";
            BtnToggleSpeakerMute.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Slate
        }
    }

    private void OnOpenSoundSettingsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
            _diagnosticService.Info("Opened Windows Sound Settings.");
        }
        catch (Exception ex)
        {
            _diagnosticService.Warn($"Failed to launch Windows Sound Settings: {ex.Message}");
        }
    }

    private async void OnStartStreamingClicked(object sender, RoutedEventArgs e)
    {
        _diagnosticService.Info("User requested Start Streaming.");
        bool started = await _streamingController.StartStreamingAsync();
        if (!started)
        {
            _diagnosticService.Warn("StartStreamingAsync returned false.");
        }
    }

    private async void OnStopStreamingClicked(object sender, RoutedEventArgs e)
    {
        _diagnosticService.Info("User requested Stop Streaming.");
        await _streamingController.StopStreamingAsync();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        byte volume = (byte)e.NewValue;
        UpdateVolumeLabel(volume);
        _streamingController?.SetVolume(volume);
    }

    private void UpdateVolumeLabel(byte volume)
    {
        int percent = (int)Math.Round((volume / 127.0) * 100);
        TxtVolumeValue.Text = $"{percent}% ({volume}/127)";
    }

    private void OnAudioDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbAudioDevices.SelectedItem is ComboBoxItem item && item.Tag is string deviceId)
        {
            _settingsService.CurrentSettings.SelectedAudioDeviceId = deviceId;
            _settingsService.Save();
            _diagnosticService.Info($"Selected audio playback device: {item.Content}");
        }
    }

    private void OnRefreshDevicesClicked(object sender, RoutedEventArgs e)
    {
        RefreshAudioDevices();
    }

    private void OnSettingsOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_settingsService == null) return;
        _settingsService.CurrentSettings.MuteSpeakersWhileStreaming = ChkMuteSpeakers.IsChecked == true;
        _settingsService.CurrentSettings.AutoStartOnConnect = ChkAutoStart.IsChecked == true;
        _settingsService.CurrentSettings.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
        _settingsService.Save();
    }

    private void OnDiagnosticCaptureModeChanged(object sender, RoutedEventArgs e)
    {
        bool captureOnly = ChkDiagnosticCaptureOnly.IsChecked == true;
        if (captureOnly)
        {
            _diagnosticService.Info("Diagnostic mode enabled: WASAPI Loopback will run in test mode.");
            try
            {
                _captureService.StartCapture(_settingsService.CurrentSettings.SelectedAudioDeviceId);
                _diagnosticService.Info("Diagnostic WASAPI capture active and receiving audio samples.");
            }
            catch (Exception ex)
            {
                _diagnosticService.Error("Diagnostic WASAPI capture test failed.", ex);
            }
        }
        else
        {
            _diagnosticService.Info("Diagnostic capture mode stopped.");
            _captureService.StopCapture();
        }
    }

    private void OnLogMessageReceived(object? sender, LogEntry msg)
    {
        Dispatcher.Invoke(() =>
        {
            string line = $"[{msg.Timestamp:HH:mm:ss}] [{msg.Level.ToString().ToUpperInvariant()}] {msg.Message}{Environment.NewLine}";
            TxtLogConsole.AppendText(line);
            if (TxtLogConsole.Text.Length > 20000)
            {
                TxtLogConsole.Text = TxtLogConsole.Text[^10000..];
            }
            TxtLogConsole.ScrollToEnd();
        });
    }

    private void OnCopyLogsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(TxtLogConsole.Text);
            _diagnosticService.Info("Diagnostic logs copied to clipboard.");
        }
        catch (Exception ex)
        {
            _diagnosticService.Warn($"Failed to copy logs: {ex.Message}");
        }
    }

    private void OnClearLogsClicked(object sender, RoutedEventArgs e)
    {
        TxtLogConsole.Clear();
        _diagnosticService.Clear();
    }

    private void OnMinimizeToTrayClicked(object sender, RoutedEventArgs e)
    {
        Hide();
        _notifyIcon.ShowBalloonTip(1500, "DualShock 4 Audio Streamer", 
            "Minimized to system tray. Streaming will continue in the background.", 
            Forms.ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit && _settingsService.CurrentSettings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _telemetryTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _streamingController.Dispose();
        _discoveryService.Dispose();
        _captureService.StopCapture();
        _transport.Dispose();

        base.OnClosed(e);
    }
}