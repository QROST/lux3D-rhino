using System.Reflection;
using System.Runtime.InteropServices;
using Lux3D.Core;
using Rhino.Commands;
using Rhino.PlugIns;

namespace Lux3D.Rhino;

[Guid("B8B147EC-5732-4FB9-BA40-C5BB8D36C2AB")]
public sealed class Lux3DPlugin : PlugIn
{
    internal static Lux3DPlugin Instance { get; private set; } = null!;
    internal Lux3DHostService Host { get; } = new();
    internal Lux3DClient Client { get; private set; } = null!;
    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        Instance = this;
        string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        Client = new Lux3DClient(Path.Combine(directory, "sidecar"));
        global::Rhino.UI.Panels.RegisterPanel(this, typeof(Lux3DPanel), "Lux3D", null!, global::Rhino.UI.PanelType.PerDoc);
        global::Rhino.RhinoDoc.CloseDocument += OnCloseDocument;
        global::Rhino.RhinoApp.WriteLine("Lux3D 已加载。输入 Lux3DPanel 打开面板。");
        return LoadReturnCode.Success;
    }
    private void OnCloseDocument(object? sender, global::Rhino.DocumentEventArgs e) => Host.Forget(e.DocumentSerialNumber);
    protected override void OnShutdown()
    {
        global::Rhino.RhinoDoc.CloseDocument -= OnCloseDocument;
        Host.Dispose();
        Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnShutdown();
    }
}

[Guid("171AC949-C560-42B9-AB2E-EBCE9B801514")]
public sealed class OpenLux3DPanelCommand : Command
{
    public override string EnglishName => "Lux3DPanel";
    protected override Result RunCommand(global::Rhino.RhinoDoc doc, RunMode mode)
    {
        global::Rhino.UI.Panels.OpenPanel(typeof(Lux3DPanel));
        return Result.Success;
    }
}
