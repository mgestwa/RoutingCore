using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using INP_IE.AutoTrayRouting.Config;
using INP_IE.AutoTrayRouting.Geometry;

namespace INP_IE.AutoTrayRouting.Search
{
    /// <summary>
    /// Finds nearest cable tray segment candidates and computes cost for connection.
    /// </summary>
    public class CableTraySearchService
    {
        private readonly Document _doc;
        private readonly AutoTrayRoutingSettings _cfg;
        private readonly SpatialIndex _index;

        public CableTraySearchService(Document doc, AutoTrayRoutingSettings cfg, IEnumerable<Element> trays)
        {
            _doc = doc; _cfg = cfg; _index = new SpatialIndex(trays);
        }

        public class Candidate
        {
            public Element Tray { get; set; }
            public XYZ AttachPoint { get; set; }
            public ElementId LevelId { get; set; }
            public double Cost { get; set; }
            public bool ServiceTypeMatch { get; set; }
            public bool DimensionMatch { get; set; }
            public XYZ TrayDirXY { get; set; }
            public ElementId TypeId { get; set; }
            public double WidthFt { get; set; }
            public double HeightFt { get; set; }
            public double LengthFt { get; set; }
        }

        public Candidate FindBestCandidate(Element source, string desiredServiceType)
        {
            var srcPt = GetSourcePoint(source);
            var preferredLevel = source.LevelId;
            var near = _index.QueryNear(srcPt, _cfg.SearchRadiusFt, preferredLevel).ToList();
            if (!near.Any()) return null;

            var candidates = new List<Candidate>();
            foreach (var it in near)
            {
                var lc = it.Element.Location as LocationCurve;
                if (lc == null) continue;
                var cp = SpatialIndex.ClosestPointOnLine(srcPt, lc);
                var d = SpatialIndex.ManhattanDistance(srcPt, cp);
                bool sameLevel = it.LevelId == preferredLevel;

                string trayService = it.Element.get_Parameter(BuiltInParameter.RBS_CTC_SERVICE_TYPE)?.AsString() ?? string.Empty;
                bool stMatch = !string.IsNullOrEmpty(trayService) && trayService.Equals(desiredServiceType, StringComparison.OrdinalIgnoreCase);

                // Dimensions
                GetTrayDims(it.Element, out double w, out double h);
                bool dimMatch = w > 0 && h > 0; // availability check; consumer uses concrete values for adaptive thresholds

                double cost = d + (!sameLevel ? _cfg.Penalties.LevelChange * Math.Abs((GetZ(srcPt) - GetZ(cp))) : 0);
                if (!stMatch) cost += _cfg.Penalties.ServiceTypeMismatch;
                if (!dimMatch) cost += _cfg.Penalties.DimensionMismatch;

                var dirXY = GetTrayDirectionXY(lc);
                var len = TryGetCurveLengthFt(lc);
                candidates.Add(new Candidate
                {
                    Tray = it.Element,
                    AttachPoint = cp,
                    LevelId = it.LevelId,
                    Cost = cost,
                    ServiceTypeMatch = stMatch,
                    DimensionMatch = dimMatch,
                    TrayDirXY = dirXY,
                    TypeId = it.Element.GetTypeId(),
                    WidthFt = w,
                    HeightFt = h,
                    LengthFt = len
                });
            }

            return candidates
                .OrderBy(c => c.Cost)
                .ThenByDescending(c => c.ServiceTypeMatch)
                .ThenByDescending(c => c.DimensionMatch)
                .ThenByDescending(c => c.LengthFt)
                .FirstOrDefault();
        }

        private static double TryGetCurveLengthFt(LocationCurve lc)
        {
            try { return lc?.Curve?.Length ?? 0.0; } catch { return 0.0; }
        }

