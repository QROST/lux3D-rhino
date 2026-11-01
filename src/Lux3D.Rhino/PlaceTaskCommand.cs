using System.Runtime.InteropServices;
using Rhino.Commands;
using Rhino.Input.Custom;
using RhinoDoc = global::Rhino.RhinoDoc;

namespace Lux3D.Rhino;

[Guid("54D7A5EE-7960-42AE-A88A-53633B6BFF91")]
public sealed class PlaceTaskCommand : Command
{
    public override string EnglishName => "Lux3DPlaceTask";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        using var input = new GetString();
        input.SetCommandPrompt("Lux3D 任务操作编号（只读取/恢复原任务，不重新生成）");
        if (input.Get() != global::Rhino.Input.GetResult.String) return Result.Cancel;
        if (!Guid.TryParse(input.StringResult(), out Guid operationId) || operationId == Guid.Empty)
        {
            global::Rhino.RhinoApp.WriteLine("操作编号无效；请从 Lux3D 任务详情复制完整 GUID。");
            return Result.Failure;
        }
        TaskPlacementService service = Lux3DPlugin.Instance.Placement;
        TaskPlacementTarget target;
        try { target = service.Capture(doc, TaskPlacementService.DefaultPlacement(doc)); }
        catch (Exception ex) { global::Rhino.RhinoApp.WriteLine(TaskPlacementPolicy.ErrorMessage(ex)); return Result.Failure; }
        // Rhino owns the command's undo record. Start only after it finishes, so Commit can own exactly one record.
        EventHandler? start = null;
        start = (_, _) =>
        {
            global::Rhino.RhinoApp.Idle -= start;
            _ = CompleteAsync(service, operationId, target);
        };
        global::Rhino.RhinoApp.Idle += start;
        global::Rhino.RhinoApp.WriteLine("正在读取原任务并准备放入；请保持当前文档打开。完成结果会显示在命令行。");
        return Result.Success;
    }
    private static async Task CompleteAsync(TaskPlacementService service, Guid operationId, TaskPlacementTarget target)
    {
        string message;
        try { message = (await service.PlaceAsync(operationId, target).ConfigureAwait(false)).Message; }
        catch (Exception ex) { message = TaskPlacementPolicy.ErrorMessage(ex); }
        await TaskPlacementService.OnUiAsync(() => { global::Rhino.RhinoApp.WriteLine("Lux3D：" + message); return true; }).ConfigureAwait(false);
    }
}
