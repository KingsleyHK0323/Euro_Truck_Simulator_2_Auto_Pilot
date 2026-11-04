// Edited by: OpenAI-Codex_Responsible: Source_Code
// Edited by: OpenAI-Codex_Source_Code
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ETS2AP.Ui.Resources;
using Microsoft.Win32;

namespace ETS2AP.Ui;

public partial class MainWindow : Window
{
    private readonly UiPreferences _preferences = UiPreferences.Load();
    private readonly NavigationState _navigation = new();
    private readonly NavigationTimeDisplay _navigationTime = new();
    private readonly DispatcherTimer _navigationExpiryTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly CancellationTokenSource _serviceCancellation = new();
    private readonly PythonServiceRestartPolicy _serviceRestartPolicy = new();
    private readonly ObservableCollection<ActivityItem> _activityEntries = new();
    private readonly ObservableCollection<ActivityItem> _recentActivityEntries = new();
    private UiPipeClient? _service;
    private PerceptionCaptureService? _perceptionCapture;
    private Process? _pythonServiceProcess;
    private string? _pythonInstallRoot;
    private string? _uiExecutable;
    private bool _isClosing;
    private bool _installingConnector;
    private bool _hasObservedServiceConnection;
    private bool _serviceConnected;
    private bool? _lastTelemetryFresh;
    private string? _lastLoggedState;
    private string? _lastLoggedErrorCode;
    private UiStatus? _latestStatus;
    private bool _statusExpired;

    public MainWindow()
    {
        InitializeComponent();
        _navigationExpiryTimer.Tick += NavigationExpiryTimer_Tick;
        _navigationExpiryTimer.Tick += StatusExpiryTimer_Tick;
        _navigationExpiryTimer.Start();
        ActivityList.ItemsSource = _activityEntries;
        RecentActivityList.ItemsSource = _recentActivityEntries;
        ThemePicker.SelectedIndex = (int)_preferences.Theme;
        GameFolderText.Text = _preferences.GameFolder ?? string.Empty;
        UpdateFolderStatus();
        ThemeManager.Apply(_preferences.Theme);
        ShowPage(UiPage.Overview);
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        Closed += Window_Closed;
        SourceInitialized += Window_SourceInitialized;
        StartServiceConnection();
    }

