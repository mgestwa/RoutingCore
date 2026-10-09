using Autodesk.Revit.DB;

namespace INP_IE.ConduitRouting.Models
{
    public enum ConduitLayoutMode
    {
        Single,
        FixedCount,
        AutoFill
    }

    public class ConduitRoutingSettings
    {
        public ElementId ConduitTypeId { get; set; } = ElementId.InvalidElementId;

        public ConduitLayoutMode LayoutMode { get; set; } = ConduitLayoutMode.Single;

        public int FixedCount { get; set; } = 1;

        public double DiameterMm { get; set; } = 25;

        public double SpacingMm { get; set; } = 0;

        public double MarginMm { get; set; } = 0;

        public double ConnectionToleranceMm { get; set; } = 180;

        /// <summary>
        /// Pozwala przejść przez przerwę w trasie kablowej (np. brak kolanka).
        /// Dopuszczalne mostki uczestniczą w tym samym rankingu co fizyczne
        /// połączenia. Kara kosztowa preferuje realną trasę, ale nie zmusza
        /// conduitu do wielokrotnie dłuższego objazdu wokół lokalnej przerwy.
        /// </summary>
        public bool AllowTrayGapBridging { get; set; }

        /// <summary>Maksymalna długość łamanej mostka nad przerwą.</summary>
        public double MaxGapMm { get; set; } = 1500;

        /// <summary>
        /// Maksymalne boczne rozminięcie osi dwóch korytek, przy którym mostek
        /// nadal zostanie poprowadzony (ścieżką Z).
        /// </summary>
        public double MaxGapLateralOffsetMm { get; set; } = 300;

        /// <summary>
        /// Mnożnik kosztu mostka w grafie. Powyżej 1 preferuje realne
        /// połączenie kształtką, ale nadal pozwala wybrać krótki mostek zamiast
        /// nieproporcjonalnie długiego objazdu.
        /// </summary>
        public double GapCostPenaltyFactor { get; set; } = 3.0;

        /// <summary>Stały narzut kosztu doliczany do każdego mostka.</summary>
        public double GapFixedPenaltyMm { get; set; } = 3000;

        /// <summary>Limit mostków na jednej trasie, chroni przed łańcuchem przeskoków.</summary>
        public int MaxBridgesPerRoute { get; set; } = 4;

        /// <summary>
        /// Pozwala doprowadzić conduit do urządzenia, przy którym nie ma trasy
        /// kablowej — od korytka odchodzi zejście prowadzone w powietrzu.
        /// Uruchamia się dopiero wtedy, gdy relacji nie da się obsłużyć
        /// korytkami z normalnego promienia wyszukiwania, i tylko dla tego końca
        /// trasy, który musiał podpiąć się do korytka spoza tego promienia.
        /// Relacje wyznaczalne w całości po korytkach zachowują się dokładnie
        /// tak jak dotychczas — również limit objazdu ich nie dotyczy.
        /// </summary>
        public bool AllowFreeAirLegs { get; set; }

        /// <summary>Maksymalna długość jednego zejścia poza korytkiem.</summary>
        public double MaxFreeAirLengthMm { get; set; } = 6000;

        /// <summary>
        /// Promień szukania korytka, do którego podpina się zejście, gdy przy
        /// urządzeniu nie ma korytka w normalnym zasięgu.
        /// </summary>
        public double FreeAirAttachRadiusMm { get; set; } = 15000;

        /// <summary>
        /// Minimalna długość ostatniego odcinka zejścia, na którym kasowane jest
        /// przesunięcie toru. Dzięki niemu conduit dochodzi do gniazda w jego
        /// osi, a nie obok niej, bez dodatkowego krótkiego odcinka i fittingu.
        /// </summary>
        public double FreeAirTaperLengthMm { get; set; } = 300;

        /// <summary>
        /// Ile razy droga w powietrzu jest droższa od tej samej długości po
        /// korytku przy wyborze punktu odejścia od trasy kablowej. Powyżej 1
        /// sprawia, że conduit trzyma się korytka najdłużej jak się da i odchodzi
        /// dopiero naprzeciw urządzenia, zamiast lecieć do niego po skosie.
        /// </summary>
        public double FreeAirCostPenaltyFactor { get; set; } = 6.0;

        /// <summary>
        /// Ile razy trasa po korytkach może być dłuższa od odległości między
        /// panelem a odbiorem w linii prostej. Chroni przed podpięciem odbioru
        /// przez sieć prowadzącą naokoło całego obiektu. Zero wyłącza kontrolę.
        /// </summary>
        public double MaxRouteDetourFactor { get; set; } = 4.0;

        /// <summary>
        /// Ile korytek przy panelu jest ocenianych pełnym przebiegiem
        /// pathfindera przy wyborze końców trasy. Każde kolejne to jeden
        /// dodatkowy przebieg, więc wartość jest świadomie niska.
        /// </summary>
        public int MaxRankedStartCandidates { get; set; } = 3;
    }
}