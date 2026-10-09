# Wyniki weryfikacji — 2026-10-09

| Kontrola | Wynik |
| --- | --- |
| Revit 2024, Release / net48 | Kompilacja i przygotowanie paczki: 0 błędów, 185 ostrzeżeń |
| Revit 2025, Release / net8.0-windows | Kompilacja i przygotowanie paczki: 0 błędów, 190 ostrzeżeń |
| Testy algorytmiczne .NET 8 | 15 188 sprawdzeń zakończonych powodzeniem; 100 losowych grafów porównanych z niezależnym rozwiązaniem |
| Zgodność kopii modułów | SHA-256 wszystkich 53 plików zgodne ze źródłem |
| Brak zmian aplikacji źródłowej | 316 plików porównanych z początkowymi sumami SHA-256; ten sam zestaw plików i stan Git |
| Skrypty PowerShell | Poprawne składniowo; skrypt kompilacji i pakowania wykonany dla obu wersji |
| Instalacja / uruchomienie w Revicie | Nie wykonywano |

Ostrzeżenia C# dotyczą przede wszystkim dopuszczalności null oraz przestarzałych elementów API w skopiowanym kodzie. Revit 2025 dodatkowo zgłasza ostrzeżenia MSB3277 o wersjach zależności zainstalowanego API; kompilacja kończy się poprawnie. Weryfikacja ładowania i działania dodatku w procesie Revita pozostaje testem ręcznym.

W próbie kompilacji dla Revita 2026 stwierdzono brak `ElementId.IntegerValue`. Ta wersja jest jawnie wyłączona z konfiguracji obsługiwanych wersji; nie wykonano migracji identyfikatorów w wiernie skopiowanych modułach. Wyniku kompilacji nie należy utożsamiać z testem poprawności trasowania na rzeczywistym modelu.

Zachowano istniejące przed rozpoczęciem pracy zmiany użytkownika, w tym modyfikację `ConduitRoutingService.cs`. Kopia reprezentuje aktualne pliki robocze, a nie tylko ostatni commit.

Paczki zawierają własny manifest o AddInId `160999A9-DF52-4A01-A492-F01009A47928`. Nie wykonano rejestracji ani instalacji w katalogach dodatków Autodesk.