        private void GetTrayDims(Element e, out double width, out double height)
        {
            width = 0; height = 0;
            // try instance parameters
            width = TryGetParamFeet(e, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            height = TryGetParamFeet(e, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            if (width > 0 && height > 0) return;

            // try type parameters
            var type = _doc.GetElement(e.GetTypeId()) as ElementType;
            if (type != null)
            {
                var w = TryGetParamFeet(type, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                var h = TryGetParamFeet(type, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                if (w > 0) width = w; if (h > 0) height = h;
            }
        }

        private static double TryGetParamFeet(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p == null) return 0;
                return p.StorageType == StorageType.Double ? p.AsDouble() : 0;
            }
            catch { return 0; }
        }

        private static double GetZ(XYZ p) => p?.Z ?? 0;

        private static XYZ GetTrayDirectionXY(LocationCurve lc)
        {
            XYZ dir = XYZ.BasisX;
            if (lc?.Curve is Line ln)
            {
                dir = ln.Direction;
            }
            else if (lc != null)
            {
                var p0 = lc.Curve.GetEndPoint(0); var p1 = lc.Curve.GetEndPoint(1);
                dir = (p1 - p0);
            }
            var v = new XYZ(dir.X, dir.Y, 0);
            double len = System.Math.Sqrt(v.X * v.X + v.Y * v.Y);
            return len < 1e-9 ? new XYZ(1, 0, 0) : new XYZ(v.X / len, v.Y / len, 0);
        }

        public static Connector GetSourceConnector(Element e)
        {
            var mep = (e as FamilyInstance)?.MEPModel;
            var cm = mep?.ConnectorManager;
            if (cm != null)
            {
                foreach (Connector c in cm.Connectors)
                {
                    if (c.Domain == Domain.DomainElectrical || c.Domain == Domain.DomainCableTrayConduit)
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        public static XYZ GetSourcePoint(Element e)
        {
            var con = GetSourceConnector(e);
            if (con != null) return con.Origin;
            // Fallback to bbox center
            var bb = e.get_BoundingBox(null);
            if (bb != null)
            {
                var c = (bb.Min + bb.Max) * 0.5;
                return new XYZ(c.X, c.Y, c.Z);
            }
            var loc = e.Location as LocationPoint;
            return loc?.Point ?? XYZ.Zero;
        }

        public class BackFaceInfo
        {
            public XYZ Point { get; set; }
            public XYZ BackDir { get; set; }
        }

        /// <summary>
        /// Computes the center of the back face of a FamilyInstance (opposite to FacingOrientation)
        /// and returns both the point and outward back direction.
        /// If not FamilyInstance, returns bbox center and a rough direction towards -Y.
        /// Adds a small outward offset using cfg.ConnectorOffsetFt if provided.
        /// </summary>
        public BackFaceInfo GetBackFaceInfo(Element e)
        {
            var bb = e.get_BoundingBox(null);
            XYZ fallbackDir = new XYZ(0, -1, 0);
            if (bb == null)
                return new BackFaceInfo { Point = GetSourcePoint(e), BackDir = fallbackDir };

            var center = (bb.Min + bb.Max) * 0.5;

            var fi = e as FamilyInstance;
            XYZ vBack = fallbackDir;
            if (fi != null)
            {
                var vFront = fi.FacingOrientation; // pointing out of FRONT
                if (vFront != null)
                    vBack = (-vFront).Normalize();
            }

            var dx = bb.Max.X - bb.Min.X;
            var dy = bb.Max.Y - bb.Min.Y;
            var dz = bb.Max.Z - bb.Min.Z;
            var halfDepth = 0.5 * (System.Math.Abs(vBack.X) * dx + System.Math.Abs(vBack.Y) * dy + System.Math.Abs(vBack.Z) * dz);

            var p = new XYZ(center.X + vBack.X * halfDepth, center.Y + vBack.Y * halfDepth, center.Z + vBack.Z * halfDepth);
            // push slightly outside
            var off = _cfg.ConnectorOffsetFt > 0 ? _cfg.ConnectorOffsetFt : 0;
            p = new XYZ(p.X + vBack.X * off, p.Y + vBack.Y * off, p.Z + vBack.Z * off);
            return new BackFaceInfo { Point = p, BackDir = vBack };
        }

        /// <summary>
        /// Keeps compatibility; returns only point.
        /// </summary>
        public XYZ GetBackFaceCenter(Element e)
        {
            return GetBackFaceInfo(e).Point;
        }
    }
}
