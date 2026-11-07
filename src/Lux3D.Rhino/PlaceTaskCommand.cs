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
        input.SetCommandPrompt(global::Lux3D.Core.LocalText.Get("Lux3D task operation ID (read/recover the original task; never regenerate)"));
        if (input.Get() != global::Rhino.Input.GetResult.String) return Result.Cancel;
        if (!Guid.TryParse(input.StringResult(), out Guid operationId) || operationId == Guid.Empty)
        {
            global::Rhino.RhinoApp.WriteLine(global::Lux3D.Core.LocalText.Get("Invalid operation ID. Copy the full GUID from Lux3D task details."));
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
        global::Rhino.RhinoApp.WriteLine(global::Lux3D.Core.LocalText.Get("Reading the original task and preparing placement. Keep this document open; the result will appear in the command line."));
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
