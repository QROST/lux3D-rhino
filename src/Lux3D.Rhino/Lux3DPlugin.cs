using System.Reflection;
using System.Runtime.InteropServices;
using Lux3D.Core;
using Rhino.Commands;
using Rhino.PlugIns;

namespace Lux3D.Rhino;

[Guid("B8B147EC-5732-4FB9-BA40-C5BB8D36C2AB")]
public sealed class Lux3DPlugin : PlugIn
{
    internal static string DisplayVersion => typeof(Lux3DPlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(Lux3DPlugin).Assembly.GetName().Version?.ToString() ?? "unknown";
    internal static Lux3DPlugin Instance { get; private set; } = null!;
    internal Lux3DHostService Host { get; } = new();
    internal Lux3DClient Client { get; private set; } = null!;
    internal TaskPlacementService Placement { get; private set; } = null!;
    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        int? languageId = null;
        try { languageId = global::Rhino.UI.Localization.CurrentLanguageId; }
        catch (Exception) { /* If the host cannot report a language, use the system UI language. */ }
        LocalText.Configure(languageId, System.Globalization.CultureInfo.CurrentUICulture.Name);
        Instance = this;
        string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        Client = new Lux3DClient(Path.Combine(directory, "sidecar"));
        Placement = new TaskPlacementService(Host, Client);
        global::Rhino.UI.Panels.RegisterPanel(this, typeof(Lux3DPanel), "Lux3D", null!, global::Rhino.UI.PanelType.PerDoc);
        global::Rhino.RhinoDoc.CloseDocument += OnCloseDocument;
        global::Rhino.RhinoApp.WriteLine("Lux3D " + DisplayVersion + global::Lux3D.Core.LocalText.Get(" loaded. Enter Lux3DPanel to open the panel."));
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
