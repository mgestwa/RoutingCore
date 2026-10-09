using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;

namespace INP_IE.AutoTrayRouting.Diagnostics
{
    /// <summary>
    /// Lightweight logger for routing session. Stores infos and warnings and shows a summary dialog.
    /// </summary>
    public class RouterLogger
    {
        private readonly List<string> _infos = new();
        private readonly List<string> _warnings = new();

        public void Info(string message) => _infos.Add(message);
        public void Warn(string message) => _warnings.Add(message);

        public void ShowSummary(string title = "AutoRoute summary")
        {
            var text = string.Join("\n", _infos);
            if (_warnings.Any())
            {
                text += "\n\nWarnings:" + string.Join("\n- ", new[] { "" }.Concat(_warnings));
            }
            TaskDialog.Show(title, text);
        }

        public (IReadOnlyList<string> infos, IReadOnlyList<string> warnings) Dump() => (_infos, _warnings);
    }
}
