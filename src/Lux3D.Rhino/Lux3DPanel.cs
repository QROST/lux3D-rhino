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
    private readonly DropDown _region = new() { DataStore = new[] { "国际区", "中国区" }, SelectedIndex = 0 };
    private readonly Label _connection = Status("尚未连接");
    private readonly Label _generationConnection = Status("生成需要区域 API Key；OAuth 登录为可选操作。");
    private readonly PasswordBox _apiKey = new();
    private readonly CheckBox _persist = new() { Text = "保存到本机安全存储", Checked = false };
    private readonly TextArea _prompt = new() { Height = 90, Wrap = true, Text = "" };
    private readonly TextBox _image = new() { ReadOnly = true, PlaceholderText = "可选：一张 PNG/JPEG 照片" };
    private readonly NumericStepper _faces = new() { MinValue = 10000, MaxValue = 300000, Increment = 5000, DecimalPlaces = 0, Value = 50000 };
    private readonly CheckBox _autoPlace = new() { Text = "生成完成后放入当前文档", Checked = true, ToolTip = "新任务会使用提交时的文档、位置、高度和角度；原文档必须仍处于活动状态。关闭面板后改为手动放入。" };
    private readonly CheckBox _pbr = new() { Text = "请求 PBR 材质", Checked = true };
    private readonly Button _generate = new() { Text = "生成（将消耗服务额度）" };
    private readonly ListBox _tasks = new() { Height = 130 };
    private readonly TextArea _details = new() { ReadOnly = true, Height = 95, Wrap = true };
    private readonly DropDown _candidate = new();
    private readonly NumericStepper _height = new() { MinValue = 0.001, MaxValue = 1e9, DecimalPlaces = 3, Value = 1000 };
    private readonly Label _heightLabel = Status("模型高度（文档单位）");
    private readonly NumericStepper _rotation = new() { MinValue = -360, MaxValue = 360, DecimalPlaces = 1, Value = 0 };
    private readonly Label _placement = Status("原点 / 世界 XY 平面；高度使用目标文档单位");
    private readonly Label _candidatePlacement = Status("");
    private readonly Label _currentTask = Status("尚未提交新任务；已有结果可在“任务 / 候选”页查看。");
    private readonly Label _operation = Status("就绪");
    private readonly TextArea _status = new() { ReadOnly = true, Height = 76, Wrap = true, Text = "生成完成后默认放入当前文档；任务和模型会保存在本地。" };
    private readonly TabControl _tabs = new();
    private readonly Button _pause = new() { Text = "暂停自动查询" };
    private readonly List<Button> _networkButtons = new();
    private readonly Label _pageLabel = Status("第 1 页，每页 30 条");
    private readonly Button _previousPage;
    private readonly Button _nextPage;
    private readonly Dictionary<Guid, TaskPlacementTarget> _autoPlacements = new();
    private readonly Dictionary<Guid, TaskFeedback> _taskFeedback = new();
    private TaskRecord? _recentTask;
    private Guid? _placingOperationId;
    private string _operationPhase = "";
    private string _planeDescription = "世界 XY 平面";
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
        Button login = NetworkButton("OAuth 登录（可选）", async () => SetConnection(await _client.LoginAsync(Region())));
        Button refreshConnection = NetworkButton("连接状态", async () => SetConnection(await _client.GetConnectionAsync(Region())));
        Button logout = NetworkButton("断开并清除凭证", async () => SetConnection(await _client.LogoutAsync(Region())));
        Button saveKey = NetworkButton("设置 API Key", async () =>
        {
            string key = _apiKey.Text;
            _apiKey.Text = string.Empty;
            SetConnection(await _client.SetApiKeyAsync(Region(), key, _persist.Checked == true));
        });
        _region.SelectedIndexChanged += (_, _) =>
        {
            _canGenerate = false; _generate.Enabled = false;
            _connection.Text = "区域已改变，请设置此区域 API Key，或点击“连接状态”读取已保存的连接。";
            UpdateGenerationConnection();
        };
        Button pickImage = LocalButton("选择照片", ChooseImage);
        Button clearImage = LocalButton("清除照片", () => _image.Text = string.Empty);
        _generate.Click += (_, _) => _ = RunAsync(GenerateAsync, "提交生成任务");
        _networkButtons.Add(_generate);
        _generate.Enabled = false;
        Button refresh = NetworkButton("查询选中任务", RefreshSelectedAsync);
        Button place = NetworkButton("放入当前文档", PlaceSelectedTaskAsync);
        Button reload = NetworkButton("读取本地历史", ReloadAsync);
        _previousPage = NetworkButton("上一页", async () => { if (_page > 0) { _page--; await ReloadAsync(null); } });
        _nextPage = NetworkButton("下一页", async () => { if (_records.Count == 30) { _page++; await ReloadAsync(null); } });
        _previousPage.Enabled = false; _nextPage.Enabled = false;
        _pause.Click += (_, _) =>
        {
            _paused = !_paused;
            _pause.Text = _paused ? "恢复自动查询" : "暂停自动查询";
            ShowFeedback(_paused ? "已暂停自动查询与等待中的自动放置；远程生成和已开始的请求不会取消。可手动查询或放入原任务。" : "已恢复自动查询与等待中的自动放置。");
        };
        _tasks.SelectedIndexChanged += (_, _) => RenderTask();
        Button add = LocalButton("加入候选（最多 3 项）", AddSelectedCandidate);
        Button local = LocalButton("加载本地 GLB", AddLocalCandidate);
        Button pick = LocalButton("选择放置点", PickPlacement);
        Button reset = LocalButton("放在世界原点", () => { _plane = Plane.WorldXY; _point = Point3d.Origin; _planeDescription = "世界 XY 平面"; UpdatePlacement(); });
        Button accept = LocalButton("接受当前候选", AcceptCandidate);
        Button remove = LocalButton("移除当前候选", RemoveCandidate);
        Button clear = LocalButton("退出比较 / 清空候选", ClearCandidates);
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
                _ = RunAsync(() => RefreshAutoTaskAsync(operationId), "自动查询原任务", background: true);
                return;
            }
            TaskRecord? record = SelectedTask();
            if (record is not null && record.State is "Queued" or "Running" or "StatusUnknown" or "Succeeded" or "DownloadFailed")
                _ = RunAsync(RefreshSelectedAsync, "自动查询原任务", background: true);
        };
        _tabs.Pages.Add(new TabPage { Text = "生成", Content = Layout(
                    _generationConnection, LocalButton("设置连接", () => _tabs.SelectedIndex = 2),
                    Status("文字或单张照片；提交后输入会固定到任务记录。"), _prompt,
                    _image, Row(pickImage, clearImage), Status("模型：G1-Turbo"),
                    Row(new Label { Text = "目标面数" }, _faces), _pbr, _autoPlace,
                    Row(_heightLabel, _height),
                    Row(new Label { Text = "旋转角度（°）" }, _rotation), _placement, Row(pick, reset),
                    Status("默认高度为 1 米；生成时记住这里的位置、尺寸和角度。"), _generate, _currentTask,
                    LocalButton("查看任务与结果", () => _tabs.SelectedIndex = 1),
                    Status("创建结果未知时不会自动重试；重新生成可能再次消耗额度。")) });
        _tabs.Pages.Add(new TabPage { Text = "任务 / 候选", Content = Layout(_tasks, Row(_previousPage, _pageLabel, _nextPage), Row(reload, refresh), _pause, _details, place,
                    Status("再次放入同一任务会定位已有 Block；需要额外实例时，加入候选后接受。"),
                    _candidatePlacement, LocalButton("调整位置、尺寸与角度", () => _tabs.SelectedIndex = 0),
                    Row(add, local), _candidate,
                    Status("候选预览显示几何和基础颜色；接受后可在 Rendered 模式查看材质。"), accept, Row(remove, clear)) });
        _tabs.Pages.Add(new TabPage { Text = "连接", Content = Layout(
                    Status("生成需要 API Key。OAuth 登录为可选操作，不能替代当前版本的生成鉴权。"),
                    _region, _connection, new Label { Text = "区域 API Key" }, _apiKey, _persist, saveKey,
                    Status("验证只查询任务列表，不检查剩余额度。Key 不会存入 Rhino 文档。"),
                    Row(refreshConnection, logout), login,
                    Status("OAuth 会在系统浏览器打开 Manycore 授权页面；登录后仍需 API Key 才能生成。")) });
        UpdatePlacementSummary();
        Content = new StackLayout { Padding = 10, Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { new Label { Text = "Lux3D for Rhino", Font = new Font(SystemFont.Bold, 14) },
                Status($"目标文档：{doc?.Name ?? "未保存文档"}"), new StackLayoutItem(_tabs, true), _operation, _status } };
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
    private RhinoDoc TargetDocument() => RhinoDoc.FromRuntimeSerialNumber(_serial) ?? throw new InvalidOperationException("目标文档已关闭；请在目标文档重新打开面板。");
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
            Ui(() => { if (!_disposed && !background && _feedbackRevision == feedbackRevision) ShowFeedback(operation + "已完成。"); });
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
                    _operation.Text = operation + (failed ? "未完成" : "结束") + " · 用时 " + ElapsedText(_operationClock.Elapsed);
                    foreach (Button button in _networkButtons) button.Enabled = true;
                    _region.Enabled = true; _generate.Enabled = _canGenerate; _previousPage.Enabled = _page > 0; _nextPage.Enabled = _records.Count == 30;
                }
            });
        }
    }
    private void ChooseImage()
    {
        using var dialog = new OpenFileDialog { Title = "选择单张 PNG/JPEG 照片", MultiSelect = false };
        dialog.Filters.Add(new FileFilter("图片", ".png", ".jpg", ".jpeg"));
        if (dialog.ShowDialog(this) == DialogResult.Ok) { ValidateImage(dialog.FileName); _image.Text = dialog.FileName; }
    }
    private static void Ui(Action action)
    {
        if (global::Rhino.RhinoApp.InvokeRequired) Application.Instance.Invoke(action); else action();
    }
    private static string ElapsedText(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒" : $"{Math.Max(0, (int)elapsed.TotalSeconds)} 秒";
    private void UpdateOperation()
    {
        if (!_disposed && _busy) _operation.Text = _operationPhase + " · 已用 " + ElapsedText(_operationClock.Elapsed);
    }
    private void PlacementProgressChanged(TaskPlacementProgress progress)
    {
        // Placement is a shared service. Do not show another panel's document or task activity here.
        if (_disposed) return;
        Ui(() =>
        {
            if (_disposed || progress.DocumentSerial != _serial || _placingOperationId != progress.OperationId) return;
            _operationPhase = progress.Message;
            _operation.Text = progress.Message + " · 放入已用 " + ElapsedText(progress.Elapsed);
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
        _status.Text = (isError ? "需要处理：" : "") + message;
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
            state = feedback.IsError ? "需要处理，请查看下方提示或任务详情" : feedback.Message;
        _currentTask.Text = "最近生成：" + ShortPrompt(task.Prompt) + "\n" + state;
    }
    private static string ShortPrompt(string prompt)
    {
        string value = prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length == 0 ? "照片生成" : value.Length > 60 ? value[..60] + "…" : value;
    }
    private static bool HasTaskIssue(TaskRecord task) => task.State is "Failed" or "ProviderCancelled" or "SubmissionUnknown" or "Rejected" or "Missing" or "DownloadFailed" or "StatusUnknown";
    private static string UnitName(global::Rhino.UnitSystem? units) => units switch
    {
        global::Rhino.UnitSystem.Millimeters => "毫米",
        global::Rhino.UnitSystem.Centimeters => "厘米",
        global::Rhino.UnitSystem.Meters => "米",
        global::Rhino.UnitSystem.Inches => "英寸",
        global::Rhino.UnitSystem.Feet => "英尺",
        null => "文档单位",
        _ => units.ToString() ?? "文档单位",
    };
    private void UpdateGenerationConnection()
    {
        string region = Region() == "cn" ? "中国区" : "国际区";
        _generationConnection.Text = _canGenerate
            ? region + " · API Key 已配置；生成仍受服务权限及额度约束。"
            : region + " · 生成需要 API Key，请先设置连接。OAuth 登录为可选操作。";
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
        if (new FileInfo(path).Length > 20_000_000) throw new InvalidOperationException("图片不能超过 20 MB。");
        byte[] data = File.ReadAllBytes(path);
        ImageInputValidator.Validate(data);
        using var stream = new MemoryStream(data, writable: false);
        using var bitmap = new Bitmap(stream);
        if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Width > 8192 || bitmap.Height > 8192 || (long)bitmap.Width * bitmap.Height > 32_000_000)
            throw new InvalidOperationException("图片无法完整解码或超出尺寸预算。");
        // Lock forces native pixel decoding instead of accepting only an image header.
        using var pixels = bitmap.Lock();
        _ = pixels.GetPixel(bitmap.Width - 1, bitmap.Height - 1);
    }
    private async Task GenerateAsync()
    {
        if (!_canGenerate) throw new InvalidOperationException("生成需要所选区域的 API Key；请在连接页配置。OAuth 登录不能替代 API Key。");
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
                if (!_disposed) result = MessageBox.Show(this, "已有提交结果未知的任务。请先在服务端核对；继续可能产生另一笔生成费用。仍要创建新任务？", "确认新生成", MessageBoxButtons.YesNo, MessageBoxType.Warning);
            });
            if (result != DialogResult.Yes) { Ui(() => ShowFeedback("未创建新任务。请先核对之前提交结果未知的任务。")); return; }
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
            ShowFeedback(automaticScheduled ? "任务已提交；完成后将按提交时的位置放入原文档。" : "任务已提交；完成后可从任务页放入当前文档。");
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
            _pageLabel.Text = $"第 {_page + 1} 页，每页 30 条";
            _tasks.DataStore = _records.Select(r => $"{r.CreatedAt.LocalDateTime:MM-dd HH:mm} · {StateText(r.State)} · {ShortPrompt(r.Prompt)}").ToArray();
            _tasks.SelectedIndex = selected.HasValue ? _records.FindIndex(r => r.OperationId == selected) : _records.Count > 0 ? 0 : -1;
            RenderTask();
        });
    }
    private async Task RefreshSelectedAsync()
    {
        TaskRecord record = SelectedTask() ?? throw new InvalidOperationException("请先选择任务。");
        TaskRecord refreshed;
        try { refreshed = await _client.RefreshAsync(record.OperationId); }
        catch (Exception ex)
        {
            Ui(() => ShowFeedback("查询原任务未完成：" + ex.Message + " 可稍后再次查询，无需重新生成。", isError: true, taskId: record.OperationId, background: _backgroundOperation, kind: FeedbackKind.Query));
            throw;
        }
        Ui(() =>
        {
            ClearRecoveredQueryError(refreshed);
            ObserveTask(refreshed);
            if (HasTaskIssue(refreshed)) ShowFeedback(TaskPlacementPolicy.NotReadyMessage(refreshed), isError: true, taskId: refreshed.OperationId, background: _backgroundOperation, kind: FeedbackKind.Query);
            else if (!_backgroundOperation) ShowFeedback("原任务：" + StateText(refreshed.State) + (refreshed.State == "Ready" ? "。可放入当前文档，或加入候选预览。" : "。"));
        });
        await ReloadAsync(record.OperationId);
        await TryAutoPlaceAsync(refreshed);
    }
    private async Task PlaceSelectedTaskAsync()
    {
        TaskRecord task = SelectedTask() ?? throw new InvalidOperationException("请先选择要放入的任务。");
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
        catch { Ui(() => ShowFeedback(result.Message + " 任务列表暂未刷新，可稍后重新查询。", isError: result.State is "Missing" or "Undone", taskId: task.OperationId)); }
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
                string message = TaskPlacementPolicy.ErrorMessage(ex) + " 请从任务页点击“放入当前文档”重试原任务。";
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
                ShowFeedback(TaskPlacementPolicy.NotReadyMessage(task) + " 可从任务页手动放入原任务。", isError: true, taskId: task.OperationId, background: _backgroundOperation, kind: FeedbackKind.Query);
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
                string message = TaskPlacementPolicy.ErrorMessage(ex) + " 请从任务页点击“放入当前文档”。";
                ShowFeedback(message, isError: true, taskId: task.OperationId, background: _backgroundOperation);
                global::Rhino.RhinoApp.WriteLine("Lux3D：" + message);
            });
        }
        finally { Ui(() => _placingOperationId = null); }
    }
    private void RenderTask()
    {
        TaskRecord? r = SelectedTask();
        if (r is null) { _details.Text = "选择任务查看结果。"; return; }
        string feedback = _taskFeedback.TryGetValue(r.OperationId, out var value) ? "\n本次会话结果：" + value.Message : "";
        _details.Text = $"{StateText(r.State)} · {(r.Region == "cn" ? "中国区" : "国际区")} · {r.FaceCount:N0} 面\n{r.Prompt}{feedback}\n{r.Error}\n任务编号：{r.OperationId}";
    }
    private static string StateText(string state) => state switch
    {
        "Ready" => "可放入 / 预览", "Rejected" => "模型已生成，资产校验未通过", "Missing" => "本地模型缓存缺失", "Queued" => "排队中", "Running" => "生成中", "Succeeded" => "生成成功，待下载",
        "Failed" => "生成失败", "DownloadFailed" => "下载失败，可重试查询", "StatusUnknown" => "状态待核对",
        "Downloading" => "下载中", "Validating" => "校验中", "Submitting" => "提交中",
        "SubmissionUnknown" => "提交结果未知", "ProviderCancelled" => "服务端已取消", _ => state,
    };
    private void AddSelectedCandidate()
    {
        TaskRecord r = SelectedTask() ?? throw new InvalidOperationException("请先选择任务。");
        if (r.State != "Ready" || r.AssetPath is null) throw new InvalidOperationException("请等待模型下载和校验完成后再加入候选。");
        AddCandidate(r.AssetPath, r.AssetSha256, r.Prompt.Length > 24 ? r.Prompt[..24] : r.Prompt);
    }
    private void AddLocalCandidate()
    {
        using var dialog = new OpenFileDialog { Title = "加载本地 GLB（不会提交生成）", MultiSelect = false };
        dialog.Filters.Add(new FileFilter("glTF Binary", ".glb"));
        if (dialog.ShowDialog(this) == DialogResult.Ok) AddCandidate(dialog.FileName, null, Path.GetFileName(dialog.FileName));
    }
    private void AddCandidate(string path, string? sha, string name)
    {
        if (_candidates.Count >= 3) throw new InvalidOperationException("每组最多 3 个候选；请先移除不需要的候选。");
        PreparedAsset asset = _host.Prepare(path, sha, TargetDocument());
        _candidates.Add((name, asset, Guid.NewGuid()));
        SelectCandidate(_candidates.Count - 1);
        ShowFeedback("候选已加载；调整生成页的高度、角度或位置即可更新预览。接受后才会放入文档。");
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
        if (index < 0 || index >= _candidates.Count) throw new InvalidOperationException("请先选择要移除的候选。");
        _previewTimer.Stop(); _preview.Clear();
        PreparedAsset asset = _candidates[index].Asset;
        _candidates.RemoveAt(index);
        try { asset.Dispose(); }
        finally { SelectCandidate(Math.Min(index, _candidates.Count - 1)); }
        ShowFeedback("已移除当前候选；原任务和模型缓存保留。");
    }
    private PlacementIntent Placement() => new(_plane, _point, _height.Value, _rotation.Value);
    private void PickPlacement()
    {
        RhinoDoc doc = TargetDocument();
        if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != _serial) throw new InvalidOperationException("请先激活面板绑定的目标文档。");
        using var get = new GetPoint();
        get.SetCommandPrompt("选择 Lux3D 放置点");
        if (get.Get() != global::Rhino.Input.GetResult.Point) return;
        _point = get.Point();
        _plane = doc.Views.ActiveView.ActiveViewport.ConstructionPlane();
        _planeDescription = "所选视图的工作平面";
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
        _heightLabel.Text = "模型高度（" + units + "）";
        _placement.Text = string.Format(CultureInfo.InvariantCulture, "位置 ({0:G5}, {1:G5}, {2:G5}) · {3}", _point.X, _point.Y, _point.Z, _planeDescription);
        _candidatePlacement.Text = $"放置高度 {_height.Value:G5} {units} · 旋转 {_rotation.Value:G5}°\n" + _placement.Text;
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
            ShowFeedback("预览未完成：" + ex.Message, isError: true);
        }
    }
    private void AcceptCandidate()
    {
        if (_candidate.SelectedIndex < 0) throw new InvalidOperationException("请先加载并选择候选。");
        ImportReceipt receipt = _host.Commit(_candidates[_candidate.SelectedIndex].Asset, Placement(), _candidates[_candidate.SelectedIndex].ImportId);
        string cleanup = string.Empty;
        try { ClearCandidates(); }
        catch { cleanup = " 预览清理未完成，可点击“退出比较 / 清空候选”；文档导入结果不受影响。"; }
        ShowFeedback(receipt.State == "Imported"
            ? "已创建独立 Block；一次 Undo 可撤销。" + cleanup
            : "此前接受的候选已被撤销或删除，没有重复插入；可用 Redo 恢复或明确新建候选。" + cleanup,
            isError: receipt.State != "Imported");
        global::Rhino.RhinoApp.WriteLine("Lux3D 候选：" + receipt.State + "，对象 " + receipt.ObjectId);
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
        _ = RunAsync(async () => { SetConnection(await _client.GetConnectionAsync(Region())); await ReloadAsync(null); }, "读取连接与本地历史", background: true);
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
