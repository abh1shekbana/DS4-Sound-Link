using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

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
    private bool _isOperationPending;

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

        // Subscribe to events (asynchronous dispatch to prevent background-thread deadlocks)
        _diagnosticService.LogReceived += OnLogMessageReceived;
        _streamingController.StatusChanged += (s, status) => Dispatcher.InvokeAsync(() => UpdateUiFromStatus(status));
        _discoveryService.ControllerConnected += (s, c) => Dispatcher.InvokeAsync(() => UpdateControllerDetails(c));
        _discoveryService.ControllerDisconnected += (s, c) => Dispatcher.InvokeAsync(() => UpdateControllerDetails(null));

        // Setup System Tray
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "DS4-SoundLink",
            Visible = true
        };
        _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();

        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add("Open Dashboard", null, (s, e) => RestoreFromTray());
        contextMenu.Items.Add("Start Streaming", null, async (s, e) =>
        {
            try { await _streamingController.StartStreamingAsync(); }
            catch (Exception ex) { _diagnosticService.Error($"Tray start streaming error: {ex.Message}"); }
        });
        contextMenu.Items.Add("Stop Streaming", null, async (s, e) =>
        {
            try { await _streamingController.StopStreamingAsync(); }
            catch (Exception ex) { _diagnosticService.Error($"Tray stop streaming error: {ex.Message}"); }
        });
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());
        _notifyIcon.ContextMenuStrip = contextMenu;

        // Apply application icon if present
        ApplyApplicationIcon();

        // Telemetry update timer (for diagnostic & stats)
        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _telemetryTimer.Tick += OnTelemetryTick;
        _telemetryTimer.Start();

        // Initialize UI values from settings
        LoadSettingsIntoUi();
        RefreshAudioDevices();

        _diagnosticService.Info("DS4-SoundLink started. Initialized Windows WASAPI capture and HID transport.");

        // Start discovery for connected controllers
        _discoveryService.StartDiscovery();

        var initialController = _discoveryService.GetConnectedControllers().FirstOrDefault();
        UpdateControllerDetails(initialController);
        UpdateUiFromStatus(_streamingController.Status);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableImmersiveDarkMode();
    }

    private void EnableImmersiveDarkMode()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int darkMode = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
            }
        }
        catch { }
    }

    private void ApplyApplicationIcon()
    {
        try
        {
            Stream? iconStream = null;

            // 1. Try embedded assembly resource (WPF pack URI)
            try
            {
                var resourceUri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
                var streamInfo = System.Windows.Application.GetResourceStream(resourceUri);
                if (streamInfo != null)
                {
                    iconStream = streamInfo.Stream;
                }
            }
            catch { }

            // 2. If not found in assembly resources, try local filesystem paths
            if (iconStream == null)
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (!File.Exists(iconPath))
                {
                    string devPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "app.ico");
                    if (File.Exists(devPath))
                    {
                        iconPath = Path.GetFullPath(devPath);
                    }
                }

                if (File.Exists(iconPath))
                {
                    iconStream = File.OpenRead(iconPath);
                }
            }

            if (iconStream != null)
            {
                using (iconStream)
                using (var ms = new MemoryStream())
                {
                    iconStream.CopyTo(ms);
                    ms.Position = 0;

                    var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count > 0)
                    {
                        var bestFrame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault() ?? decoder.Frames[0];
                        Icon = bestFrame;
                        ImgAppHeaderIcon.Source = bestFrame;
                        ImgAppHeaderIcon.Visibility = Visibility.Visible;
                        HeaderVectorIcon.Visibility = Visibility.Collapsed;
                    }

                    ms.Position = 0;
                    _notifyIcon.Icon = new System.Drawing.Icon(ms);
                    return;
                }
            }

            // 3. Fallback: Extract the icon embedded in the running PE executable itself
            string? procPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(procPath) && File.Exists(procPath))
            {
                using var exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(procPath);
                if (exeIcon != null)
                {
                    _notifyIcon.Icon = (System.Drawing.Icon)exeIcon.Clone();

                    using var bmp = exeIcon.ToBitmap();
                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;

                    var bmpImage = new BitmapImage();
                    bmpImage.BeginInit();
                    bmpImage.CacheOption = BitmapCacheOption.OnLoad;
                    bmpImage.StreamSource = ms;
                    bmpImage.EndInit();
                    bmpImage.Freeze();

                    Icon = bmpImage;
                    ImgAppHeaderIcon.Source = bmpImage;
                    ImgAppHeaderIcon.Visibility = Visibility.Visible;
                    HeaderVectorIcon.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            ImgAppHeaderIcon.Visibility = Visibility.Collapsed;
            HeaderVectorIcon.Visibility = Visibility.Visible;
        }
        catch
        {
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            ImgAppHeaderIcon.Visibility = Visibility.Collapsed;
            HeaderVectorIcon.Visibility = Visibility.Visible;
        }
    }

    // ==========================================
    // IN-WINDOW PAGE NAVIGATION
    // ==========================================

    private void OnNavSettingsClicked(object sender, RoutedEventArgs e)
    {
        ViewMain.Visibility = Visibility.Collapsed;
        ViewLogs.Visibility = Visibility.Collapsed;
        ViewSettings.Visibility = Visibility.Visible;
    }

    private void OnNavLogsClicked(object sender, RoutedEventArgs e)
    {
        ViewMain.Visibility = Visibility.Collapsed;
        ViewSettings.Visibility = Visibility.Collapsed;
        ViewLogs.Visibility = Visibility.Visible;
        TxtLogConsole.ScrollToEnd();
    }

    private void OnBackToMainClicked(object sender, RoutedEventArgs e)
    {
        ViewSettings.Visibility = Visibility.Collapsed;
        ViewLogs.Visibility = Visibility.Collapsed;
        ViewMain.Visibility = Visibility.Visible;
    }

    // ==========================================
    // CONTROLLER & AUDIO STATUS UPDATES
    // ==========================================

    private void UpdateControllerDetails(ControllerInfo? controller)
    {
        if (controller == null)
        {
            // Main Dashboard state
            DotControllerStatus.Fill = new SolidColorBrush(Color.FromRgb(113, 113, 122)); // Neutral gray
            TxtControllerStatus.Text = "No Controller Connected";
            TxtControllerStatus.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            TxtControllerSubtext.Text = "Pair controller using Share + PS";
            TxtControllerSubtext.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            BtnStartStreaming.IsEnabled = false;

            // Settings page state
            TxtSettingsControllerName.Text = "No DualShock 4 connected";
            TxtSettingsInterface.Text = "Disconnected";
            TxtSettingsCapability.Text = "Not Detected";
            TxtSettingsDevicePath.Text = "-";
            TxtSettingsBattery.Text = "-";
            BorderBatteryWidget.Visibility = Visibility.Collapsed;
            return;
        }

        // Update Battery Indicator
        UpdateBatteryUi(controller.BatteryPercent, controller.IsCharging);

        // Settings details
        TxtSettingsControllerName.Text = controller.DisplayName;
        TxtSettingsDevicePath.Text = controller.DevicePath;

        if (controller.ConnectionType == ControllerConnectionType.Bluetooth)
        {
            TxtSettingsInterface.Text = "Bluetooth (Wireless)";

            if (controller.IsCompatible)
            {
                // [GREEN DOT] Compatible Controller Connected
                DotControllerStatus.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Vibrant emerald
                TxtControllerStatus.Text = $"{controller.DisplayName} Connected";
                TxtControllerStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252));

                if (controller.IsHeadphoneJackInserted.HasValue)
                {
                    TxtControllerSubtext.Text = controller.IsHeadphoneJackInserted.Value 
                        ? "Bluetooth • 3.5 mm Jack Connected" 
                        : "Bluetooth • Headphones Disconnected";
                }
                else
                {
                    TxtControllerSubtext.Text = "Bluetooth • Ready for wireless audio";
                }
                TxtControllerSubtext.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));

                TxtSettingsCapability.Text = $"Compatible ({controller.Model}, Wireless Stream: {controller.Capabilities.BtAudioStreaming})";
                BtnStartStreaming.IsEnabled = !_streamingController.IsStreaming && !_isOperationPending;
            }
            else
            {
                DotControllerStatus.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                TxtControllerStatus.Text = $"{controller.DisplayName} (Unsupported)";
                TxtControllerStatus.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                TxtControllerSubtext.Text = "Audio streaming unsupported on this device";
                TxtControllerSubtext.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));

                TxtSettingsCapability.Text = $"Unsupported ({controller.Model})";
                BtnStartStreaming.IsEnabled = false;
            }
        }
        else if (controller.ConnectionType == ControllerConnectionType.Usb)
        {
            DotControllerStatus.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
            TxtControllerStatus.Text = $"{controller.DisplayName} (USB)";
            TxtControllerStatus.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtControllerSubtext.Text = "Wired USB • Connect via Bluetooth for wireless stream";
            TxtControllerSubtext.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));

            TxtSettingsInterface.Text = "USB (Wired)";
            TxtSettingsCapability.Text = $"USB Connected ({controller.Model}, Windows USB Audio)";
            BtnStartStreaming.IsEnabled = false;
        }

        // Enable/Disable output options based on hardware capability
        BtnSegmentSpeaker.IsEnabled = controller.Capabilities.InternalSpeakerRouting != ControllerCapabilityState.Unsupported;
        BtnSegmentHeadphones.IsEnabled = controller.Capabilities.HeadphoneRouting != ControllerCapabilityState.Unsupported;
    }

    private void UpdateBatteryUi(int percent, bool isCharging)
    {
        if (percent < 0)
        {
            BorderBatteryWidget.Visibility = Visibility.Visible;
            TxtBatteryPercent.Text = "--%";
            IconBatteryBolt.Visibility = Visibility.Collapsed;
            BatteryFill.Width = 4;
            BatteryFill.Background = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            BorderBatteryWidget.ToolTip = "Reading controller battery telemetry...";
            TxtSettingsBattery.Text = "Reading...";
            return;
        }

        BorderBatteryWidget.Visibility = Visibility.Visible;
        TxtBatteryPercent.Text = $"{percent}%";

        // Battery fill container width is 15px max
        double fillWidth = Math.Max(2.0, Math.Min(15.0, (percent / 100.0) * 15.0));
        BatteryFill.Width = fillWidth;

        if (isCharging)
        {
            IconBatteryBolt.Visibility = Visibility.Visible;
            BatteryFill.Background = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // Cyan #38BDF8
            BorderBatteryWidget.ToolTip = $"Controller Battery: {percent}% (Charging)";
            TxtSettingsBattery.Text = $"{percent}% (Charging)";
        }
        else
        {
            IconBatteryBolt.Visibility = Visibility.Collapsed;
            Color fillColor;
            if (percent > 50)
                fillColor = Color.FromRgb(34, 197, 94); // Green #22C55E
            else if (percent > 20)
                fillColor = Color.FromRgb(234, 179, 8); // Yellow #EAB308
            else
                fillColor = Color.FromRgb(239, 68, 68); // Red #EF4444

            BatteryFill.Background = new SolidColorBrush(fillColor);
            BorderBatteryWidget.ToolTip = $"Controller Battery: {percent}%";
            TxtSettingsBattery.Text = $"{percent}%";
        }
    }

    private void UpdateUiFromStatus(StreamingStatus status)
    {
        if (status.ControllerBatteryPercent >= 0)
        {
            UpdateBatteryUi(status.ControllerBatteryPercent, status.IsControllerCharging);
        }

        if (status.ActiveOutputRoute == AudioOutputRoute.Headphones || status.ActiveOutputRoute == AudioOutputRoute.InternalSpeaker)
        {
            UpdateOutputSelectorVisual(status.ActiveOutputRoute == AudioOutputRoute.Headphones, animate: false);
        }

        switch (status.State)
        {
            case StreamingState.Streaming:
                DotAudioStatus.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                TxtAudioStatus.Text = "Audio Transmission: Active";
                TxtAudioStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252));
                BtnStartStreaming.IsEnabled = false;
                BtnStopStreaming.IsEnabled = !_isOperationPending;
                break;

            case StreamingState.Starting:
                DotAudioStatus.Fill = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Blue
                TxtAudioStatus.Text = "Audio Transmission: Starting...";
                TxtAudioStatus.Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250));
                BtnStartStreaming.IsEnabled = false;
                BtnStopStreaming.IsEnabled = false;
                break;

            case StreamingState.Stopping:
                DotAudioStatus.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                TxtAudioStatus.Text = "Audio Transmission: Stopping...";
                TxtAudioStatus.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                BtnStartStreaming.IsEnabled = false;
                BtnStopStreaming.IsEnabled = false;
                break;

            case StreamingState.Failed:
                DotAudioStatus.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                TxtAudioStatus.Text = "Audio Transmission: Error";
                TxtAudioStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                BtnStartStreaming.IsEnabled = status.ActiveController?.IsCompatible == true && !_isOperationPending;
                BtnStopStreaming.IsEnabled = false;
                break;

            case StreamingState.ConnectedBluetoothReady:
                DotAudioStatus.Fill = new SolidColorBrush(Color.FromRgb(113, 113, 122)); // Neutral
                TxtAudioStatus.Text = "Audio Transmission: Stopped";
                TxtAudioStatus.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                BtnStartStreaming.IsEnabled = !_isOperationPending;
                BtnStopStreaming.IsEnabled = false;
                break;

            case StreamingState.ConnectedUsbOnly:
            case StreamingState.Disconnected:
            default:
                DotAudioStatus.Fill = new SolidColorBrush(Color.FromRgb(113, 113, 122)); // Neutral
                TxtAudioStatus.Text = "Audio Transmission: Stopped";
                TxtAudioStatus.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                BtnStartStreaming.IsEnabled = false;
                BtnStopStreaming.IsEnabled = false;
                break;
        }
    }

    // ==========================================
    // STREAMING OPERATION CONTROLS
    // ==========================================

    private async void OnStartStreamingClicked(object sender, RoutedEventArgs e)
    {
        if (_isOperationPending || _streamingController.IsStreaming) return;
        _isOperationPending = true;
        BtnStartStreaming.IsEnabled = false;
        BtnStopStreaming.IsEnabled = false;

        try
        {
            _diagnosticService.Info("User requested Start Streaming.");
            bool started = await _streamingController.StartStreamingAsync();
            if (!started)
            {
                _diagnosticService.Warn("StartStreamingAsync returned false.");
            }
        }
        catch (Exception ex)
        {
            _diagnosticService.Error($"Error starting audio stream: {ex.Message}", ex);
        }
        finally
        {
            _isOperationPending = false;
            UpdateUiFromStatus(_streamingController.Status);
        }
    }

    private async void OnStopStreamingClicked(object sender, RoutedEventArgs e)
    {
        if (_isOperationPending || !_streamingController.IsStreaming) return;
        _isOperationPending = true;
        BtnStartStreaming.IsEnabled = false;
        BtnStopStreaming.IsEnabled = false;

        try
        {
            _diagnosticService.Info("User requested Stop Streaming.");
            await _streamingController.StopStreamingAsync();
        }
        catch (Exception ex)
        {
            _diagnosticService.Error($"Error stopping audio stream: {ex.Message}", ex);
        }
        finally
        {
            _isOperationPending = false;
            UpdateUiFromStatus(_streamingController.Status);
        }
    }

    // ==========================================
    // SETTINGS PAGE CONTROLS & LOGIC
    // ==========================================

    private void LoadSettingsIntoUi()
    {
        var settings = _settingsService.CurrentSettings;
        SliderVolume.Value = settings.HeadphoneVolume;
        ChkMuteSpeakers.IsChecked = settings.MuteSpeakersWhileStreaming;
        ChkAutoStart.IsChecked = settings.AutoStartOnConnect;
        ChkMinimizeToTray.IsChecked = settings.MinimizeToTray;
        UpdateVolumeLabel(settings.HeadphoneVolume);
        UpdateSpeakerMuteButton(_captureService.IsEndpointMuted);
        UpdateOutputSelectorVisual(settings.HeadsetMode, animate: false);
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

    // ==========================================
    // OUTPUT ROUTING SEGMENTED TOGGLE
    // ==========================================

    private async void OnSegmentSpeakerClicked(object sender, RoutedEventArgs e)
    {
        if (!_settingsService.CurrentSettings.HeadsetMode && _streamingController.Status.ActiveOutputRoute == AudioOutputRoute.InternalSpeaker) return;
        TxtActiveOutputBadge.Text = "Switching Output...";
        await _streamingController.SetOutputDestinationAsync(false);
        bool isHeadphones = _streamingController.Status.ActiveOutputRoute == AudioOutputRoute.Headphones;
        UpdateOutputSelectorVisual(isHeadphones, animate: true);
    }

    private async void OnSegmentHeadphonesClicked(object sender, RoutedEventArgs e)
    {
        if (_settingsService.CurrentSettings.HeadsetMode && _streamingController.Status.ActiveOutputRoute == AudioOutputRoute.Headphones) return;
        TxtActiveOutputBadge.Text = "Switching Output...";
        await _streamingController.SetOutputDestinationAsync(true);
        bool isHeadphones = _streamingController.Status.ActiveOutputRoute == AudioOutputRoute.Headphones || _settingsService.CurrentSettings.HeadsetMode;
        UpdateOutputSelectorVisual(isHeadphones, animate: true);
    }

    private void OnSegmentButtonKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Left)
        {
            OnSegmentSpeakerClicked(sender, e);
            BtnSegmentSpeaker.Focus();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Right)
        {
            OnSegmentHeadphonesClicked(sender, e);
            BtnSegmentHeadphones.Focus();
            e.Handled = true;
        }
    }

    private void OnSegmentContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateOutputSelectorVisual(_settingsService.CurrentSettings.HeadsetMode, animate: false);
    }

    private void UpdateOutputSelectorVisual(bool headsetMode, bool animate = true)
    {
        // Badge text and color
        TxtActiveOutputBadge.Text = headsetMode ? "AUX / Headphone Jack" : "Internal Speaker";

        // Segment text and icon colors
        if (headsetMode)
        {
            // Headphones is active
            TxtSegmentHeadphones.Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252));
            TxtSegmentHeadphones.FontWeight = FontWeights.SemiBold;
            IconHeadphones.Fill = new SolidColorBrush(Color.FromRgb(254, 226, 226)); // Soft light red/white

            TxtSegmentSpeaker.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)); // Greyed out
            TxtSegmentSpeaker.FontWeight = FontWeights.Normal;
            IconSpeaker.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
        }
        else
        {
            // Speaker is active
            TxtSegmentSpeaker.Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252));
            TxtSegmentSpeaker.FontWeight = FontWeights.SemiBold;
            IconSpeaker.Fill = new SolidColorBrush(Color.FromRgb(254, 226, 226)); // Soft light red/white

            TxtSegmentHeadphones.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)); // Greyed out
            TxtSegmentHeadphones.FontWeight = FontWeights.Normal;
            IconHeadphones.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
        }

        // Sliding pill transition
        double containerWidth = GridSegmented.ActualWidth;
        double segmentWidth = containerWidth > 0 ? (containerWidth / 2.0) : 0;
        double targetX = headsetMode ? segmentWidth : 0.0;

        if (animate && segmentWidth > 0)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = targetX,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            TransIndicator.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        else
        {
            TransIndicator.BeginAnimation(TranslateTransform.XProperty, null);
            TransIndicator.X = targetX;
        }
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

    // ==========================================
    // LOGS & TELEMETRY CONTROLS
    // ==========================================

    private void OnTelemetryTick(object? sender, EventArgs e)
    {
        var status = _streamingController.Status;
        if (status.State == StreamingState.Streaming)
        {
            long kbSent = status.BytesSent / 1024;
            TxtPacketsSent.Text = $"{status.PacketsSent:N0} ({kbSent:N0} KB)";
            TxtEstimatedLatency.Text = $"{status.EstimatedLatencyMs:F0} ms";
            PbBufferLevel.Value = Math.Min(96, status.BufferLevelFrames);
            TxtUnderruns.Text = $"{status.UnderrunCount:N0} / {status.DroppedFramesCount:N0}";
        }
        else
        {
            TxtEstimatedLatency.Text = "Idle";
            PbBufferLevel.Value = 0;
        }

        UpdateSpeakerMuteButton(_captureService.IsEndpointMuted);
    }

    private void OnLogMessageReceived(object? sender, LogEntry msg)
    {
        Dispatcher.InvokeAsync(() =>
        {
            string line = $"[{msg.Timestamp:HH:mm:ss}] [{msg.Level.ToString().ToUpperInvariant()}] {msg.Message}{Environment.NewLine}";
            if (TxtLogConsole.Text.Length > 25000)
            {
                TxtLogConsole.Text = TxtLogConsole.Text[^12000..];
            }
            TxtLogConsole.AppendText(line);
            if (ViewLogs.Visibility == Visibility.Visible)
            {
                TxtLogConsole.ScrollToEnd();
            }
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

    // ==========================================
    // WINDOW & SYSTEM TRAY LIFECYCLE
    // ==========================================

    private void OnMinimizeToTrayClicked(object sender, RoutedEventArgs e)
    {
        Hide();
        _notifyIcon.ShowBalloonTip(1500, "DS4-SoundLink", 
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