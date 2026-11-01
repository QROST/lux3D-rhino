using System.Text.Json;
using Xunit;

namespace Lux3D.Core.Tests;

public sealed class HostImportTraceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lux3d-host-trace-tests-" + Guid.NewGuid());

    [Fact]
    public void EachAttemptGetsAnIndependentCompleteReport()
    {
        Guid operationId = Guid.NewGuid();
        var trace = new HostImportTrace(operationId, 27, "0.1.3", _root);
        trace.Record("import", "local asset read");
        trace.Complete("Placed");
        var retry = new HostImportTrace(operationId, 27, "0.1.3", _root);

        Assert.True(trace.IsPersisted);
        Assert.True(retry.IsPersisted);
        Assert.NotEqual(trace.ReportPath, retry.ReportPath);
        Assert.Equal(Path.Combine(_root, "host-diagnostics"), Path.GetDirectoryName(trace.ReportPath));
        using JsonDocument document = Read(trace.ReportPath);
        JsonElement report = document.RootElement;
        Assert.Equal(operationId, report.GetProperty("operationId").GetGuid());
        Assert.Equal(27u, report.GetProperty("documentSerial").GetUInt32());
        Assert.Equal("0.1.3", report.GetProperty("version").GetString());
        Assert.NotEqual(Guid.Empty, report.GetProperty("attemptId").GetGuid());
        Assert.Equal("Placed", report.GetProperty("result").GetString());
        Assert.True(report.GetProperty("completedAt").GetDateTimeOffset() >= report.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Contains(report.GetProperty("events").EnumerateArray(), item => item.GetProperty("stage").GetString() == "import");
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(trace.ReportPath)!, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentReadersOnlyObserveCompleteJsonSnapshots()
    {
        Guid operationId = Guid.NewGuid();
        var trace = new HostImportTrace(operationId, 1, "0.1.3", _root);
        Task writer = Task.Run(() => Parallel.For(0, 20, index => trace.Record("stage", index.ToString())));
        Task reader = Task.Run(() =>
        {
            for (int iteration = 0; iteration < 100; iteration++)
            {
                using JsonDocument snapshot = Read(trace.ReportPath);
                Assert.Equal(operationId, snapshot.RootElement.GetProperty("operationId").GetGuid());
                Assert.True(snapshot.RootElement.GetProperty("events").GetArrayLength() >= 1);
            }
        });
        await Task.WhenAll(writer, reader);
        trace.Complete("Placed");

        Assert.True(trace.IsPersisted);
        using JsonDocument final = Read(trace.ReportPath);
        Assert.Equal(20, final.RootElement.GetProperty("events").EnumerateArray().Count(item => item.GetProperty("stage").GetString() == "stage"));
        Assert.Equal("Placed", final.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public void InvalidOrUnwritableDiagnosticLocationsNeverThrow()
    {
        Directory.CreateDirectory(_root);
        string blockedRoot = Path.Combine(_root, "regular-file");
        File.WriteAllText(blockedRoot, "blocks directory creation");
        foreach (string location in new[] { blockedRoot, "invalid\0path" })
        {
            HostImportTrace? trace = null;
            Exception? error = Record.Exception(() =>
            {
                trace = new HostImportTrace(Guid.NewGuid(), 1, "0.1.3", location);
                trace.Record("import");
                trace.Complete("Placed");
                trace.Fail(new IOException("local file unavailable"));
            });
            Assert.Null(error);
            Assert.False(trace!.IsPersisted);
        }
    }

    [Fact]
    public void PersistenceFailureAfterCreationDoesNotInterruptImport()
    {
        var trace = new HostImportTrace(Guid.NewGuid(), 1, "0.1.3", _root);
        Assert.True(trace.IsPersisted);
        string directory = Path.GetDirectoryName(trace.ReportPath)!;
        Directory.Delete(directory, recursive: true);
        File.WriteAllText(directory, "blocks later writes");

        Assert.Null(Record.Exception(() => trace.Record("import-completed")));
        Assert.Null(Record.Exception(() => trace.Complete("Placed")));
        Assert.False(trace.IsPersisted);
    }

    [Fact]
    public void ReportsRemoveUrlsCredentialsAndLabeledPromptText()
    {
        var trace = new HostImportTrace(Guid.NewGuid(), 1, "0.1.3", _root);
        trace.Record("download", "source=https://private.example/result.glb?signature=private-signed-value");
        trace.Record("encoded", "https%3A%2F%2Fprivate.example%2Fprivate-encoded-value");
        trace.Record("authorization", "Bearer private-bearer-value");
        trace.Record("credentials", "api_key=private-api-key-value");
        trace.Record("input", "prompt=private-user-description");
        var exception = new InvalidOperationException("Local import failed: file:///private/private-file-url; refresh_token=private-refresh-value");
        exception.Data["credential"] = "private-exception-data";
        trace.Fail(exception);

        string json = File.ReadAllText(trace.ReportPath);
        Assert.DoesNotContain("private-", json);
        Assert.DoesNotContain("private.example", json);
        Assert.DoesNotContain("https", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file://", json, StringComparison.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement failure = document.RootElement.GetProperty("failure");
        Assert.Equal(typeof(InvalidOperationException).FullName, failure.GetProperty("exceptionType").GetString());
        Assert.Contains("Local import failed", failure.GetProperty("message").GetString());
        Assert.False(failure.TryGetProperty("stackTrace", out _));
        Assert.Equal("Failed", document.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public void DiagnosticTextIsBoundedWithoutRetainingTruncatedSecrets()
    {
        var trace = new HostImportTrace(Guid.NewGuid(), 1, "0.1.3", _root);
        trace.Record("large", new string('x', 20_000) + "https://private.example/secret");
        trace.Fail(new IOException(new string('x', 20_000) + "access_token=private-value"));

        string json = File.ReadAllText(trace.ReportPath);
        Assert.True(json.Length < 4_096);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain(new string('x', 100), json);
        Assert.True(trace.IsPersisted);
    }

    private static JsonDocument Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonDocument.Parse(stream);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
