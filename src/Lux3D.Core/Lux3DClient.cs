using System.Diagnostics;
using System.Text.Json;

namespace Lux3D.Core;

/// <summary>One private child process per panel. Secrets never cross this protocol.</summary>
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
            if (request.Length > 128 * 1024) throw new InvalidOperationException("请求过长。");
            await _process!.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(method == "login" ? TimeSpan.FromMinutes(6) : TimeSpan.FromMinutes(4));
            string line = await BoundedLine.ReadAsync(_process.StandardOutput, timeout.Token).ConfigureAwait(false)
                ?? throw new IOException("Lux3D 后台进程已停止。任务可从历史恢复，请勿重复提交未知结果的生成。");
            var response = JsonSerializer.Deserialize<RpcResponse>(line, LuxJson.Options) ?? throw new IOException("无效后台响应。");
            if (response.Version != 1 || response.Id != id) throw new IOException("后台协议不匹配。");
            if (response.Error is not null) throw new InvalidOperationException(response.Error);
            return response.Result!.Value.Deserialize<T>(LuxJson.Options) ?? throw new IOException("后台响应为空。");
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
        string dotnet = new[] { "/opt/homebrew/bin/dotnet", "/usr/local/share/dotnet/dotnet", "dotnet" }.First(p => p == "dotnet" || File.Exists(p));
        var start = new ProcessStartInfo(standalone ? executable : dotnet)
        {
            WorkingDirectory = _directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (!standalone) start.ArgumentList.Add(dll);
        _process = Process.Start(start) ?? throw new IOException("无法启动 Lux3D 后台进程。");
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