    private void ShowPage(UiPage page)
    {
        _navigation.Navigate(page);
        OverviewPage.Visibility = _navigation.IsSelected(UiPage.Overview) ? Visibility.Visible : Visibility.Collapsed;
        SetupPage.Visibility = _navigation.IsSelected(UiPage.Setup) ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = _navigation.IsSelected(UiPage.Settings) ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = _navigation.IsSelected(UiPage.Activity) ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (button, buttonPage) in new[]
        {
            (OverviewNav, UiPage.Overview), (SetupNav, UiPage.Setup),
            (SettingsNav, UiPage.Settings), (ActivityNav, UiPage.Activity)
        })
        {
            bool selected = _navigation.IsSelected(buttonPage);
            button.SetResourceReference(BackgroundProperty, selected ? "SelectedBrush" : "NavigationBrush");
            button.SetResourceReference(ForegroundProperty, selected ? "SelectedTextBrush" : "PrimaryTextBrush");
            AutomationProperties.SetItemStatus(button, selected ? Strings.CurrentPage : string.Empty);
        }
        if (!IsLoaded) return;
        PageScroll.ScrollToTop();
        (page switch
        {
            UiPage.Setup => SetupHeading,
            UiPage.Settings => SettingsHeading,
            UiPage.Activity => ActivityHeading,
            _ => OverviewHeading
        }).Focus();
    }

    private void Overview_Click(object sender, RoutedEventArgs e) => ShowPage(UiPage.Overview);
    private void Setup_Click(object sender, RoutedEventArgs e) => ShowPage(UiPage.Setup);
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowPage(UiPage.Settings);
    private void Activity_Click(object sender, RoutedEventArgs e) => ShowPage(UiPage.Activity);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        UiPage? page = NavigationState.FromShortcut(e.Key, Keyboard.Modifiers);
        if (page is null) return;
        ShowPage(page.Value);
        e.Handled = true;
    }

    private void Window_Activated(object sender, EventArgs e)
    {
        ThemeManager.Apply(_preferences.Theme);
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e) => QueueThemeUpdate();
    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueThemeUpdate();

    private void QueueThemeUpdate()
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() => ThemeManager.Apply(_preferences.Theme)));
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _navigationExpiryTimer.Stop();
        _navigationExpiryTimer.Tick -= NavigationExpiryTimer_Tick;
        _navigationExpiryTimer.Tick -= StatusExpiryTimer_Tick;
        _perceptionCapture?.Dispose();
        _perceptionCapture = null;
        _serviceCancellation.Cancel();
        StopPythonService();
        _serviceCancellation.Dispose();
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            _perceptionCapture = new PerceptionCaptureService(this, TrustedControlServerImage());
            _perceptionCapture.Start();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException)
        {
            _perceptionCapture?.Dispose();
            _perceptionCapture = null;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Strings.GameFolder };
        if (dialog.ShowDialog(this) != true) return;
        GameFolderText.Text = dialog.FolderName;
        _preferences.GameFolder = dialog.FolderName;
        _preferences.Save();
        UpdateFolderStatus();
        RestartPythonService();
    }

    private async void InstallConnector_Click(object sender, RoutedEventArgs e)
    {
        string gameFolder = GameFolderText.Text;
        if (!File.Exists(Path.Combine(gameFolder, "bin", "win_x64", "eurotrucks2.exe")))
        {
            InstallStatus.Text = Strings.FolderInvalid;
            UpdateFolderStatus();
            return;
        }
        if (ConnectorInstaller.IsGameRunning())
        {
            InstallStatus.Text = Strings.ConnectorGameMustBeClosed;
            return;
        }

        _installingConnector = true;
        InstallConnectorButton.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        try
        {
            InstallStatus.Text = Strings.ConnectorInstallElevationPrompt;
            int result = await ConnectorInstaller.LaunchElevatedAsync(gameFolder, _serviceCancellation.Token);
            InstallStatus.Text = result switch
            {
                0 => Strings.ConnectorInstallSuccess,
                ConnectorInstaller.GameRunningExitCode => Strings.ConnectorGameMustBeClosed,
                _ => Strings.ConnectorInstallFailed
            };
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            InstallStatus.Text = Strings.ConnectorInstallCancelled;
        }
        catch (OperationCanceledException)
        {
            InstallStatus.Text = Strings.ConnectorInstallCancelled;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or
            ArgumentException or CryptographicException or InvalidOperationException or Win32Exception)
        {
            InstallStatus.Text = Strings.ConnectorInstallFailed;
        }
        finally
        {
            _installingConnector = false;
            InstallProgress.Visibility = Visibility.Collapsed;
            UpdateFolderStatus();
        }
    }

    private void UpdateFolderStatus()
    {
        if (string.IsNullOrWhiteSpace(GameFolderText.Text))
        {
            FolderStatus.Text = Strings.NoFolderSelected;
            InstallConnectorButton.IsEnabled = false;
            return;
        }
        string executable = Path.Combine(GameFolderText.Text, "bin", "win_x64", "eurotrucks2.exe");
        FolderStatus.Text = File.Exists(executable) ? Strings.FolderAccepted : Strings.FolderInvalid;
        InstallConnectorButton.IsEnabled = !_installingConnector && File.Exists(executable);
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ThemePicker.SelectedIndex < 0) return;
        _preferences.Theme = (ThemeChoice)ThemePicker.SelectedIndex;
        ThemeManager.Apply(_preferences.Theme);
        _preferences.Save();
    }

    private void CopyStatus_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildStatusSnapshot());
            CopyFeedback.Text = Strings.StatusCopied;
        }
        catch (ExternalException)
        {
            CopyFeedback.Text = Strings.StatusCopyFailed;
        }
    }

    private string BuildStatusSnapshot()
    {
        if (_latestStatus is UiStatus currentStatus && !_statusExpired && !IsStatusFresh(currentStatus))
            ApplyExpiredStatus();
        if (_statusExpired)
            return string.Join(Environment.NewLine, new[]
            {
                $"{Strings.Connection}: {ConnectionTitle.Text}",
                $"{Strings.Status}: {ConnectionDetail.Text}",
                $"{Strings.AutomationState}: {AutomationStateValue.Text}",
                $"{Strings.TelemetryAge}: {Strings.NotProvided}",
                $"{Strings.Speed}: {SpeedValue.Text} {Strings.Kmh}",
                $"{Strings.SpeedLimit}: {SpeedLimitValue.Text} {Strings.Kmh}",
                $"{Strings.DistanceRemaining}: {DistanceValue.Text}",
                $"{Strings.FrameCaptureLabel}: {FrameCaptureValue.Text}",
                $"{Strings.GameFocusLabel}: {GameFocusValue.Text}",
                $"{Strings.InputMonitoringLabel}: {InputMonitoringValue.Text}",
                $"{Strings.ManualInputLabel}: {ManualInputValue.Text}",
                $"{Strings.LaneConfidenceLabel}: {LaneConfidenceValue.Text}",
                $"{Strings.FrontObstacleLabel}: {FrontObstacleValue.Text}"
            });
        UiStatus? status = _latestStatus;
        string age = status?.AgeS is double seconds
            ? string.Format(CultureInfo.CurrentCulture, Strings.TelemetryAgeFormat, seconds)
            : Strings.NotProvided;
        return string.Join(Environment.NewLine, new[]
        {
            $"{Strings.Connection}: {ConnectionTitle.Text}",
            $"{Strings.Status}: {ConnectionDetail.Text}",
            $"{Strings.AutomationState}: {(status is null ? Strings.OfflineState : StateDescription(status.State))}",
            $"{Strings.TelemetryAge}: {age}",
            $"{Strings.Speed}: {SpeedValue.Text} {Strings.Kmh}",
            $"{Strings.SpeedLimit}: {SpeedLimitValue.Text} {Strings.Kmh}",
            $"{Strings.DistanceRemaining}: {DistanceValue.Text}",
            $"{Strings.FrameCaptureLabel}: {FrameCaptureValue.Text}",
            $"{Strings.GameFocusLabel}: {GameFocusValue.Text}",
            $"{Strings.InputMonitoringLabel}: {InputMonitoringValue.Text}",
            $"{Strings.ManualInputLabel}: {ManualInputValue.Text}",
            $"{Strings.LaneConfidenceLabel}: {LaneConfidenceValue.Text}",
            $"{Strings.FrontObstacleLabel}: {FrontObstacleValue.Text}"
        });
    }

    private void StartServiceConnection()
    {
        string installRoot = Path.GetFullPath(AppContext.BaseDirectory);
        string? uiExecutable = Environment.ProcessPath;
        if (uiExecutable is null || !PythonServiceLauncher.IsAvailable(installRoot, uiExecutable))
        {
            ApplyStatus(null);
            return;
        }

        _pythonInstallRoot = installRoot;
        _uiExecutable = Path.GetFullPath(uiExecutable);
        try
        {
            _service = new UiPipeClient(PythonServiceLauncher.RuntimePath(installRoot));
            _service.StatusChanged += status =>
            {
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.BeginInvoke(new Action(() => ApplyStatus(status)));
            };
            _ = _service.RunAsync(_serviceCancellation.Token);
            StartPythonService();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            StopPythonService();
            ApplyStatus(null);
        }
    }

    private void StartPythonService()
    {
        if (_isClosing || _pythonInstallRoot is null || _uiExecutable is null) return;
        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = PythonServiceLauncher.CreateStartInfo(
                    _pythonInstallRoot, _uiExecutable, TrustedControlServerImage()),
                EnableRaisingEvents = true
            };
            process.Exited += PythonServiceProcess_Exited;
            _pythonServiceProcess = process;
            if (process.Start()) return;
            _pythonServiceProcess = null;
            process.Exited -= PythonServiceProcess_Exited;
            process.Dispose();
            ApplyStatus(null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            if (process is not null)
            {
                _pythonServiceProcess = null;
                process.Exited -= PythonServiceProcess_Exited;
                process.Dispose();
            }
            ApplyStatus(null);
        }
    }

    private void RestartPythonService()
    {
        if (_isClosing || _pythonInstallRoot is null || _uiExecutable is null) return;
        StopPythonService();
        StartPythonService();
    }

    private string? TrustedControlServerImage()
    {
        string? gameFolder = _preferences.GameFolder;
        if (string.IsNullOrWhiteSpace(gameFolder)) return null;
        try
        {
            string image = Path.GetFullPath(Path.Combine(
                gameFolder, "bin", "win_x64", "eurotrucks2.exe"));
            return File.Exists(image) ? image : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void PythonServiceProcess_Exited(object? sender, EventArgs e)
    {
        if (sender is not Process process || Dispatcher.HasShutdownStarted) return;
        try
        {
            _ = Dispatcher.BeginInvoke(new Action(() => HandlePythonServiceExit(process)));
        }
        catch (InvalidOperationException)
        {
            process.Dispose();
        }
    }

    private void HandlePythonServiceExit(Process process)
    {
        if (!ReferenceEquals(_pythonServiceProcess, process))
        {
            process.Dispose();
            return;
        }

        _pythonServiceProcess = null;
        process.Exited -= PythonServiceProcess_Exited;
        process.Dispose();
        ApplyStatus(null);
        if (!_isClosing && _serviceRestartPolicy.TryConsumeRestart())
            StartPythonService();
    }

    private void StopPythonService()
    {
        Process? process = _pythonServiceProcess;
        _pythonServiceProcess = null;
        if (process is null) return;
        process.Exited -= PythonServiceProcess_Exited;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(2000);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ApplyStatus(UiStatus? status)
    {
        if (status is not null && !IsStatusFresh(status))
        {
            _latestStatus = status;
            _navigationTime.SetStatus(status);
            RefreshNavigationTime();
            ApplyExpiredStatus();
            return;
        }
        _statusExpired = false;
        _latestStatus = status;
        _navigationTime.SetStatus(status);
        RefreshNavigationTime();
        if (status is null)
        {
            RecordStatus(null, telemetryFresh: false);
            ConnectionTitle.Text = Strings.NotConnected;
            UpdateConnectionDetail(Strings.NotConnectedDetail);
            SpeedValue.Text = SpeedLimitValue.Text = DistanceValue.Text = "--";
            ApplyPerception(null);
            GameCheck.Text = ConnectorCheck.Text = Strings.Pending;
            ControlCheck.Text = Strings.NotProvided;
            AutomationStateValue.Text = Strings.OfflineState;
            return;
        }

        ConnectionTitle.Text = Strings.ServiceConnected;
        ApplyPerception(status.Perception);
        AutomationStateValue.Text = StateDescription(status.State);
        bool fresh = status.AgeS is <= 0.25 && status.SpeedMps is not null;
        RecordStatus(status, fresh);
        string? errorCode = status.ErrorCode;
        if (errorCode is null && !fresh)
            errorCode = status.AgeS is null || status.SpeedMps is null
                ? "TELEMETRY_UNAVAILABLE"
                : "TELEMETRY_STALE";
        UpdateConnectionDetail(errorCode is null
            ? StateDescription(status.State)
            : ErrorDescription(errorCode));
        SpeedValue.Text = fresh ? FormatKmh(status.SpeedMps) : "--";
        SpeedLimitValue.Text = fresh ? FormatKmh(status.SpeedLimitMps) : "--";
        DistanceValue.Text = fresh && status.DistanceM is double distance
            ? string.Format(CultureInfo.CurrentCulture, Strings.DistanceKmFormat, distance / 1000.0)
            : "--";
        GameCheck.Text = status.ErrorCode == "GAME_NOT_RUNNING"
            ? Strings.Unavailable
            : fresh ? Strings.Available : Strings.Pending;
        ConnectorCheck.Text = fresh
            ? Strings.Available
            : status.ErrorCode is "TELEMETRY_UNAVAILABLE" or "TELEMETRY_STALE"
                ? Strings.Unavailable
                : Strings.Pending;
        ControlCheck.Text = DescribeNativeControlStatus(status.Control);
    }

    private void NavigationExpiryTimer_Tick(object? sender, EventArgs e) => RefreshNavigationTime();

    private void StatusExpiryTimer_Tick(object? sender, EventArgs e)
    {
        if (_latestStatus is UiStatus status && !_statusExpired && !IsStatusFresh(status))
            ApplyExpiredStatus();
    }

    private static bool IsStatusFresh(UiStatus status)
    {
        try
        {
            return UiStatusFreshness.IsFresh(status, UiPipeClient.QpcNanosecondsValue());
        }
        catch (Exception error) when (error is OverflowException or InvalidOperationException)
        {
            return false;
        }
    }

    private void ApplyExpiredStatus()
    {
        ConnectionTitle.Text = Strings.ServiceConnected;
        if (_statusExpired) return;
        _statusExpired = true;
        if (!_hasObservedServiceConnection || !_serviceConnected)
            AddActivity(Strings.ActivityServiceConnected);
        _hasObservedServiceConnection = true;
        _serviceConnected = true;
        if (_lastTelemetryFresh != false)
            AddActivity(Strings.ActivityTelemetryUnavailable);
        _lastTelemetryFresh = false;
        AnnounceActivityUpdate();
        UpdateConnectionDetail(Strings.ErrorStaleStatus);
        SpeedValue.Text = SpeedLimitValue.Text = DistanceValue.Text = "--";
        EtaValue.Text = Strings.Unavailable;
        ApplyPerception(null);
        GameCheck.Text = ConnectorCheck.Text = Strings.Pending;
        ControlCheck.Text = Strings.NotProvided;
        AutomationStateValue.Text = Strings.Unavailable;
    }

    private void RefreshNavigationTime()
    {
        if (_statusExpired)
        {
            EtaValue.Text = Strings.Unavailable;
            return;
        }
        try
        {
            EtaValue.Text = _navigationTime.Render(UiPipeClient.QpcNanosecondsValue(),
                Strings.NavigationTimeFormat, CultureInfo.CurrentCulture) ?? Strings.Unavailable;
        }
        catch (Exception error) when (error is OverflowException or InvalidOperationException or FormatException)
        {
            EtaValue.Text = Strings.Unavailable;
        }
    }
    private static string DescribeNativeControlStatus(NativeControlStatus? status)
    {
        if (status is null) return Strings.NotProvided;
        if (status.Connection == "not_configured") return Strings.ControlLinkNotConfigured;
        if (status.Connection == "disconnected") return Strings.ControlLinkDisconnected;

        string nativeState = status.State switch
        {
            "disengaged" => Strings.NativeControlDisengaged,
            "engaged" => Strings.NativeControlEngaged,
            "manual_override_latched" => Strings.NativeControlManualOverride,
            "timeout" => Strings.NativeControlTimeout,
            "rejected" => Strings.NativeControlRejected,
            _ => Strings.NotProvided
        };
        if (status.Reason == "none")
            return string.Format(CultureInfo.CurrentCulture,
                Strings.ControlLinkConnectedStateFormat, nativeState);

        string nativeReason = status.Reason switch
        {
            "manual_input" => Strings.NativeReasonManualInput,
            "command_timeout" => Strings.NativeReasonCommandTimeout,
            "malformed_packet" => Strings.NativeReasonMalformedPacket,
            "version_mismatch" => Strings.NativeReasonVersionMismatch,
            "client_disconnected" => Strings.NativeReasonClientDisconnected,
            "telemetry_unavailable" => Strings.NativeReasonTelemetryUnavailable,
            _ => Strings.NotProvided
        };
        return string.Format(CultureInfo.CurrentCulture,
            Strings.ControlLinkConnectedStateReasonFormat, nativeState, nativeReason);
    }

    private void ApplyPerception(PerceptionStatus? status)
    {
        FrameCaptureValue.Text = status is null ? Strings.NotProvided
            : status.FrameValid ? Strings.Available : Strings.Unavailable;
        GameFocusValue.Text = status is null ? Strings.NotProvided
            : status.GameFocused ? Strings.Available : Strings.Unavailable;
        InputMonitoringValue.Text = status is null ? Strings.NotProvided
            : status.InputMonitorHealthy ? Strings.Available : Strings.Unavailable;
        ManualInputValue.Text = status is null ? Strings.NotProvided
            : status.ManualInputActive ? Strings.StatusActive : Strings.StatusInactive;
        LaneConfidenceValue.Text = status?.LaneConfidence is double confidence
            ? string.Format(CultureInfo.CurrentCulture, Strings.LaneConfidenceFormat,
                confidence * 100.0)
            : Strings.NotProvided;
        FrontObstacleValue.Text = status?.FrontClear switch
        {
            true => Strings.StatusClear,
            false => Strings.StatusNotClear,
            null => Strings.StatusNotValidated
        };
    }

    private void RecordStatus(UiStatus? status, bool telemetryFresh)
    {
        bool connected = status is not null;
        if (connected && (!_hasObservedServiceConnection || !_serviceConnected))
            AddActivity(Strings.ActivityServiceConnected);
        else if (!connected && _hasObservedServiceConnection && _serviceConnected)
            AddActivity(Strings.ActivityServiceDisconnected);
        _hasObservedServiceConnection = true;
        _serviceConnected = connected;

        if (status is null)
        {
            _lastTelemetryFresh = false;
            _lastLoggedState = null;
            _lastLoggedErrorCode = null;
            AnnounceActivityUpdate();
            return;
        }

        if (_lastTelemetryFresh != telemetryFresh)
            AddActivity(telemetryFresh ? Strings.ActivityTelemetryAvailable : Strings.ActivityTelemetryUnavailable);
        _lastTelemetryFresh = telemetryFresh;

        if (!string.Equals(_lastLoggedState, status.State, StringComparison.Ordinal))
            AddActivity(string.Format(CultureInfo.CurrentCulture, Strings.ActivityStateChangedFormat,
                StateDescription(status.State)));
        _lastLoggedState = status.State;

        if (!string.IsNullOrEmpty(status.ErrorCode) &&
            !string.Equals(_lastLoggedErrorCode, status.ErrorCode, StringComparison.Ordinal))
            AddActivity(string.Format(CultureInfo.CurrentCulture, Strings.ActivityIssueFormat,
                ErrorDescription(status.ErrorCode)));
        _lastLoggedErrorCode = status.ErrorCode;
        AnnounceActivityUpdate();
    }

    private void AddActivity(string message)
    {
        var entry = new ActivityItem(DateTime.Now.ToString("t", CultureInfo.CurrentCulture), message);
        _activityEntries.Insert(0, entry);
        _recentActivityEntries.Insert(0, entry);
        while (_activityEntries.Count > 50) _activityEntries.RemoveAt(_activityEntries.Count - 1);
        while (_recentActivityEntries.Count > 3) _recentActivityEntries.RemoveAt(_recentActivityEntries.Count - 1);
        ActivityEmpty.Visibility = Visibility.Collapsed;
        RecentActivityEmpty.Visibility = Visibility.Collapsed;
    }

    private void AnnounceActivityUpdate()
    {
        ItemsControl? liveRegion = _navigation.IsSelected(UiPage.Activity)
            ? ActivityList
            : _navigation.IsSelected(UiPage.Overview) ? RecentActivityList : null;
        if (liveRegion is null) return;
        var peer = UIElementAutomationPeer.FromElement(liveRegion)
            ?? UIElementAutomationPeer.CreatePeerForElement(liveRegion);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static string FormatKmh(double? speedMps) => speedMps is double speed
        ? Math.Round(speed * 3.6, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.CurrentCulture)
        : "--";

    private void UpdateConnectionDetail(string detail)
    {
        if (ConnectionDetail.Text == detail) return;
        ConnectionDetail.Text = detail;
        var peer = UIElementAutomationPeer.FromElement(ConnectionDetail)
            ?? UIElementAutomationPeer.CreatePeerForElement(ConnectionDetail);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static string ErrorDescription(string? code) => code switch
    {
        "GAME_NOT_RUNNING" => Strings.ErrorGameNotRunning,
        "TELEMETRY_UNAVAILABLE" => Strings.ErrorTelemetryUnavailable,
        "TELEMETRY_STALE" => Strings.ErrorTelemetryStale,
        "CONTROL_UNAVAILABLE" => Strings.ErrorControlUnavailable,
        "MANUAL_OVERRIDE" => Strings.ErrorManualOverride,
        "COMMAND_TIMEOUT" => Strings.ErrorCommandTimeout,
        "INVALID_CONFIG" => Strings.ErrorInvalidConfig,
        "UNSUPPORTED_VERSION" => Strings.ErrorUnsupportedVersion,
        "MALFORMED_MESSAGE" => Strings.ErrorMalformedMessage,
        "STALE_STATUS" => Strings.ErrorStaleStatus,
        "INTERNAL_ERROR" => Strings.ErrorInternalError,
        _ => Strings.ServiceStatusUnknown
    };

    private static string StateDescription(string state) => state switch
    {
        "ready" => Strings.StateReady,
        "engaged" => Strings.StateEngaged,
        "manual_override" => Strings.StateManualOverride,
        "fault" => Strings.StateFault,
        _ => Strings.ServiceStatusUnknown
    };
}

// Edited by: OpenAI-Codex_Responsible: Source_Code
internal sealed record ActivityItem(string Time, string Message);
