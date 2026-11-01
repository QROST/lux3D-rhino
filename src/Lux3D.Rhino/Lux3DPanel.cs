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
    private readonly uint _serial;
    private readonly Lux3DClient _client;
    private readonly Lux3DHostService _host;
    private readonly TaskPlacementService _taskPlacement;
    private readonly CandidatePreview _preview = new();
    private readonly List<(string Name, PreparedAsset Asset, Guid ImportId)> _candidates = new();
    private readonly List<TaskRecord> _records = new();
    private readonly UITimer _timer = new() { Interval = 5 };
    private readonly DropDown _region = new() { DataStore = new[] { "国际区 global", "中国区 cn" }, SelectedIndex = 0 };
    private readonly Label _connection = Status("尚未连接");
    private readonly PasswordBox _apiKey = new();
    private readonly CheckBox _persist = new() { Text = "保存到本机安全存储", Checked = false };
    private readonly TextArea _prompt = new() { Height = 90, Wrap = true, Text = "" };
    private readonly TextBox _image = new() { ReadOnly = true, PlaceholderText = "可选：一张 PNG/JPEG 照片" };
    private readonly DropDown _model = new() { DataStore = new[] { "G1-Turbo" }, SelectedIndex = 0 };
    private readonly NumericStepper _faces = new() { MinValue = 10000, MaxValue = 300000, Increment = 5000, DecimalPlaces = 0, Value = 50000 };
    private readonly CheckBox _autoPlace = new() { Text = "生成完成后放入当前文档", Checked = true, ToolTip = "新任务会使用提交时的文档、位置、高度和角度；原文档必须仍处于活动状态。关闭面板后改为手动放入。" };
    private readonly CheckBox _pbr = new() { Text = "请求 PBR 材质", Checked = true };
    private readonly Button _generate = new() { Text = "生成（将消耗服务额度）" };
    private readonly ListBox _tasks = new() { Height = 130 };
    private readonly TextArea _details = new() { ReadOnly = true, Height = 95, Wrap = true };
    private readonly DropDown _candidate = new();
    private readonly NumericStepper _height = new() { MinValue = 0.001, MaxValue = 1e9, DecimalPlaces = 3, Value = 1000 };
    private readonly NumericStepper _rotation = new() { MinValue = -360, MaxValue = 360, DecimalPlaces = 1, Value = 0 };
    private readonly Label _placement = Status("原点 / 世界 XY 平面；高度使用目标文档单位");
    private readonly Label _status = Status("默认生成完成后放入当前文档；结果也会保存在本地任务历史。");
    private readonly List<Button> _networkButtons = new();
    private readonly Label _pageLabel = Status("第 1 页，每页 30 条");
    private readonly Button _previousPage;
    private readonly Button _nextPage;
    private readonly Dictionary<Guid, TaskPlacementTarget> _autoPlacements = new();
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
        Button login = NetworkButton("Manycore OAuth 登录", async () => SetConnection(await _client.LoginAsync(Region())));
        Button refreshConnection = NetworkButton("连接状态", async () => SetConnection(await _client.GetConnectionAsync(Region())));
        Button logout = NetworkButton("断开并清除凭证", async () => SetConnection(await _client.LogoutAsync(Region())));
        Button saveKey = NetworkButton("设置 API Key", async () =>
        {
            string key = _apiKey.Text;
            _apiKey.Text = string.Empty;
            SetConnection(await _client.SetApiKeyAsync(Region(), key, _persist.Checked == true));
        });
        _region.SelectedIndexChanged += (_, _) => { _canGenerate = false; _generate.Enabled = false; _connection.Text = "区域已改变；任务仍使用各自提交时的区域。"; };
        Button pickImage = LocalButton("选择照片", ChooseImage);
        Button clearImage = LocalButton("清除照片", () => _image.Text = string.Empty);
        _generate.Click += (_, _) => _ = RunAsync(GenerateAsync);
        _networkButtons.Add(_generate);
        _generate.Enabled = false;
        Button refresh = NetworkButton("查询选中任务", RefreshSelectedAsync);
        Button place = NetworkButton("放入当前文档", PlaceSelectedTaskAsync);
        Button reload = NetworkButton("读取本地历史", ReloadAsync);
        _previousPage = NetworkButton("上一页", async () => { if (_page > 0) { _page--; await ReloadAsync(null); } });
        _nextPage = NetworkButton("下一页", async () => { if (_records.Count == 30) { _page++; await ReloadAsync(null); } });
        _previousPage.Enabled = false; _nextPage.Enabled = false;
        Button pause = LocalButton("停止 / 恢复等待", () =>
        {
            _paused = !_paused;
            _status.Text = _paused ? "已停止本地轮询；远程任务仍可继续。" : "已恢复本地轮询。";
        });
        _tasks.SelectedIndexChanged += (_, _) => RenderTask();
        Button add = LocalButton("加入候选（最多 3 项）", AddSelectedCandidate);
        Button local = LocalButton("加载本地 GLB", AddLocalCandidate);
        Button pick = LocalButton("拾取点并使用当前 CPlane", PickPlacement);
        Button reset = LocalButton("重置为世界原点", () => { _plane = Plane.WorldXY; _point = Point3d.Origin; UpdatePlacement(); });
        Button accept = LocalButton("接受当前候选", AcceptCandidate);
        Button clear = LocalButton("退出比较 / 清空候选", ClearCandidates);
        _candidate.SelectedIndexChanged += (_, _) => ShowPreview();
        _height.ValueChanged += (_, _) => ShowPreview();
        _rotation.ValueChanged += (_, _) => ShowPreview();
        _timer.Elapsed += (_, _) =>
        {
            if (!_shown || _paused || _busy || _disposed) return;
            if (_autoPlacements.Count > 0)
            {
                _autoPollIndex %= _autoPlacements.Count;
                Guid operationId = _autoPlacements.Keys.ElementAt(_autoPollIndex++);
                _ = RunAsync(() => RefreshAutoTaskAsync(operationId));
                return;
            }
            TaskRecord? record = SelectedTask();
            if (record is not null && record.State is "Queued" or "Running" or "StatusUnknown" or "Succeeded" or "DownloadFailed")
                _ = RunAsync(RefreshSelectedAsync);
        };
        var tabs = new TabControl
        {
            Pages =
            {
                new TabPage { Text = "生成", Content = Layout(
                    Status("文字或单张照片；提交后输入会固定到任务记录。"), _prompt,
                    _image, Row(pickImage, clearImage), Row(new Label { Text = "模型" }, _model),
                    Row(new Label { Text = "目标面数" }, _faces), _pbr, _autoPlace,
                    Status("放置使用“任务 / 候选”页的参考高度和位置；默认高度为 1 米换算到文档单位。"), _generate,
                    Status("未知提交结果不会自动重试；重新生成可能再次消耗额度。")) },
                new TabPage { Text = "任务 / 候选", Content = Layout(_tasks, Row(_previousPage, _pageLabel, _nextPage), Row(reload, refresh), pause, _details, place,
                    Status("同一任务再次放入只定位已有 Block；需要新实例时使用候选。"), add, local, _candidate, Row(new Label { Text = "参考高度" }, _height),
                    Row(new Label { Text = "平面内角度 °" }, _rotation), _placement, pick, reset,
                    Status("预览显示几何和基础颜色；完整 PBR 请接受后在 Rendered 模式检查。"), accept, clear) },
                new TabPage { Text = "连接", Content = Layout(_region, _connection, login,
                    Status("OAuth 将打开系统浏览器，由您完成 Manycore 授权。API Key 是独立连接方式。"),
                    new Label { Text = "API Key（不会存入 .3dm）" }, _apiKey, _persist, saveKey,
                    Row(refreshConnection, logout)) },
            }
        };
        Content = new StackLayout { Padding = 10, Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { new Label { Text = "Lux3D for Rhino", Font = new Font(SystemFont.Bold, 14) },
                Status($"目标：{doc?.Name ?? "未保存文档"} / 会话 {_serial}"), new StackLayoutItem(tabs, true), _status } };
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
        button.Click += (_, _) => _ = RunAsync(action);
        _networkButtons.Add(button);
        return button;
    }
    private Button LocalButton(string text, Action action)
    {
        var button = new Button { Text = text };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { _status.Text = ex.Message; } };
        return button;
    }
    private string Region() => _region.SelectedIndex == 1 ? "cn" : "global";
    private RhinoDoc TargetDocument() => RhinoDoc.FromRuntimeSerialNumber(_serial) ?? throw new InvalidOperationException("目标文档已关闭；请在目标文档重新打开面板。");
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true;
        foreach (Button button in _networkButtons) button.Enabled = false;
        _status.Text = "正在处理…";
        try { await action(); Ui(() => { if (!_disposed && _status.Text == "正在处理…") _status.Text = "已更新。"; }); }
        catch (Exception ex) { Ui(() => { if (!_disposed) _status.Text = ex.Message; global::Rhino.RhinoApp.WriteLine("Lux3D：" + ex.Message); }); }
        finally
        {
            Ui(() =>
            {
                _busy = false;
                if (!_disposed) { foreach (Button button in _networkButtons) button.Enabled = true; _generate.Enabled = _canGenerate; _previousPage.Enabled = _page > 0; _nextPage.Enabled = _records.Count == 30; }
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
    private void SetConnection(ConnectionInfo info) => Ui(() =>
    {
        if (_disposed) return;
        _canGenerate = info.Region == Region() && info.CanGenerate;
        _connection.Text = info.Message;
        _generate.Enabled = !_busy && _canGenerate;
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
        if (!_canGenerate) throw new InvalidOperationException("此连接尚无已验证的 Lux3D API 权限。请先在连接页配置。");
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
            if (result != DialogResult.Yes) return;
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
            _status.Text = automaticScheduled ? "任务已提交；完成后将按提交时的位置放入原文档。" : "任务已提交；完成后可从任务页放入当前文档。";
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
            _pageLabel.Text = $"第 {_page + 1} 页，每页 30 条";
            _tasks.DataStore = _records.Select(r => $"{r.CreatedAt.LocalDateTime:MM-dd HH:mm} [{StateText(r.State)}] {r.Prompt}").ToArray();
            _tasks.SelectedIndex = selected.HasValue ? _records.FindIndex(r => r.OperationId == selected) : _records.Count > 0 ? 0 : -1;
            RenderTask();
        });
    }
    private async Task RefreshSelectedAsync()
    {
        TaskRecord record = SelectedTask() ?? throw new InvalidOperationException("请先选择任务。");
        TaskRecord refreshed = await _client.RefreshAsync(record.OperationId);
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
            result = await _taskPlacement.PlaceAsync(task.OperationId, target,
                () => !_disposed && _shown && epoch == _placementEpoch);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(TaskPlacementPolicy.ErrorMessage(ex), ex);
        }
        Ui(() => { if (!_disposed) _status.Text = result.Message; global::Rhino.RhinoApp.WriteLine("Lux3D：" + result.Message); });
        // History refresh is independent of the completed document mutation.
        try { await ReloadAsync(task.OperationId); }
        catch { Ui(() => { if (!_disposed) _status.Text = result.Message + " 任务列表暂未刷新，可稍后重新查询。"; }); }
    }
    private async Task RefreshAutoTaskAsync(Guid operationId)
    {
        try
        {
            TaskRecord task = await _client.GetTaskAsync(operationId);
            if (!TaskPlacementPolicy.HasCachedAsset(task) && TaskPlacementPolicy.CanRefresh(task))
                task = await _client.RefreshAsync(operationId);
            Guid? selected = null;
            Ui(() => selected = SelectedTask()?.OperationId);
            await ReloadAsync(selected);
            await TryAutoPlaceAsync(task);
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                _autoPlacements.Remove(operationId);
                string message = TaskPlacementPolicy.ErrorMessage(ex) + " 请从任务页点击“放入当前文档”重试原任务。";
                if (!_disposed) _status.Text = message;
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
                if (!_disposed) _status.Text = TaskPlacementPolicy.NotReadyMessage(task) + " 可从任务页手动放入原任务。";
            });
            return;
        }
        if (task.State != "Ready")
        {
            Ui(() => { if (!_disposed) _status.Text = "任务 " + task.OperationId.ToString("N")[..8] + "：" + StateText(task.State) + "，完成后会放入原文档。"; });
            return;
        }
        try
        {
            TaskPlacementResult result = await _taskPlacement.PlaceReadyAsync(task, target,
                () => !_disposed && _shown && !_paused && epoch == _placementEpoch && _autoPlacements.ContainsKey(task.OperationId));
            Ui(() =>
            {
                if (result.State != "Deferred") _autoPlacements.Remove(task.OperationId);
                if (!_disposed) _status.Text = result.Message;
                if (result.State != "Deferred") global::Rhino.RhinoApp.WriteLine("Lux3D：" + result.Message);
            });
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                _autoPlacements.Remove(task.OperationId);
                string message = TaskPlacementPolicy.ErrorMessage(ex) + " 请从任务页点击“放入当前文档”。";
                if (!_disposed) _status.Text = message;
                global::Rhino.RhinoApp.WriteLine("Lux3D：" + message);
            });
        }
    }
    private void RenderTask()
    {
        TaskRecord? r = SelectedTask();
        _details.Text = r is null ? "尚无任务。" : $"{StateText(r.State)} · {r.Region} · {r.Model} · {r.FaceCount} 面\n{r.Prompt}\n操作 {r.OperationId}\n{r.Error}";
    }
    private static string StateText(string state) => state switch
    {
        "Ready" => "可放入 / 预览", "Rejected" => "模型已生成，资产校验未通过", "Missing" => "本地模型缓存缺失", "Queued" => "排队中", "Running" => "生成中", "Succeeded" => "生成成功，待下载",
        "Failed" => "失败", "DownloadFailed" => "下载失败，可重试查询", "StatusUnknown" => "状态待核对",
        "SubmissionUnknown" => "提交结果未知", "ProviderCancelled" => "服务端已取消", _ => state,
    };
    private void AddSelectedCandidate()
    {
        TaskRecord r = SelectedTask() ?? throw new InvalidOperationException("请先选择任务。");
        if (r.State != "Ready" || r.AssetPath is null) throw new InvalidOperationException("只有已校验并缓存的 Ready 任务可以加入候选。");
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
        if (_candidates.Count >= 3) throw new InvalidOperationException("每组最多 3 个候选；请清空后新建候选组。");
        PreparedAsset asset = _host.Prepare(path, sha, TargetDocument());
        _candidates.Add((name, asset, Guid.NewGuid()));
        _candidate.DataStore = _candidates.Select((c, i) => $"{(char)('A' + i)} · {c.Name}").ToArray();
        _candidate.SelectedIndex = _candidates.Count - 1;
        ShowPreview();
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
        UpdatePlacement();
    }
    private void UpdatePlacement()
    {
        _placement.Text = string.Format(CultureInfo.InvariantCulture, "点 ({0:G5}, {1:G5}, {2:G5}) / 当前选定平面；{3}", _point.X, _point.Y, _point.Z, TargetDocument().ModelUnitSystem);
        ShowPreview();
    }
    private void ShowPreview()
    {
        if (_disposed || _candidate.SelectedIndex < 0 || _candidate.SelectedIndex >= _candidates.Count) return;
        try { _preview.Show(_candidates[_candidate.SelectedIndex].Asset, Placement()); }
        catch (Exception ex) { _preview.Clear(); _status.Text = ex.Message; }
    }
    private void AcceptCandidate()
    {
        if (_candidate.SelectedIndex < 0) throw new InvalidOperationException("请先加载并选择候选。");
        ImportReceipt receipt = _host.Commit(_candidates[_candidate.SelectedIndex].Asset, Placement(), _candidates[_candidate.SelectedIndex].ImportId);
        string cleanup = string.Empty;
        try { ClearCandidates(); }
        catch { cleanup = " 预览清理未完成，可点击“退出比较 / 清空候选”；文档导入结果不受影响。"; }
        _status.Text = receipt.State == "Imported"
            ? $"已创建独立 Block；一次 Undo 可撤销。对象 {receipt.ObjectId}" + cleanup
            : "此前接受的候选已被撤销或删除，没有重复插入；可用 Redo 恢复或明确新建候选。" + cleanup;
    }
    private void ClearCandidates()
    {
        _preview.Clear();
        foreach (var item in _candidates) item.Asset.Dispose();
        _candidates.Clear(); _candidate.DataStore = Array.Empty<string>();
    }
    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
    {
        _shown = true; _timer.Start(); ShowPreview();
        _ = RunAsync(async () => { SetConnection(await _client.GetConnectionAsync(Region())); await ReloadAsync(null); });
    }
    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { _shown = false; _timer.Stop(); _preview.Clear(); }
    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument) { _shown = false; _placementEpoch++; _autoPlacements.Clear(); _timer.Stop(); ClearCandidates(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; _placementEpoch++; _autoPlacements.Clear(); _timer.Stop(); _timer.Dispose(); ClearCandidates(); _preview.Dispose(); }
        base.Dispose(disposing);
    }
}
