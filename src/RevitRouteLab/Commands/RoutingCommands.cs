using System.IO;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitRouteLab.AutoTrayRouting;
using RevitRouteLab.AutoTrayRouting.Config;
using RevitRouteLab.ConduitManager.Models;
using RevitRouteLab.ConduitManager.Services;
using RevitRouteLab.ConduitRouting.Models;
using RevitRouteLab.ConduitRouting.Services;
using RevitRouteLab.ConduitRouting.Views;

namespace RevitRouteLab.Commands;

public abstract class RoutingCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var uiDoc = data.Application.ActiveUIDocument;
        if (uiDoc == null || uiDoc.Document.IsFamilyDocument || uiDoc.Document.IsReadOnly)
        {
            TaskDialog.Show("Route Lab", "Otwórz edytowalny projekt Revit.");
            return Result.Cancelled;
        }
        try { return Run(uiDoc); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    protected abstract Result Run(UIDocument uiDoc);

    protected static ConduitRoutingSettings? Settings(UIDocument uiDoc, IList<Element> trays)
    {
        var window = new ConduitRoutingSettingsWindow(uiDoc.Document, trays);
        new WindowInteropHelper(window) { Owner = uiDoc.Application.MainWindowHandle };
        return window.ShowDialog() == true ? window.Settings : null;
    }

    protected static Result ReviewAndExecute(UIDocument uiDoc, ConduitRoutingService service, ConduitExecutionPlan plan)
    {
        var report = plan.Report;
        var summary = $"Odcinki: {report.PlannedSegments}\nDługość: {report.PlannedLengthMm / 1000:F2} m\n" +
                      $"Przejścia przez przerwy: {report.BridgedGapCount}\nZejścia poza korytka: {report.FreeAirLengthMm / 1000:F2} m";
        if (!string.IsNullOrWhiteSpace(report.TrayServiceTypeSummary))
            summary += "\nService Type: " + report.TrayServiceTypeSummary;
        var details = string.Join("\n", report.Warnings.Concat(report.BridgedGaps).Concat(report.FreeAirLegs));
        if (!plan.CanExecute)
        {
            new TaskDialog("Route Lab") { MainInstruction = "Nie udało się przygotować trasy", MainContent = summary,
                ExpandedContent = details, CommonButtons = TaskDialogCommonButtons.Close }.Show();
            return Result.Cancelled;
        }

        var dialog = new TaskDialog("Route Lab")
        {
            MainInstruction = "Plan trasy jest gotowy",
            MainContent = summary,
            ExpandedContent = details,
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.CommandLink2
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Utwórz conduit według planu");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Pokaż korytka trasy bez tworzenia elementów");
        var result = dialog.Show();
        if (result == TaskDialogResult.CommandLink2)
        {
            var ids = plan.RoutePlan.Segments.Select(s => s.SourceElementId)
                .Where(id => id != null && id != ElementId.InvalidElementId && uiDoc.Document.GetElement(id) != null)
                .Distinct().ToList();
            if (ids.Count > 0)
            {
                uiDoc.Selection.SetElementIds(ids);
                uiDoc.ShowElements(ids);
            }
            return Result.Succeeded;
        }
        if (result != TaskDialogResult.CommandLink1) return Result.Cancelled;

        // Routing commands execute geometry plans without writing relation metadata.
        report = service.ExecutePreparedPlan(plan);
        new TaskDialog("Route Lab")
        {
            MainInstruction = report.TransactionRolledBack ? "Operacja została wycofana" : "Zakończono tworzenie trasy",
            MainContent = $"Conduity: {report.CreatedConduits}\nPołączenia: {report.CreatedConnections}\nNieudane połączenia: {report.FailedConnections}",
            ExpandedContent = string.Join("\n", report.Warnings),
            CommonButtons = TaskDialogCommonButtons.Close
        }.Show();
        return report.TransactionRolledBack ? Result.Cancelled : Result.Succeeded;
    }

    protected static Result RoutePoints(UIDocument uiDoc, bool viaTray)
    {
        var filter = new StraightCableTraySelectionFilter();
        var start = uiDoc.Selection.PickObject(ObjectType.PointOnElement, filter, "Wskaż punkt początkowy na prostym korytku");
        var end = uiDoc.Selection.PickObject(ObjectType.PointOnElement, filter, "Wskaż punkt końcowy na prostym korytku");
        var doc = uiDoc.Document;
        var startElement = doc.GetElement(start.ElementId);
        var endElement = doc.GetElement(end.ElementId);
        var trays = new List<Element> { startElement, endElement };
        Element? required = null;
        if (viaTray)
        {
            required = doc.GetElement(uiDoc.Selection.PickObject(ObjectType.Element, filter, "Wskaż korytko, przez które ma przejść trasa").ElementId);
            trays.Add(required);
        }
        var settings = Settings(uiDoc, trays.Distinct().ToList());
        if (settings == null) return Result.Cancelled;
        var service = new ConduitRoutingService(doc);
        var plan = required == null
            ? service.PrepareCollisionAwareRouteBetweenTrayPoints(startElement, start.GlobalPoint, endElement, end.GlobalPoint, settings)
            : service.PrepareCollisionAwareRouteBetweenTrayPointsViaTray(startElement, start.GlobalPoint, endElement, end.GlobalPoint, required, settings);
        return ReviewAndExecute(uiDoc, service, plan);
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class RoutePointsCommand : RoutingCommand
{
    protected override Result Run(UIDocument uiDoc) => RoutePoints(uiDoc, false);
}

[Transaction(TransactionMode.Manual)]
public sealed class RouteViaTrayCommand : RoutingCommand
{
    protected override Result Run(UIDocument uiDoc) => RoutePoints(uiDoc, true);
}

[Transaction(TransactionMode.Manual)]
public sealed class FillTraysCommand : RoutingCommand
{
    protected override Result Run(UIDocument uiDoc)
    {
        var doc = uiDoc.Document;
        var trays = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(TrayElementClassifier.IsTrayElement).ToList();
        if (trays.Count == 0)
            trays = uiDoc.Selection.PickObjects(ObjectType.Element, new TrayElementSelectionFilter(), "Wskaż korytka i kształtki")
                .Select(r => doc.GetElement(r.ElementId)).ToList();
        if (trays.Count == 0) return Result.Cancelled;
        var settings = Settings(uiDoc, trays);
        if (settings == null) return Result.Cancelled;
        var service = new ConduitRoutingService(doc);
        return ReviewAndExecute(uiDoc, service, service.PrepareFillSelectedTrayNetwork(trays, settings));
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class RouteDevicesCommand : RoutingCommand
{
    protected override Result Run(UIDocument uiDoc)
    {
        var doc = uiDoc.Document;
        var source = doc.GetElement(uiDoc.Selection.PickObject(ObjectType.Element, "Wskaż urządzenie początkowe").ElementId);
        var target = doc.GetElement(uiDoc.Selection.PickObject(ObjectType.Element, "Wskaż urządzenie końcowe").ElementId);
        if (source.Id == target.Id)
        {
            TaskDialog.Show("Route Lab", "Wskaż dwa różne urządzenia.");
            return Result.Cancelled;
        }
        var locator = new ConduitAutomaticEndpointLocatorService(doc);
        var settings = Settings(uiDoc, locator.FindNearbyTrays(source, target).Cast<Element>().ToList());
        if (settings == null) return Result.Cancelled;
        if (!locator.TryLocate(source, target, settings, out var endpoints, out var error))
        {
            TaskDialog.Show("Route Lab", error);
            return Result.Cancelled;
        }
        var service = new ConduitRoutingService(doc);
        return ReviewAndExecute(uiDoc, service, service.PrepareCollisionAwareRoute(endpoints, settings));
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class AutoTrayCommand : RoutingCommand
{
    protected override Result Run(UIDocument uiDoc)
    {
        var ids = uiDoc.Selection.GetElementIds().ToList();
        if (ids.Count != 2)
        {
            TaskDialog.Show("Route Lab", "Zaznacz dokładnie dwa elementy źródłowe przed uruchomieniem Auto korytka.");
            return Result.Cancelled;
        }
        var dialog = new TaskDialog("Route Lab — Auto korytka")
        {
            MainInstruction = "Wybierz elementy do utworzenia",
            MainContent = "Uruchomienie utworzy geometrię w bieżącym modelu.",
            CommonButtons = TaskDialogCommonButtons.Cancel
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Korytka");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Conduity 25 mm");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Korytka i conduity 25 mm");
        var result = dialog.Show();
        if (result != TaskDialogResult.CommandLink1 && result != TaskDialogResult.CommandLink2 && result != TaskDialogResult.CommandLink3)
            return Result.Cancelled;
        var options = new RoutingOptions
        {
            CreateCableTray = result != TaskDialogResult.CommandLink2,
            CreateConduit = result != TaskDialogResult.CommandLink1,
            ConduitConfig = new ConduitConfig { DiameterMM = 25 }
        };
        var folder = Path.GetDirectoryName(typeof(AutoTrayCommand).Assembly.Location);
        new AutoTrayRouter().Run(uiDoc, ids[0], ids[1], AutoTrayRoutingSettings.Load(folder), options);
        return Result.Succeeded;
    }
}
