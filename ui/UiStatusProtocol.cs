// Edited by: OpenAI-Codex_Responsible: Source_Code
// Edited by: OpenAI-Codex_Source_Code
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ETS2AP.Ui;

internal sealed record UiStatus(string State, string? ErrorCode, long Revision,
    double? SpeedMps, double? SpeedLimitMps, double? DistanceM, double? AgeS,
    PerceptionStatus? Perception, NativeControlStatus? Control,
    ulong TimestampNs, double? NavigationTimeS, ulong? NavigationTimeSampleTimestampNs);

internal sealed record PerceptionStatus(bool FrameValid, double? FrameAgeS,
    bool GameFocused, bool InputMonitorHealthy, bool ManualInputActive,
    double? LaneConfidence, bool? FrontClear);

internal sealed record NativeControlStatus(string Connection, string? State, string? Reason);

internal static class UiStatusProtocol
{
    internal const int MaxFrameBytes = 65_536;

    internal static string ReadType(ReadOnlySpan<byte> json)
    {
        if (json.Length is < 1 or > MaxFrameBytes) throw new InvalidDataException("Invalid UI frame length.");
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        CheckObjects(root);
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid UI envelope.");
        string type = RequiredString(root, "type");
        string version = RequiredString(root, "version");
        string id = RequiredString(root, "id");
        string timestamp = RequiredString(root, "timestamp");
        if (type.Length > 64 || version.Length > 16 || !version.Contains('.') ||
            id.Length is < 1 or > 64 || !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ||
            !(id.StartsWith("u-", StringComparison.Ordinal) || id.StartsWith("p-", StringComparison.Ordinal)) ||
            !ulong.TryParse(timestamp, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _) ||
            (timestamp.Length > 1 && timestamp[0] == '0') ||
            root.GetProperty("payload").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid UI envelope.");
        return type;
    }

    internal static UiStatus ParseStatus(ReadOnlySpan<byte> json)
    {
        if (json.Length is < 1 or > MaxFrameBytes) throw new InvalidDataException("Invalid UI frame length.");
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        CheckObjects(document.RootElement);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || RequiredString(root, "type") != "status" ||
            !RequiredString(root, "version").StartsWith("1.", StringComparison.Ordinal) ||
            !RequiredString(root, "id").StartsWith("p-", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid status envelope.");
        string timestamp = RequiredString(root, "timestamp");
        if (!ulong.TryParse(timestamp, NumberStyles.None,
                CultureInfo.InvariantCulture, out ulong timestampNs) ||
            (timestamp.Length > 1 && timestamp[0] == '0'))
            throw new InvalidDataException("Invalid status timestamp.");
        JsonElement payload = root.GetProperty("payload");
        string state = RequiredString(payload, "state");
        if (state is not ("ready" or "engaged" or "manual_override" or "fault"))
            throw new InvalidDataException("Invalid control state.");
        JsonElement error = payload.GetProperty("error_code");
        string? code = error.ValueKind == JsonValueKind.Null ? null : error.GetString();
        long revision = payload.GetProperty("state_revision").GetInt64();
        if (revision < 0) throw new InvalidDataException("Invalid state revision.");
        JsonElement telemetry = payload.GetProperty("telemetry");
        PerceptionStatus? perception = null;
        NativeControlStatus? control = null;
        if (payload.TryGetProperty("perception", out JsonElement perceptionElement))
        {
            bool frameValid = RequiredBoolean(perceptionElement, "frame_valid");
            double? frameAge = OptionalNullableNonnegative(perceptionElement, "frame_age_s");
            bool gameFocused = RequiredBoolean(perceptionElement, "game_focused");
            bool monitorHealthy = RequiredBoolean(perceptionElement, "input_monitor_healthy");
            bool manualInput = RequiredBoolean(perceptionElement, "manual_input_active");
            double? confidence = OptionalNullableNonnegative(perceptionElement, "lane_confidence");
            if (confidence is > 1.0)
                throw new InvalidDataException("Invalid lane confidence.");
            bool? frontClear = OptionalNullableBoolean(perceptionElement, "front_clear");
            perception = new PerceptionStatus(frameValid, frameAge, gameFocused,
                monitorHealthy, manualInput, confidence, frontClear);
        }
        if (payload.TryGetProperty("control", out JsonElement controlElement))
        {
            if (controlElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid native control status.");
            string connection = RequiredString(controlElement, "connection");
            string? nativeState = OptionalNullableString(controlElement, "state");
            string? reason = OptionalNullableString(controlElement, "reason");
            if (connection is not ("not_configured" or "disconnected" or "connected") ||
                nativeState is not null and not ("disengaged" or "engaged" or
                    "manual_override_latched" or "timeout" or "rejected") ||
                reason is not null and not ("none" or "manual_input" or "command_timeout" or
                    "malformed_packet" or "version_mismatch" or "client_disconnected" or
                    "telemetry_unavailable") ||
                (connection == "connected" && (nativeState is null || reason is null)) ||
                (connection != "connected" && (nativeState is not null || reason is not null)))
                throw new InvalidDataException("Invalid native control status.");
            control = new NativeControlStatus(connection, nativeState, reason);
        }
        return new UiStatus(state, code, revision,
            OptionalNonnegative(telemetry, "speed_mps"),
            OptionalNonnegative(telemetry, "speed_limit_mps"),
            OptionalNonnegative(telemetry, "navigation_distance_m"),
            OptionalNonnegative(telemetry, "telemetry_age_s"), perception, control,
            timestampNs, OptionalNavigationTime(telemetry),
            OptionalNavigationTimestamp(telemetry));
    }

    internal static void ValidateHelloAck(ReadOnlySpan<byte> json, string requestId)
    {
        if (json.Length is < 1 or > MaxFrameBytes) throw new InvalidDataException("Invalid UI frame length.");
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        CheckObjects(document.RootElement);
        JsonElement root = document.RootElement;
        if (RequiredString(root, "type") != "hello_ack" || RequiredString(root, "id") != requestId ||
            RequiredString(root, "version") != RequiredString(root.GetProperty("payload"), "selected_version") ||
            RequiredString(root.GetProperty("payload"), "selected_version") is not ("1.0" or "1.1" or "1.2" or "1.3"))
            throw new InvalidDataException("Invalid UI handshake.");
    }

    private static double? OptionalNavigationTime(JsonElement telemetry)
    {
        if (!telemetry.TryGetProperty("navigation_time_s", out JsonElement field) ||
            field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out double value) ||
            !double.IsFinite(value) || value < 0)
            return null;
        return value;
    }

    private static ulong? OptionalNavigationTimestamp(JsonElement telemetry)
    {
        if (!telemetry.TryGetProperty("navigation_time_sample_timestamp_ns", out JsonElement field) ||
            field.ValueKind != JsonValueKind.String)
            return null;
        string? text = field.GetString();
        if (string.IsNullOrEmpty(text) || (text.Length > 1 && text[0] == '0') ||
            !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong timestamp) ||
            timestamp == 0)
            return null;
        return timestamp;
    }
    private static double? OptionalNonnegative(JsonElement value, string name)
    {
        JsonElement field = value.GetProperty(name);
        if (field.ValueKind == JsonValueKind.Null) return null;
        double number = field.GetDouble();
        if (!double.IsFinite(number) || number < 0) throw new InvalidDataException("Invalid telemetry value.");
        return number;
    }

    private static double? OptionalNullableNonnegative(JsonElement value, string name)
    {
        JsonElement field = value.GetProperty(name);
        if (field.ValueKind == JsonValueKind.Null) return null;
        double number = field.GetDouble();
        if (!double.IsFinite(number) || number < 0) throw new InvalidDataException("Invalid perception value.");
        return number;
    }

    private static bool RequiredBoolean(JsonElement value, string name)
    {
        JsonElement field = value.GetProperty(name);
        return field.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Invalid perception flag.")
        };
    }

