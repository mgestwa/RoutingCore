using Autodesk.Revit.UI;

namespace RevitRouteLab;

public sealed class Application : IExternalApplication
{
    public Result OnStartup(UIControlledApplication app)
    {
        const string tab = "Route Lab";
        try { app.CreateRibbonTab(tab); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* Existing tab. */ }

        var panel = app.CreateRibbonPanel(tab, "Trasy");
        Add<Commands.RoutePointsCommand>(panel, "Między\npunktami", "Wyznacz trasę conduitu po sieci korytek, obejrzyj plan i opcjonalnie utwórz elementy.");
        Add<Commands.RouteViaTrayCommand>(panel, "Przez\nkorytko", "Wyznacz najkrótszą trasę przechodzącą przez wybrane proste korytko.");
        Add<Commands.RouteDevicesCommand>(panel, "Między\nurządzeniami", "Dobierz końce trasy do dwóch wskazanych urządzeń i wyznacz conduit po korytkach.");
        Add<Commands.FillTraysCommand>(panel, "Conduit\nw korytkach", "Utwórz conduit w zaznaczonych korytkach i kształtkach.");
        Add<Commands.AutoTrayCommand>(panel, "Auto\nkorytka", "Uruchom AutoTrayRouter dla dwóch wybranych elementów.");
        return Result.Succeeded;
    }

    private static void Add<T>(RibbonPanel panel, string label, string tooltip)
    {
        panel.AddItem(new PushButtonData(typeof(T).Name, label,
            typeof(Application).Assembly.Location, typeof(T).FullName) { ToolTip = tooltip });
    }

    public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
}
