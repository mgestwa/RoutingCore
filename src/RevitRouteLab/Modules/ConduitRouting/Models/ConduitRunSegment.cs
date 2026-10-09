using System;
using Autodesk.Revit.DB;

namespace RevitRouteLab.ConduitRouting.Models
{
    /// <summary>
    /// Rodzaj odcinka trasy. Odcinki prowadzone po korytku i kształtce mają
    /// element źródłowy w modelu, mostek nad przerwą i zejście do urządzenia
    /// biegną w powietrzu i pożyczają przekrój od sąsiedniego korytka.
    /// </summary>
    public enum ConduitSegmentKind
    {
        TrayRun,
        FittingRun,
        Bridge,
        FreeAir
    }

    public class ConduitRunSegment
    {
        public ConduitRunSegment(XYZ start, XYZ end, ElementId sourceElementId, int layoutIndex, ElementId levelId)
            : this(start, end, sourceElementId, layoutIndex, levelId, ConduitSegmentKind.TrayRun, sourceElementId)
        {
        }

        public ConduitRunSegment(
            XYZ start,
            XYZ end,
            ElementId sourceElementId,
            int layoutIndex,
            ElementId levelId,
            ConduitSegmentKind kind,
            ElementId widthReferenceElementId)
            : this(start, end, sourceElementId, layoutIndex, levelId, kind, widthReferenceElementId, 1.0, 1.0)
        {
        }

        public ConduitRunSegment(
            XYZ start,
            XYZ end,
            ElementId sourceElementId,
            int layoutIndex,
            ElementId levelId,
            ConduitSegmentKind kind,
            ElementId widthReferenceElementId,
            double startLaneFactor,
            double endLaneFactor)
        {
            Start = start;
            End = end;
            SourceElementId = sourceElementId;
            LayoutIndex = layoutIndex;
            LevelId = levelId;
            Kind = kind;
            WidthReferenceElementId = widthReferenceElementId ?? sourceElementId;
            StartLaneFactor = startLaneFactor;
            EndLaneFactor = endLaneFactor;
        }

        public XYZ Start { get; }

        public XYZ End { get; }

        public ElementId SourceElementId { get; }

        public int LayoutIndex { get; }

        public ElementId LevelId { get; }

        public ConduitSegmentKind Kind { get; }

        /// <summary>
        /// Element, z którego odczytywana jest szerokość przekroju przy
        /// przydzielaniu torów. Dla odcinków po korytku i kształtce jest to ten
        /// sam element co <see cref="SourceElementId"/>, dla mostka i zejścia —
        /// korytko, z którego odcinek wychodzi.
        /// </summary>
        public ElementId WidthReferenceElementId { get; }

        /// <summary>
        /// Udział przesunięcia toru na początku odcinka: 1.0 oznacza pełny
        /// offset toru, 0.0 — oś elementu. Zejście do urządzenia kończy się
        /// skosem z 1.0 na 0.0, żeby conduit doszedł do gniazda w jego osi,
        /// a nie obok niej.
        /// </summary>
        public double StartLaneFactor { get; }

        /// <summary>Udział przesunięcia toru na końcu odcinka.</summary>
        public double EndLaneFactor { get; }

        /// <summary>
        /// True, gdy odcinek zbiega z toru do osi elementu (albo odwrotnie).
        /// Wszystkie tory schodzą się wtedy w jednym punkcie, więc taki odcinek
        /// nie może rozstrzygać o zajętości toru — inaczej pierwszy conduit
        /// doprowadzony do urządzenia blokowałby przy nim każdy następny.
        /// </summary>
        public bool IsLaneConverging => Math.Abs(StartLaneFactor - EndLaneFactor) > 1e-9;

        /// <summary>
        /// Kierunek szerokości przekroju odczytany z elementu prowadzącego.
        /// Tory rozkładają się wzdłuż tej osi. Wyznaczanie jej z samego kierunku
        /// biegu zawodzi na korytkach pionowych, gdzie iloczyn wektorowy z osią Z
        /// jest zerowy i nie niesie żadnej informacji o obrocie korytka.
        /// Null oznacza, że kierunek trzeba wyprowadzić geometrycznie.
        /// </summary>
        public XYZ CrossSectionSide { get; set; }

        /// <summary>
        /// Direction from the tray support plane toward its open side. The
        /// allocator uses it to keep the conduit radius clear of tray rungs and
        /// transports it continuously through bends.
        /// </summary>
        public XYZ? CrossSectionUp { get; set; }

        /// <summary>Odtwarza odcinek w nowym położeniu, zachowując wszystkie pozostałe pola.</summary>
        public ConduitRunSegment WithGeometry(XYZ start, XYZ end)
        {
            return new ConduitRunSegment(
                start, end, SourceElementId, LayoutIndex, LevelId, Kind, WidthReferenceElementId,
                StartLaneFactor, EndLaneFactor)
            {
                CrossSectionSide = CrossSectionSide,
                CrossSectionUp = CrossSectionUp
            };
        }
    }
}
