// Edited by: OpenAI-Codex_Responsible: Source_Code
// Edited by: OpenAI-Codex_Source_Code
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace ETS2AP.Ui;

internal sealed class UiPipeClient
{
    private const string PipeName = "ETS2AP_UI_v1";
    private readonly string _trustedPythonImage;

    internal event Action<UiStatus?>? StatusChanged;
    internal string? LastFailure { get; private set; }

    internal UiPipeClient(string trustedPythonImage)
    {
        _trustedPythonImage = CanonicalPath(trustedPythonImage);
    }

    internal async Task RunAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1000, cancellation);
                if (!VerifyServer(pipe)) throw new UnauthorizedAccessException("Untrusted UI pipe server.");
                string helloId = "u-1";
                await SendAsync(pipe, new { type = "hello", version = "1.3", id = helloId,
                    timestamp = QpcNanoseconds(), payload = new { supported_versions = new[] { "1.3", "1.2", "1.1", "1.0" } } }, cancellation);
                UiStatusProtocol.ValidateHelloAck(await ReadAsync(pipe, cancellation), helloId);
                while (!cancellation.IsCancellationRequested)
                {
                    byte[] data = await ReadAsync(pipe, cancellation);
                    string type = UiStatusProtocol.ReadType(data);
                    if (type == "status") StatusChanged?.Invoke(UiStatusProtocol.ParseStatus(data));
                    else if (type == "config") continue;
                    else if (type == "error") throw new InvalidDataException("UI service error.");
                }
            }
            catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or
                InvalidDataException or JsonException or OperationCanceledException or InvalidOperationException or
                KeyNotFoundException or FormatException or OverflowException)
            {
                LastFailure = error.GetType().Name;
                if (cancellation.IsCancellationRequested) break;
            }
            StatusChanged?.Invoke(null);
            try { await Task.Delay(1000, cancellation); }
            catch (OperationCanceledException) { break; }
        }
        StatusChanged?.Invoke(null);
    }

    private bool VerifyServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) || pid == 0)
            return false;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            string? image = process.MainModule?.FileName;
            if (image is null || CanonicalPath(image) != _trustedPythonImage) return false;
            if (!OpenProcessToken(process.Handle, 0x0008, out nint token)) return false;
            try
            {
                using var identity = new WindowsIdentity(token);
                SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
                return currentUser is not null && identity.User?.Equals(currentUser) == true;
            }
            finally { CloseHandle(token); }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string CanonicalPath(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var buffer = new char[32768];
        uint length = GetFinalPathNameByHandleW(stream.SafeFileHandle.DangerousGetHandle(), buffer,
            (uint)buffer.Length, 0x00000002);
        if (length == 0 || length >= buffer.Length) throw new IOException("Cannot resolve executable path.");
        return new string(buffer, 0, (int)length).ToUpperInvariant();
    }

    internal static ulong QpcNanosecondsValue()
    {
        long ticks = Stopwatch.GetTimestamp();
        long frequency = Stopwatch.Frequency;
        if (ticks < 0 || frequency <= 0) throw new InvalidOperationException("Invalid QPC clock.");
        return checked((ulong)(((UInt128)(ulong)ticks * 1_000_000_000UL) / (ulong)frequency));
    }

    private static string QpcNanoseconds() =>
        QpcNanosecondsValue().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task SendAsync(Stream stream, object message, CancellationToken cancellation)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] prefix = BitConverter.GetBytes((uint)payload.Length);
        await stream.WriteAsync(prefix, cancellation);
        await stream.WriteAsync(payload, cancellation);
        await stream.FlushAsync(cancellation);
    }

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, timeout.Token);
        uint length = BitConverter.ToUInt32(prefix);
        if (length is < 1 or > UiStatusProtocol.MaxFrameBytes)
            throw new InvalidDataException("Invalid UI frame length.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, timeout.Token);
        return payload;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(nint file, [Out] char[] path, uint length, uint flags);
}