    private static bool? OptionalNullableBoolean(JsonElement value, string name)
    {
        JsonElement field = value.GetProperty(name);
        return field.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException("Invalid perception classification.")
        };
    }

    private static string? OptionalNullableString(JsonElement value, string name)
    {
        JsonElement field = value.GetProperty(name);
        return field.ValueKind == JsonValueKind.Null ? null : RequiredString(value, name);
    }

    private static string RequiredString(JsonElement value, string name)
    {
        string? result = value.GetProperty(name).GetString();
        return string.IsNullOrWhiteSpace(result) ? throw new InvalidDataException("Missing UI field.") : result;
    }

    private static void CheckObjects(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || names.Count > 64)
                    throw new InvalidDataException("Duplicate or excessive JSON members.");
                CheckObjects(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray()) CheckObjects(item);
        }
        else if (value.ValueKind == JsonValueKind.Number &&
                 (!value.TryGetDouble(out double number) || !double.IsFinite(number)))
            throw new InvalidDataException("Nonfinite JSON number.");
    }
}

internal sealed class NavigationTimeDisplay
{
    private const ulong MaximumSampleAgeNs = 250_000_000;
    private const int MaximumDisplayCharacters = 32;
    private UiStatus? _status;

    internal void SetStatus(UiStatus? status) => _status = status;

    internal string? Render(ulong nowNs, string format, CultureInfo culture)
    {
        UiStatus? status = _status;
        if (status is null || status.NavigationTimeS is not double seconds ||
            !double.IsFinite(seconds) || seconds < 0 ||
            status.AgeS is not double age || !double.IsFinite(age) || age < 0 || age > 0.25 ||
            status.NavigationTimeSampleTimestampNs is not ulong sampleNs || sampleNs == 0 ||
            status.TimestampNs == 0 || status.TimestampNs > nowNs || sampleNs > status.TimestampNs ||
            nowNs - sampleNs > MaximumSampleAgeNs)
            return null;

        double wholeSeconds = seconds == 0 ? 0.0 : Math.Floor(seconds);
        double hours = Math.Floor(wholeSeconds / 3600.0);
        double remainder = wholeSeconds % 3600.0;
        string formatted = string.Format(culture, format, hours, (int)(remainder / 60.0), (int)(remainder % 60.0));
        return formatted.Length <= MaximumDisplayCharacters ? formatted : null;
    }
}
