using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Lux3D.Core;

/// <summary>One private child process per panel. OAuth tokens never cross this protocol.</summary>
public sealed class Lux3DClient : IAsyncDisposable
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1);
    private Process? _process;
    private bool _disposed;
    public Lux3DClient(string sidecarDirectory) { _directory = Path.GetFullPath(sidecarDirectory); }

    public Task<ConnectionInfo> GetConnectionAsync(string region, CancellationToken cancellationToken = default) => Call<ConnectionInfo>("connection", new { region }, cancellationToken);
    public Task<ConnectionInfo> LoginAsync(string region, CancellationToken cancellationToken = default) => Call<ConnectionInfo>("login", new { region }, cancellationToken);
    public Task<ConnectionInfo> LogoutAsync(string region, CancellationToken cancellationToken = default) => Call<ConnectionInfo>("logout", new { region }, cancellationToken);
    public Task<ConnectionInfo> SetApiKeyAsync(string region, string apiKey, bool persist, CancellationToken cancellationToken = default) => Call<ConnectionInfo>("setKey", new { region, apiKey, persist }, cancellationToken);
    public Task<TaskRecord[]> ListTasksAsync(CancellationToken cancellationToken = default) => Call<TaskRecord[]>("list", new { }, cancellationToken);
    public Task<TaskRecord> GetTaskAsync(Guid operationId, CancellationToken cancellationToken = default) => Call<TaskRecord>("getTask", new { operationId }, cancellationToken);
    public Task<TaskRecord[]> ListTasksAsync(int offset, int limit, CancellationToken cancellationToken = default) => Call<TaskRecord[]>("list", new { offset, limit }, cancellationToken);
    public Task<bool> HasUnknownSubmissionsAsync(CancellationToken cancellationToken = default) => Call<bool>("hasUnknown", new { }, cancellationToken);
    public Task<TaskRecord> GenerateAsync(string region, GenerationInput input, CancellationToken cancellationToken = default) => Call<TaskRecord>("generate", new { region, input }, cancellationToken);
    public Task<TaskRecord> RefreshAsync(Guid operationId, CancellationToken cancellationToken = default) => Call<TaskRecord>("refresh", new { operationId }, cancellationToken);

    private async Task<T> Call<T>(string method, object payload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Lux3DClient));
            EnsureProcess();
            var id = Guid.NewGuid();
            string request = JsonSerializer.Serialize(new RpcRequest(1, id, method, LuxJson.Element(payload)), LuxJson.Options);
            if (request.Length > 128 * 1024) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The request is too large."));
            await _process!.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(method == "login" ? TimeSpan.FromMinutes(6) : TimeSpan.FromMinutes(4));
            string line = await BoundedLine.ReadAsync(_process.StandardOutput, timeout.Token).ConfigureAwait(false)
                ?? throw new IOException(global::Lux3D.Core.LocalText.Get("The Lux3D background process stopped. Recover the task from history; do not resubmit a generation with an unknown result."));
            var response = JsonSerializer.Deserialize<RpcResponse>(line, LuxJson.Options) ?? throw new IOException(global::Lux3D.Core.LocalText.Get("Invalid background response."));
            if (response.Version != 1 || response.Id != id) throw new IOException(global::Lux3D.Core.LocalText.Get("Background protocol mismatch."));
            if (response.Error is not null) throw new InvalidOperationException(response.Error);
            return response.Result!.Value.Deserialize<T>(LuxJson.Options) ?? throw new IOException(global::Lux3D.Core.LocalText.Get("The background response is empty."));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException)
        {
            StopProcess();
            throw;
        }
        finally { _gate.Release(); }
    }

    private void EnsureProcess()
    {
        if (_process is { HasExited: false }) return;
        _process?.Dispose();
        string executable = Path.Combine(_directory, OperatingSystem.IsWindows() ? "Lux3D.Sidecar.exe" : "Lux3D.Sidecar");
        string dll = Path.Combine(_directory, "Lux3D.Sidecar.dll");
        bool standalone = File.Exists(executable);
        if (!standalone && !File.Exists(dll)) throw new IOException(global::Lux3D.Core.LocalText.Get("Lux3D background files are missing. Reinstall the complete plugin package."));
        var start = new ProcessStartInfo(standalone ? executable : SidecarRuntime.FindDotNet8())
        {
            WorkingDirectory = _directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.Environment[LocalText.LanguageEnvironmentVariable] = LocalText.Language;
        if (!standalone) start.ArgumentList.Add(dll);
        try { _process = Process.Start(start) ?? throw new IOException(global::Lux3D.Core.LocalText.Get("Could not start the Lux3D background process.")); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new IOException(global::Lux3D.Core.LocalText.Get("Could not start the Lux3D background process. Check file permissions and the .NET 8 Runtime for this machine."));
        }
        // Drain diagnostics without copying provider responses or secrets into Rhino logs.
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
    }
    private void StopProcess()
    {
        if (_process is null) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _process.Dispose(); _process = null;
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; StopProcess(); }
        finally { _gate.Release(); }
    }
}

public static class BoundedLine
{
    public static async Task<string?> ReadAsync(TextReader reader, CancellationToken ct, int maxLength = 2 * 1024 * 1024)
    {
        var text = new System.Text.StringBuilder();
        char[] one = new char[1];
        while (true)
        {
            int count = await reader.ReadAsync(one.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) return text.Length == 0 ? null : throw new IOException("Truncated protocol line.");
            if (one[0] == '\n') return text.ToString();
            if (text.Length >= maxLength) throw new IOException("Protocol line exceeds limit.");
            text.Append(one[0]);
        }
    }
}
