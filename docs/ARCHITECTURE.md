# Mapa wydzielonych modułów

## Przepływ

`RoutePointsCommand / RouteViaTrayCommand / RouteDevicesCommand`
→ `ConduitRoutingSettingsWindow`
→ `ConduitRoutingService`
→ `ConduitRoutePlanner`
→ `TrayNetworkCollector` + `TrayGapBridgeService`
→ `TrayGraphBuilder`
→ `ConduitPathfinder`
→ `ConduitSegmentBuilder` + `FreeAirLegBuilder`
→ `ConduitPositionAllocator`
→ `ConduitExecutionPlan`
→ przegląd planu
→ `ConduitElementFactory` + `ConduitConnectorService` w transakcji Revita.

`RouteDevicesCommand` dobiera wcześniej końce za pomocą `ConduitAutomaticEndpointLocatorService`. Usługa używa `ComputeDistances`, aby oceniać kandydatów według kosztu dostępnej drogi w sieci.

## Gdzie szukać algorytmów

Wszystkie poniższe ścieżki są względne wobec `src/RevitRouteLab/Modules`.

| Plik | Odpowiedzialność |
| --- | --- |
| `ConduitManager/Services/ConduitPathfinder.cs` | Dijkstra, koszty do wszystkich węzłów oraz wariant z obowiązkowym przejściem po korytku |
| `ConduitManager/Models/TrayGraph.cs` | Lista sąsiedztwa skierowanego grafu |
| `ConduitManager/Models/TrayGraphEdge.cs` | Koszt, element i geometria przejścia; odcinek korytka, kształtka, konektor, mostek |
| `ConduitManager/Services/TrayGraphBuilder.cs` | Tworzenie węzłów, krawędzi oraz punktów początku i końca |
| `ConduitManager/Services/TrayNetworkCollector.cs` | Przejście po konektorach sieci i uwzględnienie dopuszczalnych przerw |
| `ConduitManager/Services/TrayGapBridgeService.cs` | Geometria mostków i ograniczenia łączenia przerw |
| `ConduitManager/Services/ConduitRoutePlanner.cs` | Zamiana trasy grafowej na plan odcinków |
| `ConduitManager/Services/ConduitPositionAllocator.cs` | Dobór położenia conduitu wewnątrz przekrojów |
| `ConduitManager/Services/FreeAirLegBuilder.cs` | Zejścia do urządzeń poza korytkiem |
| `AutoTrayRouting/Search/CableTraySearchService.cs` | Wyszukiwanie i ranking korytek |
| `AutoTrayRouting/Routing/PathPlanner.cs` | Geometryczne planowanie dojścia do korytka |
| `AutoTrayRouting/Routing/ConnectionBuilder.cs` | Tworzenie połączeń korytek |

## Granice wydzielenia

Oryginalne `ConduitManagerWorkflow`, jego części, baza projektu i interfejs Circuit Managera nie zostały skopiowane. Ich rolę w uruchamianiu trasowania przejęły małe polecenia `RevitRouteLab.Commands`. Oryginalne polecenia zależne od Nice3point również zastąpiono adapterami opartymi na `IExternalCommand`. Nie są potrzebne pakiety Nice3point, CommunityToolkit ani OpenXML.

Algorytmy nadal używają typów Autodesk. To samodzielny projekt dodatku Revit, nie program EXE pracujący na plikach RVT poza Revitem. Testy konsolowe wykonują wyłącznie logikę grafową na danych syntetycznych.

## Ewentualny rozwój A*

Punktem wejścia jest `ConduitPathfinder.TryFindShortestPath`. Obecny algorytm używa kosztu przebytej drogi. A* wymagałby jawnego dodania heurystyki i sprawdzenia jej dopuszczalności względem kosztów konektorów i mostków. Wyszukiwanie kosztów do wszystkich kandydatów nadal ma zastosowanie dla Dijkstry. Nie wprowadzono takiej zmiany w ramach wiernego wydzielenia.
