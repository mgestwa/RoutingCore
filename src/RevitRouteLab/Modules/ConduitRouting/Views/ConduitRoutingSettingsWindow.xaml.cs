using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using INP_IE.ConduitManager.Services;
using INP_IE.ConduitRouting.Models;
using Canvas = System.Windows.Controls.Canvas;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using Ellipse = System.Windows.Shapes.Ellipse;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace INP_IE.ConduitRouting.Views
{
    public partial class ConduitRoutingSettingsWindow : Window
    {
        private const double FeetToMm = 304.8;

        private static readonly Color AccentColor = Color.FromRgb(0, 122, 204);
        private static readonly Color GhostColor = Color.FromRgb(105, 105, 110);
        private static readonly Color ErrorColor = Color.FromRgb(244, 135, 113);
        private static readonly Color WarningColor = Color.FromRgb(215, 186, 125);
        private static readonly Color OkColor = Color.FromRgb(78, 201, 176);

        private readonly Document _doc;
        private readonly int _relationCount;
        private readonly bool _initialized;

        public ConduitRoutingSettings Settings { get; private set; }

        public ConduitRoutingSettingsWindow(Document doc, IList<Element> selectedElements, int relationCount = 1)
        {
            InitializeComponent();
            _doc = doc;
            _relationCount = Math.Max(1, relationCount);
            LoadConduitTypes(doc);
            LoadTrayPreviewItems(selectedElements);
            ApplyOperationHeader();
            _initialized = true;
            UpdatePreview();
        }

        private void ApplyOperationHeader()
        {
            if (_relationCount > 1)
            {
                HeaderTextBlock.Text = "Trasowanie hurtowe conduitów";
                SubtitleTextBlock.Text =
                    $"Relacje w operacji: {_relationCount} — każda otrzyma własny tor w korytku.";
                CreateButton.Content = $"Utwórz trasy ({_relationCount})";
            }
            else
            {
                HeaderTextBlock.Text = "Trasowanie conduitu";
                SubtitleTextBlock.Text = "Jeden conduit jako reprezentacja relacji.";
                CreateButton.Content = "Utwórz trasę";
            }
        }

        private void LoadConduitTypes(Document doc)
        {
            var conduitTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(ConduitType))
                .Cast<ConduitType>()
                .Select(type => new ConduitTypeItem(type.Id, type.Name))
                .OrderBy(type => type.Name)
                .ToList();

            ConduitTypeComboBox.ItemsSource = conduitTypes;
            ConduitTypeComboBox.SelectedItem = conduitTypes.FirstOrDefault();
        }

        private void OnConduitTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadDiametersForSelectedType();
            UpdatePreview();
        }

        private void LoadDiametersForSelectedType()
        {
            if (DiameterComboBox == null)
            {
                return;
            }

            var previousMm = (DiameterComboBox.SelectedItem as DiameterItem)?.DiameterMm ?? 25.0;
            var diameters = GetAvailableDiameters();

            if (diameters.Count == 0)
            {
                diameters = new List<DiameterItem>
                {
                    new DiameterItem(16), new DiameterItem(21), new DiameterItem(27),
                    new DiameterItem(35), new DiameterItem(41), new DiameterItem(53),
                    new DiameterItem(63), new DiameterItem(78), new DiameterItem(91),
                    new DiameterItem(103), new DiameterItem(129), new DiameterItem(155)
                };
            }

            DiameterComboBox.ItemsSource = diameters;
            var match = diameters
                .OrderBy(d => Math.Abs(d.DiameterMm - previousMm))
                .FirstOrDefault();
            DiameterComboBox.SelectedItem = match ?? diameters.FirstOrDefault();
        }

        private List<DiameterItem> GetAvailableDiameters()
        {
            var result = new SortedSet<double>();
            if (_doc == null || !(ConduitTypeComboBox.SelectedItem is ConduitTypeItem typeItem))
            {
                return new List<DiameterItem>();
            }

            if (!(_doc.GetElement(typeItem.Id) is ConduitType conduitType))
            {
                return new List<DiameterItem>();
            }

            var manager = conduitType.RoutingPreferenceManager;
            if (manager != null)
            {
                var ruleCount = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments);
                for (var i = 0; i < ruleCount; i++)
                {
                    var rule = manager.GetRule(RoutingPreferenceRuleGroupType.Segments, i);
                    if (rule == null)
                    {
                        continue;
                    }

                    if (!(_doc.GetElement(rule.MEPPartId) is Segment segment))
                    {
                        continue;
                    }

                    foreach (MEPSize size in segment.GetSizes())
                    {
                        result.Add(Math.Round(size.NominalDiameter * FeetToMm, 1));
                    }
                }
            }

            if (result.Count == 0)
            {
                foreach (var segment in GetFallbackSegments(conduitType))
                {
                    foreach (MEPSize size in segment.GetSizes())
                    {
                        result.Add(Math.Round(size.NominalDiameter * FeetToMm, 1));
                    }
                }
            }

            return result.Select(mm => new DiameterItem(mm)).ToList();
        }

        private IEnumerable<Segment> GetFallbackSegments(ConduitType conduitType)
        {
            var segments = new FilteredElementCollector(_doc)
                .WhereElementIsElementType()
                .OfType<Segment>()
                .Concat(new FilteredElementCollector(_doc)
                    .WhereElementIsNotElementType()
                    .OfType<Segment>())
                .ToList();

            var matchingByName = segments
                .Where(segment => NamesMatch(segment.Name, conduitType.Name))
                .ToList();

            if (matchingByName.Count > 0)
            {
                return matchingByName;
            }

            return segments
                .Where(IsConduitSegment)
                .ToList();
        }

        private static bool NamesMatch(string segmentName, string conduitTypeName)
        {
            if (string.IsNullOrWhiteSpace(segmentName) || string.IsNullOrWhiteSpace(conduitTypeName))
            {
                return false;
            }

            return conduitTypeName.IndexOf(segmentName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   segmentName.IndexOf(conduitTypeName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsConduitSegment(Segment segment)
        {
            var categoryName = segment.Category?.Name ?? string.Empty;
            var segmentName = segment.Name ?? string.Empty;

            return categoryName.IndexOf("conduit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   segmentName.IndexOf("conduit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   segmentName.IndexOf("kable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   segmentName.IndexOf("cable", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void LoadTrayPreviewItems(IList<Element> selectedElements)
        {
            // Najwęższe korytko znalezione przy końcach relacji jest domyślnym przekrojem nominalnym.
            var items = (selectedElements ?? new List<Element>())
                .Select(BuildPreviewItem)
                .Where(item => item != null)
                .OrderBy(item => item.WidthMm)
                .ToList();

            if (items.Count == 0)
            {
                items.Add(new TrayPreviewItem("Domyślne korytko 200 × 100 mm", 200, 100));
            }
            else if (items.Count > 1)
            {
                items[0] = new TrayPreviewItem(
                    items[0].Description + "  (najwęższe w podglądzie)",
                    items[0].WidthMm,
                    items[0].HeightMm);
            }

            TrayPreviewComboBox.ItemsSource = items;
            TrayPreviewComboBox.SelectedItem = items.FirstOrDefault();
        }

        private static TrayPreviewItem BuildPreviewItem(Element element)
        {
            var widthParam = element.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            var heightParam = element.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            var widthMm = widthParam != null ? widthParam.AsDouble() * FeetToMm : 0;
            var heightMm = heightParam != null ? heightParam.AsDouble() * FeetToMm : 0;
            if (widthMm <= 0 || heightMm <= 0)
            {
                return null;
            }

            var description = $"{element.Category?.Name ?? "Tray"} {element.Id.IntegerValue} — {widthMm:F0} × {heightMm:F0} mm";
            return new TrayPreviewItem(description, widthMm, heightMm);
        }

        private void OnSettingsChanged(object sender, RoutedEventArgs e)
        {
            UpdatePreview();
        }

        private void OnBridgingToggled(object sender, RoutedEventArgs e)
        {
            if (GapSettingsGrid != null)
            {
                GapSettingsGrid.IsEnabled = AllowGapBridgingCheckBox.IsChecked == true;
            }

            UpdatePreview();
        }

        private void OnFreeAirToggled(object sender, RoutedEventArgs e)
        {
            if (FreeAirSettingsGrid != null)
            {
                FreeAirSettingsGrid.IsEnabled = AllowFreeAirLegsCheckBox.IsChecked == true;
            }

            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (!_initialized || PreviewCanvas == null)
            {
                return;
            }

            PreviewCanvas.Children.Clear();

            var validationError = ValidateInputs(out var inputs);
            UpdateValidationUi(validationError);
            var spacingMm = inputs.SpacingMm;
            var marginMm = inputs.MarginMm;

            var diameterMm = (DiameterComboBox.SelectedItem as DiameterItem)?.DiameterMm ?? 0;
            if (!(TrayPreviewComboBox.SelectedItem is TrayPreviewItem tray) || diameterMm <= 0)
            {
                CapacityTextBlock.Text = string.Empty;
                PreviewSummaryTextBlock.Text = string.Empty;
                return;
            }

            var canvasWidth = PreviewCanvas.Width;
            var canvasHeight = PreviewCanvas.Height;
            var padding = 16.0;
            var scale = Math.Min(
                (canvasWidth - 2 * padding) / tray.WidthMm,
                (canvasHeight - 2 * padding) / tray.HeightMm);
            var trayWidthPx = tray.WidthMm * scale;
            var trayHeightPx = tray.HeightMm * scale;
            var trayX = (canvasWidth - trayWidthPx) / 2.0;
            var trayY = (canvasHeight - trayHeightPx) / 2.0;

            DrawTray(trayX, trayY, trayWidthPx, trayHeightPx);
            if (marginMm > 0)
            {
                DrawMargin(
                    trayX + marginMm * scale,
                    trayY + marginMm * scale,
                    trayWidthPx - 2 * marginMm * scale,
                    trayHeightPx - 2 * marginMm * scale);
            }

            // Ta sama matematyka geometryczna co w alokatorze, ale bez zajętości
            // modelu i bez wiedzy o rzeczywistym przebiegu poszczególnych relacji.
            var pitchMm = ConduitLaneMath.GetPitchMm(diameterMm, spacingMm);
            var maxStep = ConduitLaneMath.GetMaximumLaneStep(tray.WidthMm, diameterMm, marginMm, pitchMm);
            var nominalLaneCount = maxStep < 0 ? 0 : 2 * maxStep + 1;
            var plannedLaneCount = Math.Min(_relationCount, nominalLaneCount);
            var heightFits = ConduitLaneMath.FitsInsideTrayHeight(
                tray.HeightMm, diameterMm, marginMm);

            var centerX = trayX + trayWidthPx / 2.0;
            var supportClearanceMm = ConduitLaneMath.GetCrossSectionClearanceMm(marginMm);
            var centerY = trayY + trayHeightPx -
                          (supportClearanceMm + diameterMm / 2.0) * scale;
            var drawn = 0;
            var maxUsedOffsetMm = 0.0;
            if (maxStep >= 0)
            {
                foreach (var lane in ConduitLaneMath.BuildCenterOutLaneSequence(maxStep))
                {
                    var offsetMm = lane * pitchMm;
                    var used = drawn < plannedLaneCount;
                    if (used)
                    {
                        maxUsedOffsetMm = Math.Max(maxUsedOffsetMm, Math.Abs(offsetMm));
                    }

                    DrawConduit(centerX + offsetMm * scale, centerY, diameterMm * scale, used);
                    drawn++;
                }
            }

            UpdateCapacityText(
                tray, diameterMm, marginMm, nominalLaneCount, plannedLaneCount,
                maxUsedOffsetMm, heightFits);
        }

        private void UpdateCapacityText(
            TrayPreviewItem tray,
            double diameterMm,
            double marginMm,
            int nominalLaneCount,
            int plannedLaneCount,
            double maxUsedOffsetMm,
            bool heightFits)
        {
            if (!heightFits)
            {
                var requiredHeightMm =
                    ConduitLaneMath.GetRequiredTrayHeightMm(diameterMm, marginMm);
                CapacityTextBlock.Foreground = new SolidColorBrush(ErrorColor);
                CapacityTextBlock.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    "Nominalna kontrola wysokości: korytko ma {0:0} mm, a conduit Ø{1:0.#} mm z wymaganym luzem potrzebuje {2:0.#} mm.",
                    tray.HeightMm, diameterMm, requiredHeightMm);
            }
            else if (nominalLaneCount == 0)
            {
                CapacityTextBlock.Foreground = new SolidColorBrush(ErrorColor);
                CapacityTextBlock.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    "Nominalna kontrola szerokości: conduit Ø{0:0.#} mm z marginesem {1:0.#} mm nie mieści się w korytku o szerokości {2:0} mm.",
                    diameterMm, marginMm, tray.WidthMm);
            }
            else if (_relationCount > nominalLaneCount)
            {
                CapacityTextBlock.Foreground = new SolidColorBrush(WarningColor);
                CapacityTextBlock.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    "Nominalnie zmieści się {0} z {1} relacji. Rzeczywisty wynik zależy od przebiegu tras i istniejących conduitów.",
                    nominalLaneCount, _relationCount);
            }
            else
            {
                var edgeUse = ConduitLaneMath.GetTrayFillRatio(
                    tray.WidthMm, diameterMm, marginMm, maxUsedOffsetMm);
                CapacityTextBlock.Foreground = new SolidColorBrush(OkColor);
                CapacityTextBlock.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    "Nominalnie: {0} planowanych / {1} geometrycznie dostępnych • wykorzystanie szerokości do skrajnego toru {2:P0}",
                    plannedLaneCount, nominalLaneCount, Math.Min(1.0, edgeUse));
            }

            PreviewSummaryTextBlock.Text =
                "Pełne koła — relacje tej operacji, przerywane — pozostałe tory nominalne. " +
                "Podgląd nie uwzględnia istniejących conduitów ani rzeczywistego przebiegu relacji; " +
                "pełna kontrola nastąpi w następnym kroku.";
        }

        private void DrawTray(double x, double y, double width, double height)
        {
            var rectangle = new Rectangle
            {
                Width = width,
                Height = height,
                Stroke = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromRgb(45, 45, 48))
            };

            Canvas.SetLeft(rectangle, x);
            Canvas.SetTop(rectangle, y);
            PreviewCanvas.Children.Add(rectangle);
        }

        private void DrawMargin(double x, double y, double width, double height)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var rectangle = new Rectangle
            {
                Width = width,
                Height = height,
                Stroke = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            };

            Canvas.SetLeft(rectangle, x);
            Canvas.SetTop(rectangle, y);
            PreviewCanvas.Children.Add(rectangle);
        }

        private void DrawConduit(double centerX, double centerY, double diameterPx, bool used)
        {
            var radius = Math.Max(2, diameterPx / 2.0);
            var ellipse = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                StrokeThickness = used ? 1.5 : 1
            };

            if (used)
            {
                ellipse.Fill = new SolidColorBrush(
                    Color.FromArgb(190, AccentColor.R, AccentColor.G, AccentColor.B));
                ellipse.Stroke = new SolidColorBrush(AccentColor);
            }
            else
            {
                ellipse.Fill = Brushes.Transparent;
                ellipse.Stroke = new SolidColorBrush(GhostColor);
                ellipse.StrokeDashArray = new DoubleCollection { 3, 2 };
            }

            Canvas.SetLeft(ellipse, centerX - radius);
            Canvas.SetTop(ellipse, centerY - radius);
            PreviewCanvas.Children.Add(ellipse);
        }

        private string ValidateInputs(out ParsedInputs inputs)
        {
            inputs = new ParsedInputs();

            if (!TryParseDouble(SpacingTextBox?.Text, out var spacingMm) || spacingMm < 0)
            {
                return "Odstęp między conduitami musi być liczbą ≥ 0.";
            }

            inputs.SpacingMm = spacingMm;

            if (!TryParseDouble(MarginTextBox?.Text, out var marginMm) || marginMm < 0)
            {
                return "Margines od krawędzi musi być liczbą ≥ 0.";
            }

            inputs.MarginMm = marginMm;

            if (!TryParseDouble(ConnectionToleranceTextBox?.Text, out var toleranceMm) || toleranceMm <= 0)
            {
                return "Tolerancja łączenia musi być liczbą > 0.";
            }

            inputs.ToleranceMm = toleranceMm;

            if (!TryParseDouble(MaxDetourFactorTextBox?.Text, out var detourFactor) || detourFactor < 0)
            {
                return "Maksymalny objazd musi być liczbą ≥ 0 (0 wyłącza kontrolę).";
            }

            if (detourFactor > 0 && detourFactor < 1)
            {
                return "Maksymalny objazd nie może być mniejszy niż 1× odległości w linii prostej.";
            }

            inputs.MaxDetourFactor = detourFactor;
            inputs.AllowBridging = AllowGapBridgingCheckBox?.IsChecked == true;
            inputs.AllowFreeAir = AllowFreeAirLegsCheckBox?.IsChecked == true;

            if (inputs.AllowBridging)
            {
                if (!TryParseDouble(MaxGapTextBox?.Text, out var maxGapMm) || maxGapMm <= 0)
                {
                    return "Maksymalna przerwa musi być liczbą > 0.";
                }

                if (!TryParseDouble(MaxGapOffsetTextBox?.Text, out var maxGapOffsetMm) || maxGapOffsetMm < 0)
                {
                    return "Maksymalne rozminięcie osi musi być liczbą ≥ 0.";
                }

                inputs.MaxGapMm = maxGapMm;
                inputs.MaxGapOffsetMm = maxGapOffsetMm;
            }

            if (inputs.AllowFreeAir)
            {
                if (!TryParseDouble(MaxFreeAirLengthTextBox?.Text, out var maxFreeAirMm) || maxFreeAirMm <= 0)
                {
                    return "Maksymalna długość zejścia musi być liczbą > 0.";
                }

                if (!TryParseDouble(FreeAirTaperTextBox?.Text, out var taperMm) || taperMm < 0)
                {
                    return "Długość skosu przy urządzeniu musi być liczbą ≥ 0.";
                }

                if (taperMm >= maxFreeAirMm)
                {
                    return "Skos przy urządzeniu musi być krótszy niż całe zejście.";
                }

                inputs.MaxFreeAirLengthMm = maxFreeAirMm;
                inputs.FreeAirTaperMm = taperMm;
            }

            return null;
        }

        private class ParsedInputs
        {
            public double SpacingMm { get; set; }

            public double MarginMm { get; set; }

            public double ToleranceMm { get; set; }

            public double MaxDetourFactor { get; set; }

            public bool AllowBridging { get; set; }

            public double MaxGapMm { get; set; }

            public double MaxGapOffsetMm { get; set; }

            public bool AllowFreeAir { get; set; }

            public double MaxFreeAirLengthMm { get; set; }

            public double FreeAirTaperMm { get; set; }
        }

        private void UpdateValidationUi(string error)
        {
            var hasError = !string.IsNullOrEmpty(error);
            if (ValidationTextBlock != null)
            {
                ValidationTextBlock.Text = error ?? string.Empty;
                ValidationTextBlock.Visibility = hasError
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            }

            if (CreateButton != null)
            {
                CreateButton.IsEnabled = !hasError;
            }
        }

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var error = ValidateInputs(out var inputs);
            if (error == null && !(ConduitTypeComboBox.SelectedItem is ConduitTypeItem))
            {
                error = "Wybierz typ conduita.";
            }

            if (error == null && !(DiameterComboBox.SelectedItem is DiameterItem))
            {
                error = "Wybierz średnicę conduita.";
            }

            if (error != null)
            {
                UpdateValidationUi(error);
                return;
            }

            var conduitType = (ConduitTypeItem)ConduitTypeComboBox.SelectedItem;
            var diameterItem = (DiameterItem)DiameterComboBox.SelectedItem;

            Settings = new ConduitRoutingSettings
            {
                ConduitTypeId = conduitType.Id,
                LayoutMode = ConduitLayoutMode.Single,
                FixedCount = 1,
                DiameterMm = diameterItem.DiameterMm,
                SpacingMm = inputs.SpacingMm,
                MarginMm = inputs.MarginMm,
                ConnectionToleranceMm = inputs.ToleranceMm,
                MaxRouteDetourFactor = inputs.MaxDetourFactor,
                AllowTrayGapBridging = inputs.AllowBridging,
                AllowFreeAirLegs = inputs.AllowFreeAir
            };

            if (inputs.AllowBridging)
            {
                Settings.MaxGapMm = inputs.MaxGapMm;
                Settings.MaxGapLateralOffsetMm = inputs.MaxGapOffsetMm;
            }

            if (inputs.AllowFreeAir)
            {
                Settings.MaxFreeAirLengthMm = inputs.MaxFreeAirLengthMm;
                Settings.FreeAirTaperLengthMm = inputs.FreeAirTaperMm;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private class ConduitTypeItem
        {
            public ConduitTypeItem(ElementId id, string name)
            {
                Id = id;
                Name = name;
            }

            public ElementId Id { get; }

            public string Name { get; }
        }

        private class DiameterItem
        {
            public DiameterItem(double diameterMm)
            {
                DiameterMm = diameterMm;
            }

            public double DiameterMm { get; }

            public string Display => $"{DiameterMm:0.#} mm";
        }

        private class TrayPreviewItem
        {
            public TrayPreviewItem(string description, double widthMm, double heightMm)
            {
                Description = description;
                WidthMm = widthMm;
                HeightMm = heightMm;
            }

            public string Description { get; }

            public double WidthMm { get; }

            public double HeightMm { get; }
        }
    }
}
