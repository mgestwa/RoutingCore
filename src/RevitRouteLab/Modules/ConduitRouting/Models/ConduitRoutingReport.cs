using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace INP_IE.ConduitRouting.Models
{
    public class ConduitRoutingReport
    {
        public int SelectedCableTrays { get; set; }

        public int SelectedFittings { get; set; }

        public int PlannedSegments { get; set; }

        public double PlannedLengthMm { get; set; }

        public string Relation { get; set; }

        public string RelationId { get; set; }

        public string RelationSource { get; set; }

        public double RelationLengthMm { get; set; }

        public double FnLengthMm { get; set; }

        public double PeLengthMm { get; set; }

        public int InvalidPlannedSegments { get; set; }

        public int CreatedConduits { get; set; }

        public int CreatedConnections { get; set; }

        public int FailedConnections { get; set; }

        public int SkippedExistingConduits { get; set; }

        public bool ExecutionCanceled { get; set; }

        public bool TransactionRolledBack { get; set; }

        /// <summary>Informacyjny wynik sprawdzenia Service Type na wybranej ścieżce.</summary>
        public string TrayServiceTypeSummary { get; set; } = string.Empty;

        /// <summary>True dla stałej wartości, false dla mieszanej, null przy braku danych.</summary>
        public bool? IsTrayServiceTypeConsistent { get; set; }

        public List<ElementId> CreatedElementIds { get; } = new List<ElementId>();

        public List<string> Warnings { get; } = new List<string>();

        /// <summary>
        /// Opis każdej przerwy w trasie kablowej, przez którą przeszła wybrana
        /// trasa. Pozwala zobaczyć w podglądzie i raporcie, że conduit został
        /// poprowadzony poza korytkiem.
        /// </summary>
        public List<string> BridgedGaps { get; } = new List<string>();

        public int BridgedGapCount => BridgedGaps.Count;

        /// <summary>
        /// Łączna długość odcinków poprowadzonych poza korytkiem jako zejście do
        /// urządzenia. Conduit biegnie tam w powietrzu, więc musi być widoczny
        /// osobno od długości prowadzonej po trasach kablowych.
        /// </summary>
        public double FreeAirLengthMm { get; set; }

        /// <summary>Opis każdego zejścia poza korytkiem użytego przez trasę.</summary>
        public List<string> FreeAirLegs { get; } = new List<string>();

        public int FreeAirLegCount => FreeAirLegs.Count;
    }
}