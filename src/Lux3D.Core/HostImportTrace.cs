using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lux3D.Core;

/// <summary>
/// Best-effort local import diagnostics. All methods are thread-safe. The optional
/// root is a task-store root; reports are written to its host-diagnostics directory.
/// Callers must pass local stage descriptions only, never prompts, credentials or
/// remote response bodies. Redaction is an additional safeguard, not a secret detector.
/// </summary>
public sealed class HostImportTrace
{
    private const int MaximumEvents = 512;
    private const int MaximumInputTextLength = 16_384;
    private const RegexOptions RedactionOptions = RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly TimeSpan RedactionTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex UrlPattern = new(
        @"\b(?:[a-z][a-z0-9+.-]*:(?://|\\{2})|www\.)[^\s""'<>]+|\bhttps?%3a%2f%2f[^\s""'<>]+",
        RedactionOptions, RedactionTimeout);
    private static readonly Regex AuthorizationPattern = new(
        @"\b(?:Bearer|Basic)\s+\S+", RedactionOptions, RedactionTimeout);
    private static readonly Regex SensitiveFieldPattern = new(
        @"\b(?:api[\s_-]?key|access[\s_-]?token|refresh[\s_-]?token|id[\s_-]?token|client[\s_-]?secret|authorization|token|password|passwd|secret|credentials?|prompt)\b[\s\S]*|(?:提示词|密码|令牌|密钥|凭证)[\s\S]*",
        RedactionOptions, RedactionTimeout);
    private static readonly Regex OpaqueSecretPattern = new(
        @"\b(?:sk-[a-z0-9_-]+|eyj[a-z0-9_-]+\.[a-z0-9_-]+\.[a-z0-9_-]+|[a-z0-9_+/=-]{40,})",
        RedactionOptions, RedactionTimeout);

    private readonly object _sync = new();
    private readonly Guid _operationId;
    private readonly uint _documentSerial;
    private readonly string _version;
    private readonly Guid _attemptId = Guid.NewGuid();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly Queue<TraceEvent> _events = new();
    private readonly string _directory = string.Empty;
    private DateTimeOffset _updatedAt;
    private DateTimeOffset? _completedAt;
    private string _result = "InProgress";
    private TraceFailure? _failure;
    private long _droppedEvents;
    private bool _isPersisted;

    public string ReportPath { get; } = string.Empty;

    /// <summary>True only when the latest snapshot was successfully persisted.</summary>
    public bool IsPersisted { get { lock (_sync) return _isPersisted; } }

    public HostImportTrace(Guid operationId, uint documentSerial, string pluginVersion, string? root = null)
    {
        _operationId = operationId;
        _documentSerial = documentSerial;
        _version = Sanitize(pluginVersion, 64) ?? "unknown";
        _updatedAt = _startedAt;
        try
        {
            _directory = Path.Combine(Path.GetFullPath(root ?? TaskStore.DefaultRoot), "host-diagnostics");
            ReportPath = Path.Combine(_directory, $"{operationId:N}-{_attemptId:N}.json");
        }
        catch (Exception)
        {
            // An unusable diagnostic location must not prevent importing a model.
        }
        Record("start");
    }

    public void Record(string stage, string? detail = null)
    {
        lock (_sync)
        {
            AddEvent(Sanitize(stage, 96) ?? "unknown", Sanitize(detail, 1_024));
            Persist();
        }
    }

    public void Complete(string state)
    {
        lock (_sync)
        {
            _result = Sanitize(state, 128) ?? "Completed";
            AddEvent("complete", _result);
            _completedAt = _updatedAt;
            Persist();
        }
    }

    public void Fail(Exception exception)
    {
        lock (_sync)
        {
            string type = "Exception";
            string? message = null;
            try
            {
                type = Sanitize(exception.GetType().FullName, 256) ?? type;
                message = Sanitize(exception.Message, 1_024);
            }
            catch (Exception)
            {
                // Even a custom Exception.Message getter must not break the import.
            }
            _failure = new TraceFailure(type, message);
            _result = "Failed";
            AddEvent("failure", type);
            _completedAt = _updatedAt;
            Persist();
        }
    }

    private void AddEvent(string stage, string? detail)
    {
        _updatedAt = DateTimeOffset.UtcNow;
        if (_events.Count == MaximumEvents)
        {
            _events.Dequeue();
            _droppedEvents++;
        }
        _events.Enqueue(new TraceEvent(_updatedAt, stage, detail));
    }

    private void Persist()
    {
        _isPersisted = false;
        if (ReportPath.Length == 0) return;
        try
        {
            Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                operationId = _operationId,
                documentSerial = _documentSerial,
                version = _version,
                attemptId = _attemptId,
                startedAt = _startedAt,
                updatedAt = _updatedAt,
                completedAt = _completedAt,
                result = _result,
                events = _events.ToArray(),
                droppedEvents = _droppedEvents,
                failure = _failure
            }, LuxJson.Options);
            // The temporary file is in the destination directory, so readers see
            // a complete previous or new snapshot. TaskStore also restricts Unix permissions.
            TaskStore.WriteAtomic(ReportPath, data);
            _isPersisted = true;
        }
        catch (Exception)
        {
            // Diagnostics are never a prerequisite for model placement.
        }
    }

    private static string? Sanitize(string? text, int maximumLength)
    {
        if (text is null) return null;
        // Do not truncate a URL or credential before redaction and accidentally
        // retain its prefix. Oversized diagnostic inputs are omitted entirely.
        if (text.Length > MaximumInputTextLength) return "[diagnostic text omitted: too long]";
        try
        {
            text = UrlPattern.Replace(text, "[url removed]");
            text = AuthorizationPattern.Replace(text, "[authorization removed]");
            text = SensitiveFieldPattern.Replace(text, "[sensitive text removed]");
            text = OpaqueSecretPattern.Replace(text, "[opaque text removed]");
            text = new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
            if (text.Length <= maximumLength) return text;
            int length = maximumLength;
            if (char.IsHighSurrogate(text[length - 1])) length--;
            return text[..length];
        }
        catch (Exception)
        {
            return "[diagnostic text omitted]";
        }
    }

    private sealed record TraceEvent(DateTimeOffset Timestamp, string Stage, string? Detail);
    private sealed record TraceFailure(string ExceptionType, string? Message);
}
