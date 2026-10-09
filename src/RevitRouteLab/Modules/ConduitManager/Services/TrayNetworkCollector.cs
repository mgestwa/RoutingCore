using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitRouteLab.ConduitManager.Models;
using RevitRouteLab.ConduitRouting.Models;

namespace RevitRouteLab.ConduitManager.Services
{
    public class TrayNetworkCollector
    {
        private const int MaxRouteNetworkElements = 2000;

        private readonly Document _document;

        public TrayNetworkCollector()
        {
        }

        public TrayNetworkCollector(Document document)
        {
            _document = document;
        }

        public List<Element> CollectConnectedNetwork(Element startElement, Element endElement, ConduitRoutingReport report)
        {
            return CollectNetwork(startElement, endElement, null, report).Elements;
        }

        /// <summary>
        /// Zbiera sieć korytek osiągalną ze wskazanego elementu. Najpierw idzie
        /// po fizycznych konektorach, a następnie — jeśli ustawienia na to
        /// pozwalają — dokłada dopuszczalne mostki. Mostki muszą trafić do grafu
        /// również wtedy, gdy cel da się osiągnąć długim objazdem. Dopiero
        /// pathfinder porównuje taki objazd z ukaranym kosztem przerwy.
        /// </summary>
        public TrayNetworkResult CollectNetwork(
            Element startElement,
            Element endElement,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            return CollectNetwork(
                startElement,
                endElement,
                settings,
                report,
                settings != null && settings.AllowTrayGapBridging);
        }

        /// <summary>
        /// Zbiera sieć aż do osiągnięcia wszystkich wymaganych elementów.
        /// Wariant jest używany przez trasę ręczną z korytkiem wymuszonym.
        /// </summary>
        public TrayNetworkResult CollectNetwork(
            Element startElement,
            IReadOnlyCollection<Element>? targetElements,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            return CollectNetwork(
                startElement,
                targetElements,
                settings,
                report,
                settings != null && settings.AllowTrayGapBridging);
        }

        /// <summary>
        /// Wariant używany przez lokator końców trasy. Jawny parametr pozwala
        /// włączyć albo wyłączyć mostki bez kopiowania ani chwilowego
        /// modyfikowania ustawień.
        /// </summary>
        public TrayNetworkResult CollectNetwork(
            Element startElement,
            Element endElement,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report,
            bool allowGapBridging)
        {
            return CollectNetwork(
                startElement,
                new[] { endElement },
                settings,
                report,
                allowGapBridging);
        }

        public TrayNetworkResult CollectNetwork(
            Element startElement,
            IReadOnlyCollection<Element>? targetElements,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report,
            bool allowGapBridging)
        {
            var result = new TrayNetworkResult();
            var elements = new Dictionary<int, Element>();
            var queue = new Queue<Element>();
            var targetIds = new HashSet<int>(
                (targetElements ?? new List<Element>())
                .Where(element => element != null)
                .Select(element => element.Id.IntegerValue));

            elements[startElement.Id.IntegerValue] = startElement;
            queue.Enqueue(startElement);
            ExpandByConnectors(elements, queue);

            result.TargetReached = targetIds.All(elements.ContainsKey);
            if (allowGapBridging && CanBridge(settings))
            {
                BridgeAcrossGaps(elements, targetIds, settings, result);
                result.TargetReached = targetIds.All(elements.ContainsKey);
            }

            if (elements.Count >= MaxRouteNetworkElements && !result.TargetReached)
            {
                report.Warnings.Add($"Przerwano analizę sieci po osiągnięciu limitu {MaxRouteNetworkElements} elementów.");
            }

            result.Elements.AddRange(elements.Values);
            return result;
        }

        private bool CanBridge(ConduitRoutingSettings settings)
        {
            return _document != null && settings != null && settings.AllowTrayGapBridging;
        }

        private static void ExpandByConnectors(Dictionary<int, Element> elements, Queue<Element> queue)
        {
            while (queue.Count > 0 && elements.Count < MaxRouteNetworkElements)
            {
                var element = queue.Dequeue();
                foreach (var connector in MepConnectorReader.GetConnectors(element))
                {
                    foreach (Connector reference in connector.AllRefs)
                    {
                        var owner = reference.Owner;
                        if (owner == null || owner.Id.IntegerValue == element.Id.IntegerValue || !TrayElementClassifier.IsTrayElement(owner))
                        {
                            continue;
                        }

                        var ownerKey = owner.Id.IntegerValue;
                        if (elements.ContainsKey(ownerKey))
                        {
                            continue;
                        }

                        elements[ownerKey] = owner;
                        queue.Enqueue(owner);
                    }
                }
            }
        }

        /// <summary>
        /// Dokłada kolejne warstwy sieci, przechodząc przez przerwy w trasie
        /// kablowej. Każda runda dokłada wszystkie przerwy widoczne z aktualnej
        /// sieci, więc wybór najtańszego przejścia zostaje dla pathfindera.
        /// </summary>
        private void BridgeAcrossGaps(
            Dictionary<int, Element> elements,
            IReadOnlyCollection<int> targetElementIds,
            ConduitRoutingSettings settings,
            TrayNetworkResult result)
        {
            var bridgeService = new TrayGapBridgeService(_document);
            var knownBridges = new HashSet<string>();
            var maxRounds = System.Math.Max(1, settings.MaxBridgesPerRoute);

            for (var round = 0; round < maxRounds; round++)
            {
                var discovered = bridgeService.FindBridges(elements.Values.ToList(), settings);
                var addedBridge = false;
                var queue = new Queue<Element>();

                foreach (var bridge in discovered)
                {
                    var key = $"{bridge.FromElement.Id.IntegerValue}:{bridge.ToElement.Id.IntegerValue}:{bridge.LengthMm:F3}";
                    if (!knownBridges.Add(key))
                    {
                        continue;
                    }

                    result.Bridges.Add(bridge);
                    addedBridge = true;

                    foreach (var element in new[] { bridge.FromElement, bridge.ToElement })
                    {
                        if (elements.ContainsKey(element.Id.IntegerValue))
                        {
                            continue;
                        }

                        elements[element.Id.IntegerValue] = element;
                        queue.Enqueue(element);
                    }
                }

                if (!addedBridge)
                {
                    return;
                }

                ExpandByConnectors(elements, queue);
                if (targetElementIds.All(elements.ContainsKey))
                {
                    return;
                }
            }
        }
    }
}
