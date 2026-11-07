using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using Lux3D.Core;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.UI;
using RhinoDoc = global::Rhino.RhinoDoc;
using OpenFileDialog = Eto.Forms.OpenFileDialog;

namespace Lux3D.Rhino;

[Guid("AC141E4E-8B0E-4A82-A34A-F2B0783201FB")]
public sealed class Lux3DPanel : Panel, IPanel
{
    private enum FeedbackKind { Query, Placement }
    private sealed record TaskFeedback(string Message, bool IsError, FeedbackKind Kind);
    private readonly uint _serial;
    private readonly Lux3DClient _client;
    private readonly Lux3DHostService _host;
    private readonly TaskPlacementService _taskPlacement;
    private readonly CandidatePreview _preview = new();
    private readonly List<(string Name, PreparedAsset Asset, Guid ImportId)> _candidates = new();
    private readonly List<TaskRecord> _records = new();
    private readonly UITimer _timer = new() { Interval = 5 };
    private readonly UITimer _elapsedTimer = new() { Interval = 1 };
    private readonly UITimer _previewTimer = new() { Interval = 0.15 };
    private readonly Stopwatch _operationClock = new();
    private readonly DropDown _region = new() { DataStore = new[] { global::Lux3D.Core.LocalText.Get("Global"), global::Lux3D.Core.LocalText.Get("China") }, SelectedIndex = 0 };
    private readonly Label _connection = Status(global::Lux3D.Core.LocalText.Get("Not connected"));
    private readonly Label _generationConnection = Status(global::Lux3D.Core.LocalText.Get("Generation requires a regional API key; OAuth sign-in is optional."));
    private readonly PasswordBox _apiKey = new();
    private readonly CheckBox _persist = new() { Text = global::Lux3D.Core.LocalText.Get("Save in secure local storage"), Checked = false };
    private readonly TextArea _prompt = new() { Height = 90, Wrap = true, Text = "" };
    private readonly TextBox _image = new() { ReadOnly = true, PlaceholderText = global::Lux3D.Core.LocalText.Get("Optional: one PNG/JPEG photo") };
    private readonly NumericStepper _faces = new() { MinValue = 10000, MaxValue = 300000, Increment = 5000, DecimalPlaces = 0, Value = 50000 };
    private readonly CheckBox _autoPlace = new() { Text = global::Lux3D.Core.LocalText.Get("Place in current document when ready"), Checked = true, ToolTip = global::Lux3D.Core.LocalText.Get("New tasks use the document, location, height and angle at submission. The original document must remain active. After closing the panel, place the result manually.") };
    private readonly CheckBox _pbr = new() { Text = global::Lux3D.Core.LocalText.Get("Request PBR materials"), Checked = true };
    private readonly Button _generate = new() { Text = global::Lux3D.Core.LocalText.Get("Generate (uses service credits)") };
    private readonly ListBox _tasks = new() { Height = 130 };
    private readonly TextArea _details = new() { ReadOnly = true, Height = 95, Wrap = true };
    private readonly DropDown _candidate = new();
    private readonly NumericStepper _height = new() { MinValue = 0.001, MaxValue = 1e9, DecimalPlaces = 3, Value = 1000 };
    private readonly Label _heightLabel = Status(global::Lux3D.Core.LocalText.Get("Model height (document units)"));
    private readonly NumericStepper _rotation = new() { MinValue = -360, MaxValue = 360, DecimalPlaces = 1, Value = 0 };
    private readonly Label _placement = Status(global::Lux3D.Core.LocalText.Get("Origin / World XY plane; height uses target document units"));
    private readonly Label _candidatePlacement = Status("");
    private readonly Label _currentTask = Status(global::Lux3D.Core.LocalText.Get("No new task submitted. Existing results are under Tasks / Candidates."));
    private readonly Label _operation = Status(global::Lux3D.Core.LocalText.Get("Ready"));
    private readonly TextArea _status = new() { ReadOnly = true, Height = 76, Wrap = true, Text = global::Lux3D.Core.LocalText.Get("Finished models are placed in the current document by default. Tasks and models are saved locally.") };
    private readonly TabControl _tabs = new();
    private readonly Button _pause = new() { Text = global::Lux3D.Core.LocalText.Get("Pause automatic queries") };
    private readonly List<Button> _networkButtons = new();
    private readonly Label _pageLabel = Status(global::Lux3D.Core.LocalText.Get("Page 1 · 30 per page"));
    private readonly Button _previousPage;
    private readonly Button _nextPage;
    private readonly Dictionary<Guid, TaskPlacementTarget> _autoPlacements = new();
    private readonly Dictionary<Guid, TaskFeedback> _taskFeedback = new();
    private TaskRecord? _recentTask;
    private Guid? _placingOperationId;
    private string _operationPhase = "";
    private string _planeDescription = global::Lux3D.Core.LocalText.Get("World XY plane");
    private int _feedbackRevision;
    private bool _feedbackIsError, _backgroundOperation;
    private int _page, _placementEpoch, _autoPollIndex;
    private Plane _plane = Plane.WorldXY;
    private Point3d _point = Point3d.Origin;
    private bool _busy, _disposed, _shown, _paused, _canGenerate;

