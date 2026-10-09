using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitRouteLab.AutoTrayRouting.Config;

namespace RevitRouteLab.AutoTrayRouting.Services
{
    /// <summary>
    /// Resolves service type string based on rules in settings and inspected element/type.
    /// </summary>
    public class ServiceTypeResolver
    {
        private readonly AutoTrayRoutingSettings _cfg;
        public ServiceTypeResolver(AutoTrayRoutingSettings cfg) { _cfg = cfg; }

        public string Resolve(Element e, out bool matched)
        {
            matched = false;
            string name = e?.Name ?? string.Empty;

            foreach (var rule in _cfg.ServiceTypeMapping ?? Enumerable.Empty<ServiceTypeRule>())
            {
                switch ((rule.RuleType ?? "").Trim().ToLowerInvariant())
                {
                    case "namestartswith":
                        if (!string.IsNullOrEmpty(rule.Pattern) && name.StartsWith(rule.Pattern, StringComparison.OrdinalIgnoreCase))
                        { matched = true; return rule.ServiceType ?? _cfg.DefaultServiceType; }
                        break;
                    case "parametercontains":
                        if (!string.IsNullOrEmpty(rule.ParameterName) && !string.IsNullOrEmpty(rule.Pattern))
                        {
                            var p = e.LookupParameter(rule.ParameterName);
                            var val = p != null ? (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString()) : null;
                            if (!string.IsNullOrEmpty(val) && val.IndexOf(rule.Pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                            { matched = true; return rule.ServiceType ?? _cfg.DefaultServiceType; }
                        }
                        break;
                }
            }

            return _cfg.DefaultServiceType;
        }
    }
}
