// Edited by: OpenAI-Codex_Responsible: Source_Code
// Edited by: OpenAI-Codex_Source_Code
using System.Collections;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ETS2AP.Ui;
using ETS2AP.Ui.Resources;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--ipc-smoke")
            return CheckPipeSmoke(args[1]);
        if (args.Length == 1 && args[0] == "--connector-install-smoke")
        {
            try
            {
                CheckConnectorInstallTransaction();
                Console.WriteLine("PASS Connector install transaction");
                Console.WriteLine("Connector install smoke: 1 passed, 0 failed");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL Connector install transaction: {error.Message}");
                Console.WriteLine("Connector install smoke: 0 passed, 1 failed");
                return 1;
            }
        }
        (string Name, Action Run)[] checks =
        [
            ("Localized resources", CheckResources),
            ("Theme resources and contrast", CheckThemes),
            ("Preferences parsing and round trip", CheckPreferences),
            ("Navigation state and shortcuts", CheckNavigation),
            ("UI status protocol", CheckStatusProtocol),
            ("UI navigation time and freshness", CheckNavigationTimeStatus),
            ("UI QPC nanosecond clock", CheckUiQpcNanoseconds),
            ("UI navigation version compatibility", CheckUiV13HelloAck),
            ("UI v1.2 handshake", CheckUiV12HelloAck),
            ("UI native control status", CheckNativeControlStatus),
            ("Raw input safety state", CheckRawInputSafetyState),
            ("Physical input bridge protocol", CheckPhysicalInputBridgeProtocol),
            ("Windows canonical peer paths", CheckWindowsCanonicalPeerPaths),
            ("Perception frame protocol", CheckPerceptionFrameProtocol),
            ("Bundled Python service launch and restart policy", CheckPythonServiceLaunchConfiguration),
            ("Connector release metadata", CheckConnectorReleaseMetadata),
            ("Connector release download", CheckConnectorReleaseDownload),
            ("Connector archive validation", CheckConnectorArchiveValidation),
            ("Connector install directory lock", CheckConnectorInstallDirectoryLock),
            ("Connector install transaction", CheckConnectorInstallTransaction),
            ("QPC clock origin", CheckQpcClock)
        ];

        int failed = 0;
        foreach (var (name, run) in checks)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {error.Message}");
            }
        }
        Console.WriteLine($"UI checks: {checks.Length - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static int CheckPipeSmoke(string trustedPythonImage)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var client = new UiPipeClient(trustedPythonImage);
        var observed = new TaskCompletionSource<UiStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += status =>
        {
            if (status is not null) observed.TrySetResult(status);
        };
        Task run = client.RunAsync(cancellation.Token);
        try
        {
            UiStatus status = observed.Task.WaitAsync(cancellation.Token).GetAwaiter().GetResult();
            Require(status.State == "fault", "The development service must not report ready or engaged.");
            Console.WriteLine($"PASS UI pipe smoke: {status.ErrorCode}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"FAIL UI pipe smoke: no valid status within five seconds ({client.LastFailure ?? "none"}).");
            return 1;
        }
        finally
        {
            cancellation.Cancel();
            run.GetAwaiter().GetResult();
        }
    }

    private static void CheckResources()
    {
        var manager = new ResourceManager("ETS2AP.Ui.Resources.Strings", typeof(Strings).Assembly);
        ResourceSet resources = manager.GetResourceSet(CultureInfo.InvariantCulture, true, true)
            ?? throw new InvalidOperationException("Default resource set is missing.");
        var keys = resources.Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var properties = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static);
        Require(keys.Count > 0, "No localized strings were found.");
        Require(keys.SetEquals(properties.Select(property => property.Name)), "Resource keys and Strings properties differ.");
        foreach (var property in properties)
        {
            string? value = property.GetValue(null) as string;
            Require(!string.IsNullOrWhiteSpace(value), $"Localized value is empty: {property.Name}");
            Require(value == manager.GetString(property.Name, CultureInfo.InvariantCulture),
                $"Localized property does not match its resource: {property.Name}");
        }
    }

    private static void CheckThemes()
    {
        _ = new Application();
        string[] keys =
        [
            "WindowBrush", "NavigationBrush", "SurfaceBrush", "BorderBrush",
            "PrimaryTextBrush", "SecondaryTextBrush", "AccentBrush", "AccentTextBrush",
            "SelectedBrush", "SelectedTextBrush", "InfoBrush", "InfoTextBrush",
            "DisabledBrush", "DisabledTextBrush"
        ];
        foreach (string theme in new[] { "Light", "Dark", "HighContrast" })
        {
            var uri = new Uri($"/ETS2AP.Ui;component/Themes/{theme}.xaml", UriKind.Relative);
            var resources = Application.LoadComponent(uri) as ResourceDictionary
                ?? throw new InvalidOperationException($"Theme could not load: {theme}");
            foreach (string key in keys)
                Require(resources[key] is SolidColorBrush, $"Theme {theme} has no brush named {key}.");

            if (theme == "HighContrast") continue;
            foreach (var (text, background) in new[]
            {
                ("PrimaryTextBrush", "WindowBrush"),
                ("PrimaryTextBrush", "SurfaceBrush"),
                ("SecondaryTextBrush", "SurfaceBrush"),
                ("SelectedTextBrush", "SelectedBrush"),
                ("InfoTextBrush", "InfoBrush")
            })
            {
                var foreground = ((SolidColorBrush)resources[text]).Color;
                var surface = ((SolidColorBrush)resources[background]).Color;
                Require(Contrast(foreground, surface) >= 4.5,
                    $"Theme {theme} has insufficient contrast for {text} on {background}.");
            }
        }
    }

    private static void CheckPreferences()
    {
        var selected = new UiPreferences { Theme = ThemeChoice.Dark, GameFolder = "sample-game-folder" };
        var restored = UiPreferences.FromJson(selected.ToJson());
        Require(restored.Theme == ThemeChoice.Dark && restored.GameFolder == selected.GameFolder,
            "Preferences did not survive serialization.");
        Require(UiPreferences.FromJson("{invalid").Theme == ThemeChoice.System,
            "Malformed preferences did not fall back to System.");
        Require(UiPreferences.FromJson("{\"Theme\":999}").Theme == ThemeChoice.System,
            "Unknown theme did not fall back to System.");
    }

    private static void CheckNavigation()
    {
        var navigation = new NavigationState();
        Require(navigation.IsSelected(UiPage.Overview), "Overview is not the initial page.");
        foreach (UiPage page in Enum.GetValues<UiPage>())
        {
            navigation.Navigate(page);
            Require(Enum.GetValues<UiPage>().Count(navigation.IsSelected) == 1,
                $"Page {page} did not become the only selected page.");
        }
        foreach (var (key, page) in new[]
        {
            (Key.D1, UiPage.Overview), (Key.D2, UiPage.Setup),
            (Key.D3, UiPage.Settings), (Key.D4, UiPage.Activity),
            (Key.NumPad1, UiPage.Overview), (Key.NumPad4, UiPage.Activity)
        })
            Require(NavigationState.FromShortcut(key, ModifierKeys.Control) == page,
                $"Ctrl+{key} did not select {page}.");
        Require(NavigationState.FromShortcut(Key.D1, ModifierKeys.None) is null,
            "A key without Ctrl unexpectedly navigated.");
        Require(NavigationState.FromShortcut(Key.D5, ModifierKeys.Control) is null,
            "An unassigned shortcut unexpectedly navigated.");
    }

    private static void CheckPerceptionFrameProtocol()
    {
        byte[] rgb = new byte[PerceptionFramePublisher.PixelBytes];
        byte[] packet = PerceptionFramePublisher.CreatePacket(rgb, 1_000_000_000,
            PerceptionFrameFlags.GameFocused | PerceptionFrameFlags.FrameValid, 2);
        Require(packet.Length == PerceptionFramePublisher.PacketSize, "Perception frame size differs from v1.");
        Require(Encoding.ASCII.GetString(packet, 0, 4) == "E2PF", "Perception magic is invalid.");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == 64,
            "Perception header size is invalid.");
        Require(BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(16)) == 2,
            "Perception seqlock sequence is invalid.");
        Require(BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(24)) == 1_000_000_000,
            "Perception timestamp is invalid.");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(40)) == rgb.Length,
            "Perception payload size is invalid.");
        Require(packet.AsSpan(44, 20).IndexOfAnyExcept((byte)0) < 0,
            "Perception reserved bytes must be zero.");
        Require(packet.AsSpan(64).SequenceEqual(rgb), "Perception RGB payload changed.");
    }

    private static void CheckStatusProtocol()
    {
        const string valid = "{\"type\":\"status\",\"version\":\"1.0\",\"id\":\"p-2\",\"timestamp\":\"123\",\"payload\":{\"state\":\"fault\",\"error_code\":\"CONTROL_UNAVAILABLE\",\"state_revision\":0,\"telemetry\":{\"speed_mps\":10,\"speed_limit_mps\":20,\"navigation_distance_m\":1250,\"telemetry_age_s\":0.1}}}";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(valid);
        Require(UiStatusProtocol.ReadType(bytes) == "status", "Valid envelope was not parsed.");
        UiStatus parsed = UiStatusProtocol.ParseStatus(bytes);
        Require(parsed.SpeedMps == 10 && parsed.ErrorCode == "CONTROL_UNAVAILABLE", "Valid status was not parsed.");
        Require(parsed.Control is null, "UI v1.0 must not infer native control status.");
        Require(Reject(valid.Replace("\"speed_mps\":10", "\"speed_mps\":-1")),
            "Negative speed was accepted.");
        Require(Reject(valid.Replace("\"state\":\"fault\"", "\"state\":\"engaged\",\"state\":\"fault\"")),
            "Duplicate state was accepted.");
        Require(Reject(valid.Replace("\"speed_mps\":10", "\"speed_mps\":1e999")),
            "Overflowing number was accepted.");

        const string perception = "{\"type\":\"status\",\"version\":\"1.1\",\"id\":\"p-3\",\"timestamp\":\"124\",\"payload\":{\"state\":\"fault\",\"error_code\":\"CONTROL_UNAVAILABLE\",\"state_revision\":0,\"telemetry\":{\"speed_mps\":10,\"speed_limit_mps\":20,\"navigation_distance_m\":1250,\"telemetry_age_s\":0.1},\"perception\":{\"frame_valid\":true,\"frame_age_s\":0.04,\"game_focused\":true,\"input_monitor_healthy\":false,\"manual_input_active\":false,\"lane_confidence\":0.8,\"front_clear\":null}}}";
        UiStatus withPerception = UiStatusProtocol.ParseStatus(Encoding.UTF8.GetBytes(perception));
        Require(withPerception.Perception is { FrameValid: true, GameFocused: true,
                InputMonitorHealthy: false, ManualInputActive: false, LaneConfidence: 0.8,
                FrontClear: null },
            "UI v1.1 perception safety state was not preserved.");
        Require(withPerception.Control is null, "UI v1.1 must omit native control status.");
    }

    private static void CheckNavigationTimeStatus()
    {
        const string valid = """{"type":"status","version":"1.3","id":"p-3","timestamp":"1100000000","payload":{"state":"fault","error_code":"CONTROL_UNAVAILABLE","state_revision":0,"telemetry":{"speed_mps":10,"speed_limit_mps":20,"navigation_distance_m":1250,"telemetry_age_s":0.1,"navigation_time_s":3723.9,"navigation_time_sample_timestamp_ns":"1000000000"}}}""";
        UiStatus decoded = UiStatusProtocol.ParseStatus(Encoding.UTF8.GetBytes(valid));
        PropertyInfo? timeProperty = typeof(UiStatus).GetProperty("NavigationTimeS");
        Require(timeProperty is not null, "Navigation duration was not exposed.");
        Require(timeProperty!.GetValue(decoded) is double value && value == 3723.9,
            "Navigation duration was not parsed.");
        Type? displayType = typeof(UiStatusProtocol).Assembly.GetType("ETS2AP.Ui.NavigationTimeDisplay");
        Require(displayType is not null, "Navigation duration display state is unavailable.");
        object display = Activator.CreateInstance(displayType!, nonPublic: true)!;
        MethodInfo? setStatus = displayType!.GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo? render = displayType.GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic);
        Require(setStatus is not null && render is not null, "Navigation display methods are unavailable.");
        void Set(UiStatus? item) => setStatus!.Invoke(display, new object?[] { item });
        string? Render(ulong now) => (string?)render!.Invoke(display,
            new object?[] { now, "{0:0}:{1:00}:{2:00}", CultureInfo.InvariantCulture });
        UiStatus Decode(string json) => UiStatusProtocol.ParseStatus(Encoding.UTF8.GetBytes(json));

        Set(decoded);
        Require(Render(1_100_000_000) == "1:02:03", "Fresh navigation time was not formatted.");
        Require(Render(1_200_000_000) == "1:02:03", "Local time must not count down navigation time.");
        Require(Render(1_250_000_000) == "1:02:03", "A 250 ms sample must remain valid.");
        Require(Render(1_250_000_001) is null, "An expired sample remained visible.");
        Require(Render(1_500_000_000) is null,
            "Non-status traffic must not keep old navigation data visible.");

        string newer = valid.Replace("\"timestamp\":\"1100000000\"", "\"timestamp\":\"1210000000\"")
            .Replace("\"navigation_time_s\":3723.9", "\"navigation_time_s\":75")
            .Replace("\"navigation_time_sample_timestamp_ns\":\"1000000000\"",
                     "\"navigation_time_sample_timestamp_ns\":\"1200000000\"");
        Set(Decode(newer));
        Require(Render(1_260_000_000) == "0:01:15",
            "An earlier sample's expiry cleared a newer fresh status.");
        Set(null);
        Require(Render(1_260_000_000) is null, "Disconnect did not clear navigation time.");

        Set(Decode(valid.Replace("\"navigation_time_s\":3723.9", "\"navigation_time_s\":0")));
        Require(Render(1_100_000_000) == "0:00:00", "Zero seconds must remain available.");
        Set(Decode(valid.Replace("\"navigation_time_s\":3723.9", "\"navigation_time_s\":-0.0")));
        Require(Render(1_100_000_000) == "0:00:00", "Negative zero must display as zero seconds.");
        Set(Decode(valid.Replace("\"navigation_time_s\":3723.9", "\"navigation_time_s\":90061")));
        Require(Render(1_100_000_000) == "25:01:01", "Durations over 24 hours were wrapped.");
        Set(Decode(valid.Replace("\"navigation_time_s\":3723.9", "\"navigation_time_s\":1e308")));
        Require(Render(1_100_000_000) is null, "An impractically long duration was not made unavailable.");
        Set(Decode(valid.Replace("\"speed_mps\":10", "\"speed_mps\":null")));
        Require(Render(1_100_000_000) == "1:02:03", "Navigation time incorrectly requires valid speed.");

        string legacy = valid.Replace("\"version\":\"1.3\"", "\"version\":\"1.2\"")
            .Replace(",\"navigation_time_s\":3723.9,\"navigation_time_sample_timestamp_ns\":\"1000000000\"", "");
        Set(Decode(legacy));
        Require(Render(1_100_000_000) is null, "A legacy message without navigation time must be unavailable.");
        Set(Decode(valid.Replace("\"navigation_time_s\":3723.9", "\"navigation_time_s\":-1")));
        Require(Render(1_100_000_000) is null, "A negative duration was displayed.");
        Set(Decode(valid.Replace("\"navigation_time_s\":3723.9", "\"navigation_time_s\":\"bad\"")));
        Require(Render(1_100_000_000) is null, "A nonnumeric duration was displayed.");
        Set(Decode(valid.Replace("\"navigation_time_sample_timestamp_ns\":\"1000000000\"",
                                 "\"navigation_time_sample_timestamp_ns\":\"bad\"")));
        Require(Render(1_100_000_000) is null, "An invalid sample timestamp was displayed.");
        foreach (string invalidSample in new[] { "null", "\"0\"", "\"01\"", "\"18446744073709551616\"" })
        {
            Set(Decode(valid.Replace("\"navigation_time_sample_timestamp_ns\":\"1000000000\"",
                                     $"\"navigation_time_sample_timestamp_ns\":{invalidSample}")));
            Require(Render(1_100_000_000) is null, "An invalid sample timestamp was displayed.");
        }
        Set(Decode(valid.Replace(",\"navigation_time_sample_timestamp_ns\":\"1000000000\"", "")));
        Require(Render(1_100_000_000) is null, "A missing sample timestamp was displayed.");
        Set(Decode(valid.Replace("\"navigation_time_sample_timestamp_ns\":\"1000000000\"",
                                 "\"navigation_time_sample_timestamp_ns\":\"1200000000\"")));
        Require(Render(1_300_000_000) is null, "A sample newer than its message was displayed.");
        Set(Decode(valid.Replace("\"timestamp\":\"1100000000\"", "\"timestamp\":\"1400000000\"")
                        .Replace("\"navigation_time_sample_timestamp_ns\":\"1000000000\"",
                                 "\"navigation_time_sample_timestamp_ns\":\"1200000000\"")));
        Require(Render(1_300_000_000) is null, "A message from the future was displayed.");
        Require(Reject(valid.Replace("\"timestamp\":\"1100000000\"", "\"timestamp\":\"-1\"")),
            "An invalid envelope timestamp was accepted.");
    }

    private static void CheckUiQpcNanoseconds()
    {
        long frequency = Stopwatch.Frequency;
        long before = Stopwatch.GetTimestamp();
        ulong observed = UiPipeClient.QpcNanosecondsValue();
        long after = Stopwatch.GetTimestamp();
        ulong lower = (ulong)decimal.Floor((decimal)before * 1_000_000_000m / frequency);
        ulong upper = (ulong)decimal.Floor((decimal)after * 1_000_000_000m / frequency);
        Require(observed >= lower && observed <= upper, "UI QPC nanoseconds do not share the Stopwatch clock.");
    }

    private static void CheckUiV13HelloAck()
    {
        foreach (string version in new[] { "1.3", "1.2", "1.1", "1.0" })
        {
            string ack = "{\"type\":\"hello_ack\",\"version\":\"" + version +
                "\",\"id\":\"u-4\",\"timestamp\":\"125\",\"payload\":{\"selected_version\":\"" +
                version + "\"}}";
            bool accepted;
            try { UiStatusProtocol.ValidateHelloAck(Encoding.UTF8.GetBytes(ack), "u-4"); accepted = true; }
            catch (InvalidDataException) { accepted = false; }
            Require(accepted, "A supported UI handshake version was rejected: " + version);
        }
    }

    private static void CheckUiV12HelloAck()
    {
        const string helloAck = "{\"type\":\"hello_ack\",\"version\":\"1.2\",\"id\":\"u-4\",\"timestamp\":\"125\",\"payload\":{\"selected_version\":\"1.2\"}}";
        UiStatusProtocol.ValidateHelloAck(Encoding.UTF8.GetBytes(helloAck), "u-4");
        const string legacyHelloAck = "{\"type\":\"hello_ack\",\"version\":\"1.1\",\"id\":\"u-5\",\"timestamp\":\"126\",\"payload\":{\"selected_version\":\"1.1\"}}";
        UiStatusProtocol.ValidateHelloAck(Encoding.UTF8.GetBytes(legacyHelloAck), "u-5");
    }

    private static void CheckNativeControlStatus()
    {
        const string status = "{\"type\":\"status\",\"version\":\"1.2\",\"id\":\"p-4\",\"timestamp\":\"126\",\"payload\":{\"state\":\"fault\",\"error_code\":\"CONTROL_UNAVAILABLE\",\"state_revision\":0,\"telemetry\":{\"speed_mps\":10,\"speed_limit_mps\":20,\"navigation_distance_m\":1250,\"telemetry_age_s\":0.1},\"control\":{\"connection\":\"connected\",\"state\":\"engaged\",\"reason\":\"none\"}}}";
        UiStatus parsed = UiStatusProtocol.ParseStatus(Encoding.UTF8.GetBytes(status));
        PropertyInfo? controlProperty = typeof(UiStatus).GetProperty("Control");
        Require(controlProperty is not null, "UI status must expose native Control.v1 state.");
        object? control = controlProperty!.GetValue(parsed);
        Require(control is not null, "Native Control.v1 status was not parsed.");
        Type type = control!.GetType();
        Require(type.GetProperty("Connection")?.GetValue(control) as string == "connected" &&
                type.GetProperty("State")?.GetValue(control) as string == "engaged" &&
                type.GetProperty("Reason")?.GetValue(control) as string == "none",
            "Native connection, state, and reason were not preserved.");
        Require(Reject(status.Replace("\"state\":\"engaged\"", "\"state\":\"unknown\"")),
            "An unknown native state was accepted.");
        Require(Reject(status.Replace("\"connection\":\"connected\"", "\"connection\":\"disconnected\"")),
            "A disconnected channel with a native state was accepted.");
    }

    private static void CheckRawInputSafetyState()
    {
        const uint leftButtonDown = 0x0001;
        const uint leftButtonUp = 0x0002;
        uint buttons = InputMonitorSafety.ApplyMouseButtonFlags(0, leftButtonDown);
        Require(buttons == leftButtonDown, "Mouse down was not retained.");
        buttons = InputMonitorSafety.ApplyMouseButtonFlags(buttons, leftButtonUp);
        Require(buttons == 0, "Mouse up did not clear its matching down state.");
        Require(!InputMonitorSafety.IsHealthy(true, true, false, false),
            "API health alone must not authorize unvalidated physical-input coverage.");
        Require(!InputMonitorSafety.IsHealthy(true, true, true, true),
            "An unclassified HID device must keep physical-input coverage unhealthy.");
        Require(InputMonitorSafety.IsHealthy(true, true, true, false),
            "Validated monitor coverage with no unknown device should be healthy.");
    }

    private static void CheckPhysicalInputBridgeProtocol()
    {
        byte[] frame = PhysicalInputPipeProtocol.CreateFrame(7, true, false);
        Require(frame.Length == PhysicalInputPipeProtocol.FrameSize,
            "The physical-input frame has an unexpected size.");
        Require(frame.AsSpan(0, 4).SequenceEqual("E2PI"u8),
            "The physical-input frame magic is invalid.");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)) == 1,
            "The physical-input frame version is invalid.");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) == 1,
            "The available idle frame flags are invalid.");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)) == frame.Length,
            "The physical-input frame length field is invalid.");
        Require(BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(12)) == 7,
            "The physical-input frame sequence is invalid.");

        byte[] unavailable = PhysicalInputPipeProtocol.CreateFrame(8, false, false);
        Require(BinaryPrimitives.ReadUInt16LittleEndian(unavailable.AsSpan(6)) == 2,
            "An unavailable input source must be reported fail-closed.");

        var keys = new RawKeyboardState();
        keys.SetKey((nint)1, 0x41, true);
        keys.SetKey((nint)2, 0x41, true);
        keys.SetKey((nint)1, 0x41, false);
        Require(keys.IsActive, "Releasing one device must not clear another device's held key.");
        keys.RemoveDevice((nint)2);
        Require(!keys.IsActive, "Removing a keyboard must clear its held keys.");

        var mice = new RawMouseButtonState();
        mice.Apply((nint)1, 0x0001);
        mice.Apply((nint)2, 0x0001);
        mice.Apply((nint)1, 0x0002);
        Require(mice.IsActive, "Releasing one mouse must not clear another device's held button.");
        mice.RemoveDevice((nint)2);
        Require(!mice.IsActive, "Removing a mouse must clear its held buttons.");
    }

    private static void CheckWindowsCanonicalPeerPaths()
    {
        string imagePath = typeof(Program).Assembly.Location;
        string? physicalInputPath = PhysicalInputPipeClient.CanonicalPathOrNull(imagePath);
        Require(!string.IsNullOrWhiteSpace(physicalInputPath),
            "The physical-input peer image path must be canonicalizable on the workspace volume.");
        Require(UiPipeClient.CanonicalPath(imagePath) == physicalInputPath,
            "WPF named-pipe clients must use the same canonical path representation.");
    }

    private static void CheckPythonServiceLaunchConfiguration()
    {
        string installRoot = Path.Combine(Path.GetTempPath(), "ETS2AP-install");
        string uiExecutable = Path.Combine(installRoot, "ETS2AP.Ui.exe");
        ProcessStartInfo start = PythonServiceLauncher.CreateStartInfo(installRoot, uiExecutable);
        Require(start.FileName == Path.GetFullPath(Path.Combine(installRoot, "runtime", "python.exe")),
            "The service must use the bundled runtime under the fixed install root.");
        Require(start.WorkingDirectory == Path.GetFullPath(installRoot),
            "The service working directory must be the app install root.");
        Require(!start.UseShellExecute && start.CreateNoWindow && start.WindowStyle == ProcessWindowStyle.Hidden,
            "The service must launch without a shell or visible console window.");
        Require(start.ArgumentList.SequenceEqual(new[]
        {
            "-I", "-S", "-m", "ets2ap.ui_server", "--trusted-ui-image", Path.GetFullPath(uiExecutable)
        }), "The service must receive fixed module arguments and the canonical UI image path separately.");

        var restartPolicy = new PythonServiceRestartPolicy();
        Require(restartPolicy.TryConsumeRestart(), "One automatic restart should be allowed after service exit.");
        Require(!restartPolicy.TryConsumeRestart(), "Automatic restarts must be bounded to avoid a crash loop.");
    }

    private static void CheckQpcClock()
    {
        Require(QueryPerformanceFrequency(out long frequency) && frequency > 0, "QPC frequency unavailable.");
        Require(QueryPerformanceCounter(out long before), "QPC unavailable.");
        long observed = System.Diagnostics.Stopwatch.GetTimestamp();
        Require(QueryPerformanceCounter(out long after), "QPC unavailable.");
        Require(observed >= before && observed <= after, "Stopwatch does not share the QPC origin.");
    }

    private static void CheckConnectorReleaseMetadata()
    {
        const string digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string json = JsonSerializer.Serialize(new object[]
        {
            new { draft = true, tag_name = "v2.0.0", assets = Array.Empty<object>() },
            new
            {
                draft = false,
                tag_name = "v1.2.3-beta.1",
                assets = new[]
                {
                    new
                    {
                        name = "ETS2AP_DLL_V1.2.3-beta.1.zip",
                        browser_download_url = "https://github.com/KingsleyHK0323/Euro_Truck_Simulator_2_Auto_Pilot/releases/download/v1.2.3-beta.1/ETS2AP_DLL_V1.2.3-beta.1.zip",
                        size = 2048,
                        digest
                    }
                }
            }
        });
        var selected = ConnectorReleaseAssetSelector.SelectLatest(json);
        Require(selected.Version == "1.2.3-beta.1", "Prerelease version was not selected from the newest published release.");
        Require(selected.Sha256 == digest["sha256:".Length..], "Release SHA-256 digest was not parsed.");
        Require(selected.Size == 2048, "Release asset size was not retained.");

        string missingDigest = json.Replace(digest, string.Empty, StringComparison.Ordinal);
        Require(RejectRelease(missingDigest), "An asset without a SHA-256 digest was accepted.");
        string wrongHost = json.Replace("https://github.com/KingsleyHK0323/", "https://attacker.example/SpaceSquare640/", StringComparison.Ordinal);
        Require(RejectRelease(wrongHost), "A release asset from an untrusted host was accepted.");
        string wrongHostOnly = json.Replace("https://github.com/KingsleyHK0323/",
            "https://attacker.example/KingsleyHK0323/", StringComparison.Ordinal);
        Require(RejectRelease(wrongHostOnly),
            "A release asset from an untrusted host with the expected repository path was accepted.");
        string insecureScheme = json.Replace("https://github.com/KingsleyHK0323/",
            "http://github.com/KingsleyHK0323/", StringComparison.Ordinal);
        Require(RejectRelease(insecureScheme), "A release asset without HTTPS was accepted.");
        string wrongRepository = json.Replace("https://github.com/KingsleyHK0323/", "https://github.com/UntrustedOwner/", StringComparison.Ordinal);
        Require(RejectRelease(wrongRepository), "A release asset from an untrusted repository was accepted.");
        string mismatchedName = json.Replace("ETS2AP_DLL_V1.2.3-beta.1.zip", "ETS2AP_DLL_V9.9.9.zip", StringComparison.Ordinal);
        Require(RejectRelease(mismatchedName), "An asset name that does not match the release tag was accepted.");
    }

    private static void CheckConnectorArchiveValidation()
    {
        byte[] archive = BuildConnectorArchive("1.2.3-beta.1");
        var files = ConnectorPackage.ReadValidated(archive, "1.2.3-beta.1");
        Require(files.Count == 1 && files.ContainsKey("ets2ap_plugin.dll"), "Valid connector DLL was not extracted.");

        byte[] licensedArchive = BuildConnectorArchive("1.2.3-beta.1", includeSdkLicense: true);
        var licensedFiles = ConnectorPackage.ReadValidated(licensedArchive, "1.2.3-beta.1");
        Require(licensedFiles.Count == 1 && licensedFiles.ContainsKey("ets2ap_plugin.dll"),
            "A connector package with its declared SCS SDK license was rejected or installed as a DLL.");
        Require(RejectPackage(BuildConnectorArchive("1.2.3-beta.1", includeSdkLicense: true, tamperSdkLicense: true),
            "1.2.3-beta.1"), "A package with a mismatched third-party license digest was accepted.");

        Require(RejectPackage(BuildConnectorArchive("1.2.3"), "1.2.3-beta.1"),
            "A package for a different version was accepted.");
        Require(RejectPackage(BuildConnectorArchive("1.2.3-beta.1", dllContent: [0x4d, 0x5a], declaredHash: new string('0', 64)), "1.2.3-beta.1"),
            "A package with a mismatched DLL digest was accepted.");
        Require(RejectPackage(BuildConnectorArchive("1.2.3-beta.1", entryPath: "plugins/../escape.dll"), "1.2.3-beta.1"),
            "A package containing a traversal path was accepted.");
        Require(RejectPackage(BuildConnectorArchive("1.2.3-beta.1", name: "untrusted.dll"), "1.2.3-beta.1"),
            "A package containing an unapproved DLL name was accepted.");
    }

    private static void CheckConnectorReleaseDownload()
    {
        byte[] archive = BuildConnectorArchive("1.2.3-beta.1");
        string digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        string releaseJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                draft = false,
                tag_name = "v1.2.3-beta.1",
                assets = new[]
                {
                    new
                    {
                        name = "ETS2AP_DLL_V1.2.3-beta.1.zip",
                        browser_download_url = "https://github.com/KingsleyHK0323/Euro_Truck_Simulator_2_Auto_Pilot/releases/download/v1.2.3-beta.1/ETS2AP_DLL_V1.2.3-beta.1.zip",
                        size = archive.Length,
                        digest = "sha256:" + digest
                    }
                }
            }
        });
        using var handler = new FakeReleaseHandler(releaseJson, archive);
        using var client = new HttpClient(handler);
        var downloader = new ConnectorReleaseClient(client);
        DownloadedConnectorPackage package = downloader.DownloadLatestAsync(CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(package.Version == "1.2.3-beta.1" && package.Sha256 == digest,
            "Downloaded connector metadata did not match the GitHub release.");
        Require(package.Archive.SequenceEqual(archive), "Verified connector ZIP was not retained unchanged in memory.");
        Require(handler.RequestCount == 2, "The connector download did not make exactly two bounded GitHub requests.");

        string invalidRelease = releaseJson.Replace(digest, new string('0', 64), StringComparison.Ordinal);
        using var badHandler = new FakeReleaseHandler(invalidRelease, archive);
        using var badClient = new HttpClient(badHandler);
        var badDownloader = new ConnectorReleaseClient(badClient);
        bool rejected = false;
        try
        {
            _ = badDownloader.DownloadLatestAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Connector ZIP with a mismatched release digest was accepted.");
    }

    private static void CheckConnectorInstallDirectoryLock()
    {
        string parent = Path.Combine(Environment.CurrentDirectory, "build");
        Directory.CreateDirectory(parent);
        string root = Path.Combine(parent, $"ets2ap-lock-{Guid.NewGuid():N}");
        string moved = root + ".moved";
        Directory.CreateDirectory(Path.Combine(root, "bin", "win_x64", "plugins"));
        try
        {
            using (ConnectorInstaller.LockDirectoryTreeForChecks(Path.Combine(root, "bin", "win_x64", "plugins")))
            {
                bool renameBlocked = false;
                try { Directory.Move(root, moved); }
                catch (IOException) { renameBlocked = true; }
                catch (UnauthorizedAccessException) { renameBlocked = true; }
                Require(renameBlocked, "A locked installation path could still be renamed.");
            }
            Directory.Move(root, moved);
            Require(Directory.Exists(moved), "The directory chain stayed locked after lease disposal.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(moved)) Directory.Delete(moved, recursive: true);
        }
    }

    private static void CheckConnectorInstallTransaction()
    {
        MethodInfo? installMethod = typeof(ConnectorInstaller).GetMethod(
            "InstallPackageForChecks", BindingFlags.Static | BindingFlags.NonPublic);
        if (installMethod is null)
            throw new InvalidOperationException("Connector installer has no isolated package-install path for fixture checks.");

        string parent = Path.Combine(Environment.CurrentDirectory, "build");
        Directory.CreateDirectory(parent);
        string gameRoot = Path.Combine(parent, $"ets2ap-install-{Guid.NewGuid():N}");
        string win64 = Path.Combine(gameRoot, "bin", "win_x64");
        string pluginDirectory = Path.Combine(win64, "plugins");

        byte[] originalPlugin = [0x4d, 0x5a, 0x01];
        byte[] originalInput = [0x4d, 0x5a, 0x02];
        byte[] firstPlugin = [0x4d, 0x5a, 0x11];
        byte[] firstInput = [0x4d, 0x5a, 0x12];
        string pluginPath = Path.Combine(pluginDirectory, "ets2ap_plugin.dll");
        string inputPath = Path.Combine(pluginDirectory, "ets2ap_input.dll");

        try
        {
            Directory.CreateDirectory(pluginDirectory);
            File.WriteAllBytes(Path.Combine(win64, "eurotrucks2.exe"), [0x4d, 0x5a]);
            File.WriteAllBytes(pluginPath, originalPlugin);
            File.WriteAllBytes(inputPath, originalInput);

            byte[] firstArchive = BuildConnectorArchive("1.2.3",
                dllContent: firstPlugin, inputDllContent: firstInput);
            var firstPackage = new DownloadedConnectorPackage("1.2.3",
                Convert.ToHexString(SHA256.HashData(firstArchive)).ToLowerInvariant(), firstArchive);
            InvokeConnectorInstallForChecks(installMethod, gameRoot, firstPackage, static () => false);

            Require(File.ReadAllBytes(pluginPath).SequenceEqual(firstPlugin),
                "The main connector DLL was not installed into the fixture game folder.");
            Require(File.ReadAllBytes(inputPath).SequenceEqual(firstInput),
                "The input connector DLL was not installed into the fixture game folder.");

            string backupRoot = Directory.EnumerateDirectories(win64, "ETS2AP_Backups_*").Single();
            Require(File.ReadAllBytes(Path.Combine(backupRoot, "ets2ap_plugin.dll")).SequenceEqual(originalPlugin),
                "The previous plugin DLL was not backed up intact.");
            Require(File.ReadAllBytes(Path.Combine(backupRoot, "ets2ap_input.dll")).SequenceEqual(originalInput),
                "The previous input DLL was not backed up intact.");

            InvokeConnectorInstallForChecks(installMethod, gameRoot, firstPackage, static () => false);
            Require(Directory.EnumerateDirectories(win64, "ETS2AP_Backups_*").Count() == 1,
                "Reinstalling identical DLLs created an unnecessary backup.");

            byte[] updatedPlugin = [0x4d, 0x5a, 0x21];
            byte[] updatedInput = [0x4d, 0x5a, 0x22];
            byte[] updateArchive = BuildConnectorArchive("1.2.4",
                dllContent: updatedPlugin, inputDllContent: updatedInput);
            var updatePackage = new DownloadedConnectorPackage("1.2.4",
                Convert.ToHexString(SHA256.HashData(updateArchive)).ToLowerInvariant(), updateArchive);
            int gameRunningChecks = 0;
            bool interrupted = false;
            try
            {
                InvokeConnectorInstallForChecks(installMethod, gameRoot, updatePackage,
                    () => ++gameRunningChecks == 2);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                interrupted = true;
            }

            Require(interrupted && gameRunningChecks == 2,
                "The install did not stop when the game became active during a multi-DLL update.");
            Require(File.ReadAllBytes(pluginPath).SequenceEqual(firstPlugin) &&
                File.ReadAllBytes(inputPath).SequenceEqual(firstInput),
                "A failed multi-DLL update did not restore the previously installed connector files.");
            Require(!Directory.EnumerateFiles(pluginDirectory, "*.tmp").Any(),
                "A failed connector update left temporary files behind.");
        }
        finally
        {
            if (Directory.Exists(gameRoot)) Directory.Delete(gameRoot, recursive: true);
        }
    }

    private static void InvokeConnectorInstallForChecks(MethodInfo installMethod, string gameRoot,
        DownloadedConnectorPackage package, Func<bool> isGameRunning) =>
        installMethod.Invoke(null, [gameRoot, package, isGameRunning]);

    private static bool RejectRelease(string json)
    {
        try
        {
            _ = ConnectorReleaseAssetSelector.SelectLatest(json);
            return false;
        }
        catch (InvalidDataException) { return true; }
    }

    private static bool RejectPackage(byte[] archive, string version)
    {
        try
        {
            _ = ConnectorPackage.ReadValidated(archive, version);
            return false;
        }
        catch (InvalidDataException) { return true; }
    }

    private static byte[] BuildConnectorArchive(
        string version,
        string name = "ets2ap_plugin.dll",
        string entryPath = "plugins/ets2ap_plugin.dll",
        byte[]? dllContent = null,
        byte[]? inputDllContent = null,
        string? declaredHash = null,
        bool includeSdkLicense = false,
        bool tamperSdkLicense = false)
    {
        dllContent ??= [0x4d, 0x5a, 0x01, 0x02];
        string hash = declaredHash ?? Convert.ToHexString(SHA256.HashData(dllContent)).ToLowerInvariant();
        var manifestFields = new Dictionary<string, object>
        {
            ["format"] = 1,
            ["version"] = version,
            ["files"] = new List<object> { new { name, sha256 = hash } }
        };
        if (inputDllContent is not null)
        {
            ((List<object>)manifestFields["files"]).Add(new
            {
                name = "ets2ap_input.dll",
                sha256 = Convert.ToHexString(SHA256.HashData(inputDllContent)).ToLowerInvariant()
            });
        }
        byte[]? sdkLicenseBytes = null;
        byte[]? sdkLicenseArchiveBytes = null;
        if (includeSdkLicense)
        {
            sdkLicenseBytes = Encoding.UTF8.GetBytes("SCS SDK\nCopyright (C) 2016 SCS Software\n\nPermission notice.\n");
            sdkLicenseArchiveBytes = tamperSdkLicense
                ? Encoding.UTF8.GetBytes("altered license text")
                : sdkLicenseBytes;
            string sdkLicenseHash = Convert.ToHexString(SHA256.HashData(sdkLicenseBytes)).ToLowerInvariant();
            manifestFields["third_party_licenses"] = new[]
            {
                new { path = "licenses/scs-sdk-mit.txt", sha256 = sdkLicenseHash }
            };
        }
        string manifest = JsonSerializer.Serialize(manifestFields);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
                writer.Write(manifest);
            using (var dll = zip.CreateEntry(entryPath).Open())
                dll.Write(dllContent);
            if (inputDllContent is not null)
            {
                using var inputDll = zip.CreateEntry("plugins/ets2ap_input.dll").Open();
                inputDll.Write(inputDllContent);
            }
            if (sdkLicenseArchiveBytes is not null)
            {
                using var license = zip.CreateEntry("licenses/scs-sdk-mit.txt").Open();
                license.Write(sdkLicenseArchiveBytes);
            }
        }
        return output.ToArray();
    }

    private sealed class FakeReleaseHandler(string releaseJson, byte[] archive) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
                Require(request.RequestUri?.AbsoluteUri ==
                    "https://api.github.com/repos/KingsleyHK0323/Euro_Truck_Simulator_2_Auto_Pilot/releases?per_page=30",
                    "Connector metadata request targeted an unexpected repository.");
            HttpContent content = request.RequestUri?.Host == "api.github.com"
                ? new StringContent(releaseJson, Encoding.UTF8, "application/json")
                : new ByteArrayContent(archive);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryPerformanceCounter(out long value);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryPerformanceFrequency(out long value);

    private static bool Reject(string json)
    {
        try
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            _ = UiStatusProtocol.ReadType(bytes);
            _ = UiStatusProtocol.ParseStatus(bytes);
            return false;
        }
        catch (InvalidDataException) { return true; }
    }

    private static double Contrast(Color first, Color second)
    {
        double a = Luminance(first);
        double b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte component)
        {
            double value = component / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