    public Lux3DPanel(uint documentSerialNumber)
    {
        _serial = documentSerialNumber;
        _host = Lux3DPlugin.Instance.Host;
        _taskPlacement = Lux3DPlugin.Instance.Placement;
        _client = Lux3DPlugin.Instance.Client;
        RhinoDoc? doc = RhinoDoc.FromRuntimeSerialNumber(_serial);
        if (doc is not null)
        {
            _host.Session(doc);
            _height.Value = Math.Clamp(TaskPlacementService.DefaultPlacement(doc).Height, _height.MinValue, _height.MaxValue);
        }
        Button login = NetworkButton(global::Lux3D.Core.LocalText.Get("OAuth sign-in (optional)"), async () => SetConnection(await _client.LoginAsync(Region())));
        Button refreshConnection = NetworkButton(global::Lux3D.Core.LocalText.Get("Connection status"), async () => SetConnection(await _client.GetConnectionAsync(Region())));
        Button logout = NetworkButton(global::Lux3D.Core.LocalText.Get("Disconnect and clear credentials"), async () => SetConnection(await _client.LogoutAsync(Region())));
        Button saveKey = NetworkButton(global::Lux3D.Core.LocalText.Get("Set API key"), async () =>
        {
            string key = _apiKey.Text;
            _apiKey.Text = string.Empty;
            SetConnection(await _client.SetApiKeyAsync(Region(), key, _persist.Checked == true));
        });
        _region.SelectedIndexChanged += (_, _) =>
        {
            _canGenerate = false; _generate.Enabled = false;
            _connection.Text = global::Lux3D.Core.LocalText.Get("Region changed. Set its API key, or choose Connection status to load a saved connection.");
            UpdateGenerationConnection();
        };
        Button pickImage = LocalButton(global::Lux3D.Core.LocalText.Get("Choose photo"), ChooseImage);
        Button clearImage = LocalButton(global::Lux3D.Core.LocalText.Get("Clear photo"), () => _image.Text = string.Empty);
        _generate.Click += (_, _) => _ = RunAsync(GenerateAsync, global::Lux3D.Core.LocalText.Get("Submit generation task"));
        _networkButtons.Add(_generate);
        _generate.Enabled = false;
        Button refresh = NetworkButton(global::Lux3D.Core.LocalText.Get("Query selected task"), RefreshSelectedAsync);
        Button place = NetworkButton(global::Lux3D.Core.LocalText.Get("Place in current document"), PlaceSelectedTaskAsync);
        Button reload = NetworkButton(global::Lux3D.Core.LocalText.Get("Load local history"), ReloadAsync);
        _previousPage = NetworkButton(global::Lux3D.Core.LocalText.Get("Previous"), async () => { if (_page > 0) { _page--; await ReloadAsync(null); } });
        _nextPage = NetworkButton(global::Lux3D.Core.LocalText.Get("Next"), async () => { if (_records.Count == 30) { _page++; await ReloadAsync(null); } });
        _previousPage.Enabled = false; _nextPage.Enabled = false;
        _pause.Click += (_, _) =>
        {
            _paused = !_paused;
            _pause.Text = _paused ? global::Lux3D.Core.LocalText.Get("Resume automatic queries") : global::Lux3D.Core.LocalText.Get("Pause automatic queries");
            ShowFeedback(_paused ? global::Lux3D.Core.LocalText.Get("Automatic queries and pending placements are paused. Remote generation and requests already started are not cancelled. Query or place the original task manually.") : global::Lux3D.Core.LocalText.Get("Automatic queries and pending placements resumed."));
        };
        _tasks.SelectedIndexChanged += (_, _) => RenderTask();
        Button add = LocalButton(global::Lux3D.Core.LocalText.Get("Add candidate (up to 3)"), AddSelectedCandidate);
        Button local = LocalButton(global::Lux3D.Core.LocalText.Get("Load local GLB"), AddLocalCandidate);
        Button pick = LocalButton(global::Lux3D.Core.LocalText.Get("Pick placement point"), PickPlacement);
        Button reset = LocalButton(global::Lux3D.Core.LocalText.Get("Place at world origin"), () => { _plane = Plane.WorldXY; _point = Point3d.Origin; _planeDescription = global::Lux3D.Core.LocalText.Get("World XY plane"); UpdatePlacement(); });
        Button accept = LocalButton(global::Lux3D.Core.LocalText.Get("Accept current candidate"), AcceptCandidate);
        Button remove = LocalButton(global::Lux3D.Core.LocalText.Get("Remove current candidate"), RemoveCandidate);
        Button clear = LocalButton(global::Lux3D.Core.LocalText.Get("Exit comparison / Clear candidates"), ClearCandidates);
        _candidate.SelectedIndexChanged += (_, _) => SchedulePreview();
        _height.ValueChanged += (_, _) => { UpdatePlacementSummary(); SchedulePreview(); };
        _rotation.ValueChanged += (_, _) => { UpdatePlacementSummary(); SchedulePreview(); };
        _previewTimer.Elapsed += (_, _) => { _previewTimer.Stop(); ShowPreview(); };
        _elapsedTimer.Elapsed += (_, _) => UpdateOperation();
        _taskPlacement.ProgressChanged += PlacementProgressChanged;
        _timer.Elapsed += (_, _) =>
        {
            if (!_shown || _paused || _busy || _disposed) return;
            if (_autoPlacements.Count > 0)
            {
                _autoPollIndex %= _autoPlacements.Count;
                Guid operationId = _autoPlacements.Keys.ElementAt(_autoPollIndex++);
                _ = RunAsync(() => RefreshAutoTaskAsync(operationId), global::Lux3D.Core.LocalText.Get("Automatically query original task"), background: true);
                return;
            }
            TaskRecord? record = SelectedTask();
            if (record is not null && record.State is "Queued" or "Running" or "StatusUnknown" or "Succeeded" or "DownloadFailed")
                _ = RunAsync(RefreshSelectedAsync, global::Lux3D.Core.LocalText.Get("Automatically query original task"), background: true);
        };
        _tabs.Pages.Add(new TabPage { Text = global::Lux3D.Core.LocalText.Get("Generate"), Content = Layout(
                    _generationConnection, LocalButton(global::Lux3D.Core.LocalText.Get("Set up connection"), () => _tabs.SelectedIndex = 2),
                    Status(global::Lux3D.Core.LocalText.Get("Text or one photo; inputs are saved with the submitted task.")), _prompt,
                    _image, Row(pickImage, clearImage), Status(global::Lux3D.Core.LocalText.Get("Model: G1-Turbo")),
                    Row(new Label { Text = global::Lux3D.Core.LocalText.Get("Target faces") }, _faces), _pbr, _autoPlace,
                    Row(_heightLabel, _height),
                    Row(new Label { Text = global::Lux3D.Core.LocalText.Get("Rotation (°)") }, _rotation), _placement, Row(pick, reset),
                    Status(global::Lux3D.Core.LocalText.Get("Default height is 1 meter. Submission remembers this position, size and angle.")), _generate, _currentTask,
                    LocalButton(global::Lux3D.Core.LocalText.Get("View tasks and results"), () => _tabs.SelectedIndex = 1),
                    Status(global::Lux3D.Core.LocalText.Get("Unknown creation results are not retried automatically; another generation may use more credits."))) });
        _tabs.Pages.Add(new TabPage { Text = global::Lux3D.Core.LocalText.Get("Tasks / Candidates"), Content = Layout(_tasks, Row(_previousPage, _pageLabel, _nextPage), Row(reload, refresh), _pause, _details, place,
                    Status(global::Lux3D.Core.LocalText.Get("Placing the same task again locates its existing block. For another instance, add and accept a candidate.")),
                    _candidatePlacement, LocalButton(global::Lux3D.Core.LocalText.Get("Adjust position, size and angle"), () => _tabs.SelectedIndex = 0),
                    Row(add, local), _candidate,
                    Status(global::Lux3D.Core.LocalText.Get("Candidate previews show geometry and base color. After accepting, use Rendered mode to view materials.")), accept, Row(remove, clear)) });
        _tabs.Pages.Add(new TabPage { Text = global::Lux3D.Core.LocalText.Get("Connection"), Content = Layout(
                    Status(global::Lux3D.Core.LocalText.Get("Generation requires an API key. Optional OAuth sign-in does not replace generation authentication in this version.")),
                    _region, _connection, new Label { Text = global::Lux3D.Core.LocalText.Get("Regional API key") }, _apiKey, _persist, saveKey,
                    Status(global::Lux3D.Core.LocalText.Get("Validation queries the task list, not remaining credits. The key is not stored in the Rhino document.")),
                    Row(refreshConnection, logout), login,
                    Status(global::Lux3D.Core.LocalText.Get("OAuth opens the Manycore authorization page in your browser. An API key is still required for generation after signing in."))) });
        UpdatePlacementSummary();
        Content = new StackLayout { Padding = 10, Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { new Label { Text = "Lux3D for Rhino", Font = new Font(SystemFont.Bold, 14) },
                Status(global::Lux3D.Core.LocalText.Format("Target document: {0}", doc?.Name ?? global::Lux3D.Core.LocalText.Get("Untitled document"))), new StackLayoutItem(_tabs, true), _operation, _status } };
    }
    private static Label Status(string text) => new() { Text = text, Wrap = WrapMode.Word };
    private static Control Layout(params Control[] controls)
    {
        var layout = new StackLayout { Padding = 8, Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (Control control in controls) layout.Items.Add(control);
        return new Scrollable { Content = layout };
    }
    private static Control Row(params Control[] controls)
    {
        var layout = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (Control control in controls) layout.Items.Add(new StackLayoutItem(control, true));
        return layout;
    }
    private Button NetworkButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.Click += (_, _) => _ = RunAsync(action, text);
        _networkButtons.Add(button);
        return button;
    }
    private Button LocalButton(string text, Action action)
    {
        var button = new Button { Text = text };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { ShowFeedback(ex.Message, isError: true); } };
        return button;
    }
    private string Region() => _region.SelectedIndex == 1 ? "cn" : "global";
    private RhinoDoc TargetDocument() => RhinoDoc.FromRuntimeSerialNumber(_serial) ?? throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The target document is closed. Reopen the panel in the target document."));
    private async Task RunAsync(Func<Task> action, string operation, bool background = false)
    {
        if (_busy || _disposed) return;
        _busy = true;
        _backgroundOperation = background;
        int feedbackRevision = _feedbackRevision;
        _operationPhase = operation;
        _operationClock.Restart(); _elapsedTimer.Start(); UpdateOperation();
        foreach (Button button in _networkButtons) button.Enabled = false;
        _region.Enabled = false;
        bool failed = false;
        try
        {
            await action();
            Ui(() => { if (!_disposed && !background && _feedbackRevision == feedbackRevision) ShowFeedback(operation + global::Lux3D.Core.LocalText.Get(" completed.")); });
        }
        catch (Exception ex)
        {
            failed = true;
            Ui(() =>
            {
                if (_feedbackRevision == feedbackRevision || !_feedbackIsError) ShowFeedback(ex.Message, isError: true, background: background);
                global::Rhino.RhinoApp.WriteLine("Lux3D：" + ex.Message);
            });
        }
        finally
        {
            Ui(() =>
            {
                _busy = false;
                _backgroundOperation = false;
                _operationClock.Stop();
                if (!_disposed)
                {
                    _elapsedTimer.Stop();
                    _operation.Text = operation + (failed ? global::Lux3D.Core.LocalText.Get(" incomplete") : global::Lux3D.Core.LocalText.Get(" finished")) + global::Lux3D.Core.LocalText.Get(" · Duration: ") + ElapsedText(_operationClock.Elapsed);
                    foreach (Button button in _networkButtons) button.Enabled = true;
                    _region.Enabled = true; _generate.Enabled = _canGenerate; _previousPage.Enabled = _page > 0; _nextPage.Enabled = _records.Count == 30;
                }
            });
        }
    }
    private void ChooseImage()
    {
        using var dialog = new OpenFileDialog { Title = global::Lux3D.Core.LocalText.Get("Choose one PNG/JPEG photo"), MultiSelect = false };
        dialog.Filters.Add(new FileFilter(global::Lux3D.Core.LocalText.Get("Images"), ".png", ".jpg", ".jpeg"));
        if (dialog.ShowDialog(this) == DialogResult.Ok) { ValidateImage(dialog.FileName); _image.Text = dialog.FileName; }
    }
    private static void Ui(Action action)
    {
        if (global::Rhino.RhinoApp.InvokeRequired) Application.Instance.Invoke(action); else action();
    }
    private static string ElapsedText(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? global::Lux3D.Core.LocalText.Format("{0} min {1} s", (int)elapsed.TotalMinutes, elapsed.Seconds) : global::Lux3D.Core.LocalText.Format("{0} s", Math.Max(0, (int)elapsed.TotalSeconds));
    private void UpdateOperation()
    {
        if (!_disposed && _busy) _operation.Text = _operationPhase + global::Lux3D.Core.LocalText.Get(" · Elapsed: ") + ElapsedText(_operationClock.Elapsed);
    }
    private void PlacementProgressChanged(TaskPlacementProgress progress)
    {
        // Placement is a shared service. Do not show another panel's document or task activity here.
        if (_disposed) return;
        Ui(() =>
        {
            if (_disposed || progress.DocumentSerial != _serial || _placingOperationId != progress.OperationId) return;
            _operationPhase = progress.Message;
            _operation.Text = progress.Message + global::Lux3D.Core.LocalText.Get(" · Placement time: ") + ElapsedText(progress.Elapsed);
        });
    }
    private void ShowFeedback(string message, bool isError = false, Guid? taskId = null, bool background = false, FeedbackKind kind = FeedbackKind.Placement)
    {
        if (_disposed) return;
        if (taskId.HasValue)
        {
            // A later automatic status query must not erase a placement failure and its recovery instructions.
            _taskFeedback.TryGetValue(taskId.Value, out TaskFeedback? previous);
            bool preservePlacementError = kind == FeedbackKind.Query && previous is { IsError: true, Kind: FeedbackKind.Placement };
            if (!preservePlacementError && (!background || previous is not { IsError: true } || isError))
                _taskFeedback[taskId.Value] = new TaskFeedback(message, isError, kind);
            if (SelectedTask()?.OperationId == taskId) RenderTask();
            RenderRecentTask();
        }
        if (background && _feedbackIsError) return;
        _feedbackIsError = isError;
        _feedbackRevision++;
        _status.Text = (isError ? global::Lux3D.Core.LocalText.Get("Action needed: ") : "") + message;
    }
    private void ClearRecoveredQueryError(TaskRecord task)
    {
        if (_disposed || HasTaskIssue(task) || !_taskFeedback.TryGetValue(task.OperationId, out TaskFeedback? previous) || previous.Kind != FeedbackKind.Query) return;
        _taskFeedback.Remove(task.OperationId);
        if (SelectedTask()?.OperationId == task.OperationId) RenderTask();
        RenderRecentTask();
    }
    private void ObserveTask(TaskRecord task, bool makeRecent = false)
    {
        if (makeRecent || _recentTask?.OperationId == task.OperationId)
        {
            _recentTask = task;
            RenderRecentTask();
        }
    }
    private void RenderRecentTask()
    {
        if (_recentTask is not { } task) return;
        string state = StateText(task.State);
        if (_taskFeedback.TryGetValue(task.OperationId, out var feedback))
            state = feedback.IsError ? global::Lux3D.Core.LocalText.Get("Action needed; see the message below or task details") : feedback.Message;
        _currentTask.Text = global::Lux3D.Core.LocalText.Get("Latest generation: ") + ShortPrompt(task.Prompt) + "\n" + state;
    }
    private static string ShortPrompt(string prompt)
    {
        string value = prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length == 0 ? global::Lux3D.Core.LocalText.Get("Photo generation") : value.Length > 60 ? value[..60] + "…" : value;
    }
    private static bool HasTaskIssue(TaskRecord task) => task.State is "Failed" or "ProviderCancelled" or "SubmissionUnknown" or "Rejected" or "Missing" or "DownloadFailed" or "StatusUnknown";
    private static string UnitName(global::Rhino.UnitSystem? units) => units switch
    {
        global::Rhino.UnitSystem.Millimeters => global::Lux3D.Core.LocalText.Get("mm"),
        global::Rhino.UnitSystem.Centimeters => global::Lux3D.Core.LocalText.Get("cm"),
        global::Rhino.UnitSystem.Meters => global::Lux3D.Core.LocalText.Get("m"),
        global::Rhino.UnitSystem.Inches => global::Lux3D.Core.LocalText.Get("in"),
        global::Rhino.UnitSystem.Feet => global::Lux3D.Core.LocalText.Get("ft"),
        null => global::Lux3D.Core.LocalText.Get("document units"),
        _ => units.ToString() ?? global::Lux3D.Core.LocalText.Get("document units"),
    };
    private void UpdateGenerationConnection()
    {
        string region = Region() == "cn" ? global::Lux3D.Core.LocalText.Get("China") : global::Lux3D.Core.LocalText.Get("Global");
        _generationConnection.Text = _canGenerate
            ? region + global::Lux3D.Core.LocalText.Get(" · API key configured; generation is subject to service permissions and credits.")
            : region + global::Lux3D.Core.LocalText.Get(" · Generation requires an API key. Set up the connection first; OAuth sign-in is optional.");
    }
    private void SetConnection(ConnectionInfo info) => Ui(() =>
    {
        if (_disposed) return;
        _canGenerate = info.Region == Region() && info.CanGenerate;
        _connection.Text = info.Message;
        _generate.Enabled = !_busy && _canGenerate;
        UpdateGenerationConnection();
    });
    private static void ValidateImage(string path)
    {
        if (new FileInfo(path).Length > 20_000_000) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Images must not exceed 20 MB."));
        byte[] data = File.ReadAllBytes(path);
        ImageInputValidator.Validate(data);
        using var stream = new MemoryStream(data, writable: false);
        using var bitmap = new Bitmap(stream);
        if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Width > 8192 || bitmap.Height > 8192 || (long)bitmap.Width * bitmap.Height > 32_000_000)
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The image could not be fully decoded or exceeds the dimension limit."));
        // Lock forces native pixel decoding instead of accepting only an image header.
        using var pixels = bitmap.Lock();
        _ = pixels.GetPixel(bitmap.Width - 1, bitmap.Height - 1);
    }
    private async Task GenerateAsync()
    {
        if (!_canGenerate) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Generation requires an API key for the selected region. Configure it under Connection; OAuth sign-in cannot replace it."));
        UpdatePlacementSummary();
        if (!string.IsNullOrWhiteSpace(_image.Text)) ValidateImage(_image.Text);
        string region = Region();
        int placementEpoch = _placementEpoch;
        TaskPlacementTarget? automaticTarget = _autoPlace.Checked == true ? _taskPlacement.Capture(TargetDocument(), Placement()) : null;
        var input = new GenerationInput(_prompt.Text.Trim(), string.IsNullOrEmpty(_image.Text) ? null : _image.Text,
            "G1-Turbo", (int)_faces.Value, _pbr.Checked == true);
        if (await _client.HasUnknownSubmissionsAsync())
        {
            DialogResult result = DialogResult.No;
            Ui(() =>
            {
                if (!_disposed) result = MessageBox.Show(this, global::Lux3D.Core.LocalText.Get("A previous task has an unknown submission result. Check the service first; continuing may incur another generation charge. Create a new task anyway?"), global::Lux3D.Core.LocalText.Get("Confirm new generation"), MessageBoxButtons.YesNo, MessageBoxType.Warning);
            });
            if (result != DialogResult.Yes) { Ui(() => ShowFeedback(global::Lux3D.Core.LocalText.Get("No new task was created. Check the previous task with an unknown submission result first."))); return; }
            input = input with { AcknowledgeUnknownSubmission = true };
        }
        if (_disposed) return;
        TaskRecord task = await _client.GenerateAsync(region, input);
        if (_disposed) return;
        Ui(() =>
        {
            _page = 0;
            bool automaticScheduled = automaticTarget is not null && placementEpoch == _placementEpoch;
            if (automaticTarget is not null && automaticScheduled)
                _autoPlacements[task.OperationId] = automaticTarget;
            ObserveTask(task, makeRecent: true);
            ShowFeedback(automaticScheduled ? global::Lux3D.Core.LocalText.Get("Task submitted. When ready, it will be placed in the original document at the submitted position.") : global::Lux3D.Core.LocalText.Get("Task submitted. When ready, place it from the Tasks tab."));
        });
        await ReloadAsync(task.OperationId);
        await TryAutoPlaceAsync(task);
    }
    private TaskRecord? SelectedTask() => _tasks.SelectedIndex >= 0 && _tasks.SelectedIndex < _records.Count ? _records[_tasks.SelectedIndex] : null;
    private async Task ReloadAsync() => await ReloadAsync(SelectedTask()?.OperationId);
    private async Task ReloadAsync(Guid? selected)
    {
        TaskRecord[] records = await _client.ListTasksAsync(_page * 30, 30);
        Ui(() =>
        {
            if (_disposed) return;
            _records.Clear(); _records.AddRange(records.OrderByDescending(r => r.CreatedAt));
            foreach (TaskRecord record in _records) ObserveTask(record);
            _pageLabel.Text = global::Lux3D.Core.LocalText.Format("Page {0} · 30 per page", _page + 1);
            _tasks.DataStore = _records.Select(r => $"{r.CreatedAt.LocalDateTime:MM-dd HH:mm} · {StateText(r.State)} · {ShortPrompt(r.Prompt)}").ToArray();
            _tasks.SelectedIndex = selected.HasValue ? _records.FindIndex(r => r.OperationId == selected) : _records.Count > 0 ? 0 : -1;
            RenderTask();
        });
    }
    private async Task RefreshSelectedAsync()
    {
        TaskRecord record = SelectedTask() ?? throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Select a task first."));
        TaskRecord refreshed;
        try { refreshed = await _client.RefreshAsync(record.OperationId); }
        catch (Exception ex)
        {
            Ui(() => ShowFeedback(global::Lux3D.Core.LocalText.Get("The original task query did not complete: ") + ex.Message + global::Lux3D.Core.LocalText.Get(" Query again later; no new generation is needed."), isError: true, taskId: record.OperationId, background: _backgroundOperation, kind: FeedbackKind.Query));
            throw;
        }
        Ui(() =>
        {
            ClearRecoveredQueryError(refreshed);
            ObserveTask(refreshed);
            if (HasTaskIssue(refreshed)) ShowFeedback(TaskPlacementPolicy.NotReadyMessage(refreshed), isError: true, taskId: refreshed.OperationId, background: _backgroundOperation, kind: FeedbackKind.Query);
            else if (!_backgroundOperation) ShowFeedback(global::Lux3D.Core.LocalText.Get("Original task: ") + StateText(refreshed.State) + (refreshed.State == "Ready" ? global::Lux3D.Core.LocalText.Get(". Place it in the current document or add it as a preview candidate.") : "。"));
        });
        await ReloadAsync(record.OperationId);
        await TryAutoPlaceAsync(refreshed);
    }
    private async Task PlaceSelectedTaskAsync()
    {
        TaskRecord task = SelectedTask() ?? throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Select a task to place first."));
        TaskPlacementTarget target = _taskPlacement.Capture(TargetDocument(), Placement());
        _autoPlacements.Remove(task.OperationId);
        int epoch = _placementEpoch;
        TaskPlacementResult result;
        try
        {
            _placingOperationId = task.OperationId;
            result = await _taskPlacement.PlaceAsync(task.OperationId, target,
                () => !_disposed && _shown && epoch == _placementEpoch);
        }
        catch (Exception ex)
        {
            string message = TaskPlacementPolicy.ErrorMessage(ex);
            Ui(() => ShowFeedback(message, isError: true, taskId: task.OperationId));
            throw new InvalidOperationException(message, ex);
        }
        finally { Ui(() => _placingOperationId = null); }
        Ui(() => { ShowFeedback(result.Message, isError: result.State is "Missing" or "Undone", taskId: task.OperationId); global::Rhino.RhinoApp.WriteLine("Lux3D：" + result.Message); });
        // History refresh is independent of the completed document mutation.
        try { await ReloadAsync(task.OperationId); }
        catch { Ui(() => ShowFeedback(result.Message + global::Lux3D.Core.LocalText.Get(" The task list has not refreshed yet. Query it again later."), isError: result.State is "Missing" or "Undone", taskId: task.OperationId)); }
    }
    private async Task RefreshAutoTaskAsync(Guid operationId)
    {
        try
        {
            TaskRecord task = await _client.GetTaskAsync(operationId);
            if (!TaskPlacementPolicy.HasCachedAsset(task) && TaskPlacementPolicy.CanRefresh(task))
                task = await _client.RefreshAsync(operationId);
            Guid? selected = null;
            Ui(() => { selected = SelectedTask()?.OperationId; ClearRecoveredQueryError(task); ObserveTask(task); });
            await ReloadAsync(selected);
            await TryAutoPlaceAsync(task);
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                _autoPlacements.Remove(operationId);
                string message = TaskPlacementPolicy.ErrorMessage(ex) + global::Lux3D.Core.LocalText.Get(" Under Tasks, click Place in current document to retry the original task.");
                ShowFeedback(message, isError: true, taskId: operationId, background: _backgroundOperation, kind: FeedbackKind.Query);
                global::Rhino.RhinoApp.WriteLine("Lux3D：" + message);
            });
        }
    }
    private async Task TryAutoPlaceAsync(TaskRecord task)
    {
        TaskPlacementTarget? target = null;
        int epoch = 0;
        Ui(() =>
        {
            epoch = _placementEpoch;
            if (!_disposed && _shown) _autoPlacements.TryGetValue(task.OperationId, out target);
        });
        if (target is null) return;
        if (task.State is "Failed" or "ProviderCancelled" or "SubmissionUnknown" or "Rejected" or "Missing" or "DownloadFailed")
        {
            Ui(() =>
            {
                _autoPlacements.Remove(task.OperationId);
                ShowFeedback(TaskPlacementPolicy.NotReadyMessage(task) + global::Lux3D.Core.LocalText.Get(" Place the original task manually from the Tasks tab."), isError: true, taskId: task.OperationId, background: _backgroundOperation, kind: FeedbackKind.Query);
            });
            return;
        }
        if (task.State != "Ready")
        {
            Ui(() => ObserveTask(task));
            return;
        }
        try
        {
            Ui(() => _placingOperationId = task.OperationId);
            TaskPlacementResult result = await _taskPlacement.PlaceReadyAsync(task, target,
                () => !_disposed && _shown && !_paused && epoch == _placementEpoch && _autoPlacements.ContainsKey(task.OperationId));
            Ui(() =>
            {
                if (result.State != "Deferred") _autoPlacements.Remove(task.OperationId);
                ShowFeedback(result.Message, isError: result.State is "Missing" or "Undone", taskId: task.OperationId, background: _backgroundOperation);
                if (result.State != "Deferred") global::Rhino.RhinoApp.WriteLine("Lux3D：" + result.Message);
            });
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                _autoPlacements.Remove(task.OperationId);
                string message = TaskPlacementPolicy.ErrorMessage(ex) + global::Lux3D.Core.LocalText.Get(" Click Place in current document under Tasks.");
                ShowFeedback(message, isError: true, taskId: task.OperationId, background: _backgroundOperation);
                global::Rhino.RhinoApp.WriteLine("Lux3D：" + message);
            });
        }
        finally { Ui(() => _placingOperationId = null); }
    }
    private void RenderTask()
    {
        TaskRecord? r = SelectedTask();
        if (r is null) { _details.Text = global::Lux3D.Core.LocalText.Get("Select a task to view its result."); return; }
        string feedback = _taskFeedback.TryGetValue(r.OperationId, out var value) ? global::Lux3D.Core.LocalText.Get("\nSession result: ") + value.Message : "";
        _details.Text = global::Lux3D.Core.LocalText.Format("{0} · {1} · {2:N0} faces\n{3}{4}\n{5}\nTask ID: {6}", StateText(r.State), (r.Region == "cn" ? global::Lux3D.Core.LocalText.Get("China") : global::Lux3D.Core.LocalText.Get("Global")), r.FaceCount, r.Prompt, feedback, r.Error, r.OperationId);
    }
    private static string StateText(string state) => state switch
    {
        "Ready" => global::Lux3D.Core.LocalText.Get("Ready to place / preview"), "Rejected" => global::Lux3D.Core.LocalText.Get("Model generated; asset validation failed"), "Missing" => global::Lux3D.Core.LocalText.Get("Local model cache missing"), "Queued" => global::Lux3D.Core.LocalText.Get("Queued"), "Running" => global::Lux3D.Core.LocalText.Get("Generating"), "Succeeded" => global::Lux3D.Core.LocalText.Get("Generated; awaiting download"),
        "Failed" => global::Lux3D.Core.LocalText.Get("Generation failed"), "DownloadFailed" => global::Lux3D.Core.LocalText.Get("Download failed; query to retry"), "StatusUnknown" => global::Lux3D.Core.LocalText.Get("Status needs checking"),
        "Downloading" => global::Lux3D.Core.LocalText.Get("Downloading"), "Validating" => global::Lux3D.Core.LocalText.Get("Validating"), "Submitting" => global::Lux3D.Core.LocalText.Get("Submitting"),
        "SubmissionUnknown" => global::Lux3D.Core.LocalText.Get("Submission result unknown"), "ProviderCancelled" => global::Lux3D.Core.LocalText.Get("Cancelled by provider"), _ => state,
    };
    private void AddSelectedCandidate()
    {
        TaskRecord r = SelectedTask() ?? throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Select a task first."));
        if (r.State != "Ready" || r.AssetPath is null) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Wait for the model to finish downloading and validating before adding a candidate."));
        AddCandidate(r.AssetPath, r.AssetSha256, r.Prompt.Length > 24 ? r.Prompt[..24] : r.Prompt);
    }
    private void AddLocalCandidate()
    {
        using var dialog = new OpenFileDialog { Title = global::Lux3D.Core.LocalText.Get("Load local GLB (does not submit generation)"), MultiSelect = false };
        dialog.Filters.Add(new FileFilter("glTF Binary", ".glb"));
        if (dialog.ShowDialog(this) == DialogResult.Ok) AddCandidate(dialog.FileName, null, Path.GetFileName(dialog.FileName));
    }
    private void AddCandidate(string path, string? sha, string name)
    {
        if (_candidates.Count >= 3) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Up to 3 candidates per group. Remove an unwanted candidate first."));
        PreparedAsset asset = _host.Prepare(path, sha, TargetDocument());
        _candidates.Add((name, asset, Guid.NewGuid()));
        SelectCandidate(_candidates.Count - 1);
        ShowFeedback(global::Lux3D.Core.LocalText.Get("Candidate loaded. Change height, angle or position under Generate to update the preview. It is added to the document only when accepted."));
    }
    private void SelectCandidate(int index)
    {
        _candidate.DataStore = _candidates.Select((c, i) => $"{(char)('A' + i)} · {c.Name}").ToArray();
        _candidate.SelectedIndex = index;
        SchedulePreview();
    }
    private void RemoveCandidate()
    {
        int index = _candidate.SelectedIndex;
        if (index < 0 || index >= _candidates.Count) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Select a candidate to remove first."));
        _previewTimer.Stop(); _preview.Clear();
        PreparedAsset asset = _candidates[index].Asset;
        _candidates.RemoveAt(index);
        try { asset.Dispose(); }
        finally { SelectCandidate(Math.Min(index, _candidates.Count - 1)); }
        ShowFeedback(global::Lux3D.Core.LocalText.Get("Candidate removed; the original task and model cache are retained."));
    }
    private PlacementIntent Placement() => new(_plane, _point, _height.Value, _rotation.Value);
    private void PickPlacement()
    {
        RhinoDoc doc = TargetDocument();
        if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != _serial) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Activate the document linked to this panel first."));
        using var get = new GetPoint();
        get.SetCommandPrompt(global::Lux3D.Core.LocalText.Get("Choose Lux3D placement point"));
        if (get.Get() != global::Rhino.Input.GetResult.Point) return;
        _point = get.Point();
        _plane = doc.Views.ActiveView.ActiveViewport.ConstructionPlane();
        _planeDescription = global::Lux3D.Core.LocalText.Get("Selected view's construction plane");
        UpdatePlacement();
    }
    private void UpdatePlacement()
    {
        UpdatePlacementSummary();
        SchedulePreview();
    }
    private void UpdatePlacementSummary()
    {
        string units = UnitName(RhinoDoc.FromRuntimeSerialNumber(_serial)?.ModelUnitSystem);
        _heightLabel.Text = global::Lux3D.Core.LocalText.Get("Model height (") + units + "）";
        _placement.Text = string.Format(CultureInfo.InvariantCulture, global::Lux3D.Core.LocalText.Get("Position ({0:G5}, {1:G5}, {2:G5}) · {3}"), _point.X, _point.Y, _point.Z, _planeDescription);
        _candidatePlacement.Text = global::Lux3D.Core.LocalText.Format("Placement height {0:G5} {1} · Rotation {2:G5}°\n", _height.Value, units, _rotation.Value) + _placement.Text;
    }
    private void SchedulePreview()
    {
        if (_disposed || !_shown) return;
        _previewTimer.Stop(); _previewTimer.Start();
    }
    private void ShowPreview()
    {
        if (_disposed) return;
        _previewTimer.Stop();
        if (!_shown || _candidate.SelectedIndex < 0 || _candidate.SelectedIndex >= _candidates.Count) return;
        try { _preview.Show(_candidates[_candidate.SelectedIndex].Asset, Placement()); }
        catch (Exception ex)
        {
            try { _preview.Clear(); } catch { }
            ShowFeedback(global::Lux3D.Core.LocalText.Get("Preview did not complete: ") + ex.Message, isError: true);
        }
    }
    private void AcceptCandidate()
    {
        if (_candidate.SelectedIndex < 0) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Load and select a candidate first."));
        ImportReceipt receipt = _host.Commit(_candidates[_candidate.SelectedIndex].Asset, Placement(), _candidates[_candidate.SelectedIndex].ImportId);
        string cleanup = string.Empty;
        try { ClearCandidates(); }
        catch { cleanup = global::Lux3D.Core.LocalText.Get(" Preview cleanup did not complete. Click Exit comparison / Clear candidates; the imported document result is unaffected."); }
        ShowFeedback(receipt.State == "Imported"
            ? global::Lux3D.Core.LocalText.Get("An independent block was created; one Undo reverses it.") + cleanup
            : global::Lux3D.Core.LocalText.Get("The previously accepted candidate was undone or deleted; no duplicate was inserted. Restore it with Redo or explicitly create a new candidate.") + cleanup,
            isError: receipt.State != "Imported");
        global::Rhino.RhinoApp.WriteLine(global::Lux3D.Core.LocalText.Get("Lux3D candidate: ") + receipt.State + global::Lux3D.Core.LocalText.Get(", object ") + receipt.ObjectId);
    }
    private void ClearCandidates()
    {
        _previewTimer.Stop();
        _preview.Clear();
        foreach (var item in _candidates) item.Asset.Dispose();
        _candidates.Clear(); _candidate.DataStore = Array.Empty<string>();
    }
    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
    {
        if (_disposed) return;
        _shown = true; _timer.Start(); UpdatePlacementSummary(); ShowPreview();
        if (_busy) _elapsedTimer.Start();
        _ = RunAsync(async () => { SetConnection(await _client.GetConnectionAsync(Region())); await ReloadAsync(null); }, global::Lux3D.Core.LocalText.Get("Read connection and local history"), background: true);
    }
    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { _shown = false; _timer.Stop(); _elapsedTimer.Stop(); _previewTimer.Stop(); _preview.Clear(); }
    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument) { _shown = false; _placementEpoch++; _autoPlacements.Clear(); _timer.Stop(); _elapsedTimer.Stop(); ClearCandidates(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true; _placementEpoch++; _autoPlacements.Clear();
            _taskPlacement.ProgressChanged -= PlacementProgressChanged;
            _timer.Stop(); _timer.Dispose(); _elapsedTimer.Stop(); _elapsedTimer.Dispose(); _previewTimer.Stop();
            try { ClearCandidates(); }
            finally { _previewTimer.Dispose(); _preview.Dispose(); }
        }
        base.Dispose(disposing);
    }
}
