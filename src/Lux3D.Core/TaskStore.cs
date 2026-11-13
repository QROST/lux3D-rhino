using System.Security.Cryptography;
using System.Text.Json;

namespace Lux3D.Core;

/// <summary>Durable records never contain tokens or expiring asset URLs.</summary>
public sealed class TaskStore
{
    public string Root { get; }
    public TaskStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.Combine(Root, "tasks"));
        Directory.CreateDirectory(Path.Combine(Root, "assets"));
        Directory.CreateDirectory(Path.Combine(Root, "inputs"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lux3D-Rhino");
    private string PathFor(Guid id) => Path.Combine(Root, "tasks", id.ToString("N") + ".json");
    public TaskRecord Get(Guid id)
    {
        string path = PathFor(id);
        try
        {
            if (!File.Exists(path)) throw new IOException(global::Lux3D.Core.LocalText.Get("The task record is missing or too large."));
            // Readers keep their original snapshot while another process atomically replaces the file.
            // Windows requires delete sharing for that replacement; length and content use one handle.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 128 * 1024) throw new IOException(global::Lux3D.Core.LocalText.Get("The task record is missing or too large."));
            using var reader = new StreamReader(stream);
            TaskRecord record = JsonSerializer.Deserialize<TaskRecord>(reader.ReadToEnd(), LuxJson.Options) ?? throw new IOException(global::Lux3D.Core.LocalText.Get("The task record is corrupt."));
            if (!IsValidRecord(record, id)) throw new IOException(global::Lux3D.Core.LocalText.Get("The task record is corrupt."));
            return record with { Error = LocalText.RecordedMessage(record.Error) };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { throw new IOException(global::Lux3D.Core.LocalText.Get("The task record cannot be read safely. Its original file has been preserved.")); }
    }
    private static bool IsValidRecord(TaskRecord record, Guid id) => id != Guid.Empty && record.OperationId == id &&
        !string.IsNullOrWhiteSpace(record.Region) && record.Prompt is not null &&
        !string.IsNullOrWhiteSpace(record.Model) && !string.IsNullOrWhiteSpace(record.State) && record.ConnectionId is not null;
    private static bool TryRecordId(string path, out Guid id) =>
        Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out id) && id != Guid.Empty &&
        string.Equals(Path.GetFileName(path), id.ToString("N") + ".json", StringComparison.Ordinal);
    private IEnumerable<string> RecordPaths() => Directory.EnumerateFiles(Path.Combine(Root, "tasks"))
        .Where(p => string.Equals(Path.GetExtension(p), ".json", StringComparison.OrdinalIgnoreCase));
    private TaskRecord ReadForHistory(Guid id)
    {
        try { return Get(id); }
        catch (IOException)
        {
            // Display-only: empty binding makes this invalid for Save; Get still throws,
            // so refresh can never overwrite the original bytes using this placeholder.
            return new(id, "", global::Lux3D.Core.LocalText.Get("Unreadable task record"), null, "", 0,
                "SubmissionUnknown", null, null, null,
                global::Lux3D.Core.LocalText.Get("This task record cannot be read. The original file is preserved. Check the provider's history before generating again; another generation may incur a duplicate charge."),
                DateTimeOffset.MinValue);
        }
    }
    private IEnumerable<TaskRecord> Records() => RecordPaths()
        .Where(p => TryRecordId(p, out _))
        .Select(p => { TryRecordId(p, out Guid id); return ReadForHistory(id); })
        .Select(t => t.State is "Submitting" or "Uploading" ? t with { State = "SubmissionUnknown", Error = global::Lux3D.Core.LocalText.Get("The previous submission was interrupted. Automatic resubmission is disabled; check the provider's task history.") } :
            t.State == "Ready" && !File.Exists(t.AssetPath) ? t with { State = "Missing", Error = global::Lux3D.Core.LocalText.Get("The local asset is missing. Query the original task to download it again.") } : t)
        .OrderByDescending(t => t.CreatedAt);
    public TaskRecord[] List(int offset = 0, int limit = 30)
    {
        if (offset < 0 || limit is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(limit), global::Lux3D.Core.LocalText.Get("History pages must contain 1 to 30 records."));
        return Records().Skip(offset).Take(limit).ToArray();
    }
    public bool HasUnknownSubmissions() => RecordPaths().Any(p =>
        !TryRecordId(p, out Guid id) || ReadForHistory(id).State is "SubmissionUnknown" or "Submitting" or "Uploading");
    public void Save(TaskRecord task)
    {
        if (!IsValidRecord(task, task.OperationId)) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("The task record is corrupt."), nameof(task));
        string file = PathFor(task.OperationId);
        WriteAtomic(file, JsonSerializer.SerializeToUtf8Bytes(task, LuxJson.Options));
    }
    public FileStream LockMutation() => new(Path.Combine(Root, ".mutation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public static void WriteAtomic(string destination, byte[] content)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(content); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public (string Path, string Hash) StoreAsset(byte[] data)
    {
        GlbContainerValidator.Validate(data);
        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        string path = Path.Combine(Root, "assets", hash + ".glb");
        WriteAtomic(path, data);
        return (path, hash);
    }
    public (string Path, string Hash) PreserveRejectedAsset(byte[] data)
    {
        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        string directory = Path.Combine(Root, "rejected");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, hash + ".glb");
        // Quarantined bytes are never advertised as a Ready asset or passed to Rhino without validation.
        WriteAtomic(path, data);
        return (path, hash);
    }
    public (string Path, string Hash) SnapshotImage(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 24 or > 20_000_000) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Use a PNG/JPEG image of at most 20 MB."));
        byte[] data = File.ReadAllBytes(path);
        string extension = ImageInputValidator.Validate(data);
        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        string copy = Path.Combine(Root, "inputs", hash + extension);
        WriteAtomic(copy, data);
        return (copy, hash);
    }
}
