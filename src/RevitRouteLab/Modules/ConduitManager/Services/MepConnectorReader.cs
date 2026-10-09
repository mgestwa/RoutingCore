using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace INP_IE.ConduitManager.Services
{
    public static class MepConnectorReader
    {
        public static IEnumerable<Connector> GetConnectors(Element element)
        {
            if (element is MEPCurve mepCurve)
            {
                foreach (Connector connector in mepCurve.ConnectorManager.Connectors)
                {
                    yield return connector;
                }
            }
            else if (element is FamilyInstance familyInstance && familyInstance.MEPModel?.ConnectorManager != null)
            {
                foreach (Connector connector in familyInstance.MEPModel.ConnectorManager.Connectors)
                {
                    yield return connector;
                }
            }
        }
    }
}