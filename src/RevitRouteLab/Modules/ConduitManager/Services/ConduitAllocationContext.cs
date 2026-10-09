using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace INP_IE.ConduitManager.Services
{
    /// <summary>
    /// Occupancy shared by all routes prepared in one operation. It contains
    /// both conduits already present in Revit and virtual reservations made by
    /// plans that have not been executed yet. Every accepted lane reserves its
    /// axes immediately so the next relation cannot receive the same axis.
    /// </summary>
    public sealed class ConduitAllocationContext
    {
        private readonly List<ConduitOccupiedAxis> _occupiedAxes =
            new List<ConduitOccupiedAxis>();

        internal IReadOnlyList<ConduitOccupiedAxis> OccupiedAxes => _occupiedAxes;

        /// <summary>Number of axes that come from conduits already in the model.</summary>
        public int ExistingAxisCount { get; private set; }

        /// <summary>Number of axes reserved by not-yet-executed plans.</summary>
        public int ReservedAxisCount => _occupiedAxes.Count - ExistingAxisCount;

        internal void AddExistingAxis(ConduitOccupiedAxis axis)
        {
            if (axis == null)
            {
                return;
            }

            _occupiedAxes.Add(axis);
            ExistingAxisCount++;
        }

        internal void Reserve(IEnumerable<ConduitOccupiedAxis> axes)
        {
            if (axes == null)
            {
                return;
            }

            foreach (var axis in axes)
            {
                if (axis != null)
                {
                    _occupiedAxes.Add(axis);
                }
            }
        }
    }

    internal sealed class ConduitOccupiedAxis
    {
        public ConduitOccupiedAxis(
            ElementId elementId,
            ElementId sourceElementId,
            XYZ start,
            XYZ end,
            double diameterFeet)
        {
            ElementId = elementId;
            SourceElementId = sourceElementId;
            Start = start;
            End = end;
            DiameterFeet = diameterFeet;
        }

        public ElementId ElementId { get; }

        public ElementId SourceElementId { get; }

        public XYZ Start { get; }

        public XYZ End { get; }

        public double DiameterFeet { get; }

        /// <summary>True when the axis is a plan reservation, not an existing conduit.</summary>
        public bool IsReservation =>
            ElementId == null || ElementId == ElementId.InvalidElementId;
    }
}
