using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace INP_IE.ConduitManager.Models
{
    /// <summary>
    /// Sieć korytek zebrana dla jednej trasy wraz z przerwami, które trzeba było
    /// zmostkować, żeby w ogóle dojść do celu.
    /// </summary>
    public sealed class TrayNetworkResult
    {
        public List<Element> Elements { get; } = new List<Element>();

        public List<TrayGapBridge> Bridges { get; } = new List<TrayGapBridge>();

        /// <summary>True, gdy element docelowy znalazł się w zebranej sieci.</summary>
        public bool TargetReached { get; set; }

        /// <summary>True, gdy do osiągnięcia celu potrzebne było mostkowanie przerw.</summary>
        public bool UsedBridging => Bridges.Count > 0;
    }
}
