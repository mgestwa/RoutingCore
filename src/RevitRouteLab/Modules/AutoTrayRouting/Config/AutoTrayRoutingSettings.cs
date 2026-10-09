using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitRouteLab.AutoTrayRouting.Config
{
    /// <summary>
    /// DTO for AutoTray routing settings loaded from JSON. Values in mm are converted to ft on load.
    /// </summary>
    public class AutoTrayRoutingSettings
    {
        // Distances in feet (converted from mm on load)
        public double SearchRadiusFt { get; set; }
        public double SearchRadiusZFt { get; set; }
        public double MinStraightFt { get; set; }
        public double MinClearanceFt { get; set; }
        public double ConnectorOffsetFt { get; set; }
        public double MinBendRadiusFt { get; set; }

        public string DefaultServiceType { get; set; } = "Power";

        public Penalties Penalties { get; set; } = new Penalties();

        public List<ServiceTypeRule> ServiceTypeMapping { get; set; } = new List<ServiceTypeRule>();

        public static AutoTrayRoutingSettings Load(string assemblyFolder)
        {
            var searchPaths = new[]
            {
                Path.Combine(assemblyFolder, "AutoTrayRouting", "Config", "AutoTrayRouting.default.json"),
                Path.Combine(assemblyFolder, "Config", "AutoTrayRouting.default.json"),
                Path.Combine(assemblyFolder, "AutoTrayRouting.default.json")
            };

            string path = searchPaths.FirstOrDefault(File.Exists);

            AutoTrayRoutingSettings settings = new AutoTrayRoutingSettings();
            if (path != null)
            {
                var json = File.ReadAllText(path);
                var jObj = JObject.Parse(json);

                double GetMm(JObject o, string name, double defMm)
                {
                    var t = o[name];
                    if (t == null) return defMm;
                    if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
                        return t.Value<double>();
                    return defMm;
                }

                settings.SearchRadiusFt = MmToFt(GetMm(jObj, "searchRadius_mm", 15000));
                settings.SearchRadiusZFt = MmToFt(GetMm(jObj, "searchRadiusZ_mm", 4000));
                settings.MinStraightFt = MmToFt(GetMm(jObj, "minStraight_mm", 200));
                settings.MinClearanceFt = MmToFt(GetMm(jObj, "minClearance_mm", 50));
                settings.ConnectorOffsetFt = MmToFt(GetMm(jObj, "connectorOffset_mm", 50));
                settings.MinBendRadiusFt = MmToFt(GetMm(jObj, "minBendRadius_mm", 100));

                var dst = jObj["defaultServiceType"];
                if (dst != null && dst.Type == JTokenType.String)
                    settings.DefaultServiceType = dst.Value<string>();

                var pen = jObj["penalties"] as JObject;
                if (pen != null)
                    settings.Penalties = pen.ToObject<Penalties>() ?? new Penalties();

                var map = jObj["serviceTypeMapping"] as JArray;
                if (map != null)
                    settings.ServiceTypeMapping = map.ToObject<List<ServiceTypeRule>>() ?? new List<ServiceTypeRule>();
            }
            else
            {
                settings.SearchRadiusFt = MmToFt(15000);
                settings.SearchRadiusZFt = MmToFt(4000);
                settings.MinStraightFt = MmToFt(200);
                settings.MinClearanceFt = MmToFt(50);
                settings.ConnectorOffsetFt = MmToFt(50);
                settings.MinBendRadiusFt = MmToFt(100);
            }

            if (string.IsNullOrWhiteSpace(settings.DefaultServiceType))
                settings.DefaultServiceType = "Power";

            if (settings.Penalties == null) settings.Penalties = new Penalties();
            if (settings.ServiceTypeMapping == null) settings.ServiceTypeMapping = new List<ServiceTypeRule>();

            return settings;
        }

        public static double MmToFt(double mm) => mm / 304.8;
    }

    public class Penalties
    {
        public double LevelChange { get; set; } = 3.0;
        public double DimensionMismatch { get; set; } = 1.5;
        public double ServiceTypeMismatch { get; set; } = 2.0;
        public double JoinFittings { get; set; } = 1.0;
    }

    public class ServiceTypeRule
    {
        // RuleType: "NameStartsWith" or "ParameterContains"
        public string RuleType { get; set; } = "NameStartsWith";
        public string Pattern { get; set; } = string.Empty;
        public string ParameterName { get; set; } = string.Empty;
        public string ServiceType { get; set; } = "Power";
    }
}
