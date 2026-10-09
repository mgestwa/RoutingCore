# Revit Route Lab

Niezależna mikroaplikacja w formie dodatku do Autodesk Revit, wydzielona przez **kopiowanie** modułów z INP_IE dnia 2026-10-09. Rozwiązanie: `RevitRouteLab.sln`.

## Co zawiera

- Wyszukiwanie najkrótszej trasy po grafie korytek, także przez wskazane korytko.
- Zbieranie sieci konektorów, budowanie grafu i mostkowanie dopuszczalnych przerw.
- Automatyczny dobór końców trasy przy urządzeniach, opcjonalne zejścia poza korytka.
- Przydzielanie miejsca w przekroju korytka z uwzględnieniem istniejących conduitów.
- Planowanie i tworzenie conduitów oraz połączeń.
- Dotychczasowy AutoTrayRouter: wyszukiwanie korytek, geometryczne planowanie dojścia i tworzenie geometrii.
- Własną kartę **Route Lab**, pięć poleceń, okno ustawień i przegląd planu przed utworzeniem conduitów.

**Algorytm najkrótszej ścieżki w źródle to Dijkstra, nie A\*.** Nie znaleziono implementacji A* i nie dodawano nowego algorytmu pod tą nazwą. `PathPlanner` w AutoTrayRouting buduje geometryczne odcinki trasy, a `CableTraySearchService` ocenia kandydatów. Nie jest to ogólny solver omijający wszystkie przeszkody budowlane w modelu.

## Niezależność od INP_IE

53 pliki w `src/RevitRouteLab/Modules` skopiowano bez zmiany zawartości. Ich pochodzenie i sumy SHA-256 zawiera `extraction-manifest.json`. Zachowano historyczne przestrzenie nazw `INP_IE.*`, lecz są kompilowane do osobnej biblioteki **RevitRouteLab.dll**. Nie ma odwołania do projektu ani biblioteki INP_IE, dowiązań do oryginalnych plików ani wspólnego kroku wdrożenia.

Nowy kod uruchamiający znajduje się w `Application.cs` i `Commands/RoutingCommands.cs`. Dodatek ma osobny AddInId, własny manifest i własny katalog instalacyjny. Nie zawiera bazy relacji Circuit Managera, obsługi obwodów, Excela, monitoringu ani pozostałych narzędzi INP_IE. Polecenia mikroaplikacji nie rejestrują relacji i nie zapisują metadanych `INP_Relacja*`. Skopiowana klasa `ConduitMetadataWriter` pozostaje zależnością serwisu, lecz przy pustych metadanych nie wykonuje zapisów.

Budowanie i testy nie instalują dodatku. Uruchomienie polecenia tworzenia geometrii po ręcznej instalacji zmienia otwarty model Revit zgodnie z wybranym działaniem; samo przygotowanie planu i opcja pokazania korytek nie tworzą elementów.

## Wymagania i kompilacja

- Windows, .NET SDK obsługujący .NET 8 oraz .NET Framework 4.8 Developer Pack dla Revita 2024.
- Revit odpowiedniej wersji lub dostęp do jego `RevitAPI.dll` i `RevitAPIUI.dll`.
- NuGet `Newtonsoft.Json` 13.0.3, zgodny z kopiowanym kodem. Pierwsze odtwarzanie pakietów może wymagać internetu.

W PowerShell, z katalogu mikroaplikacji:

```powershell
.\scripts\Build.ps1 -RevitVersion 2024
.\scripts\Build.ps1 -RevitVersion 2025
```

Revit 2024 używa `net48`, a 2025 używa `net8.0-windows`. Zweryfikowano kompilację obu wersji. **Revit 2026 nie jest obsługiwany przez wiernie skopiowany kod**: usunięto w nim `ElementId.IntegerValue`, używane w modułach źródłowych. Migracja do 64-bitowych identyfikatorów wymaga osobnej zmiany. Starszych wersji niż 2024 nie weryfikowano.

Inna lokalizacja API:

```powershell
.\scripts\Build.ps1 -RevitVersion 2024 -RevitApiPath 'D:\RevitAPI\2024'
```

Gotowe paczki są w `artifacts/Revit2024` i `artifacts/Revit2025`. Każda zawiera manifest `RevitRouteLab.addin` i podkatalog `RevitRouteLab/<rok>` z biblioteką, symbolami, Newtonsoft.Json i konfiguracją AutoTrayRouting. Biblioteki Autodesk nie są dystrybuowane.

## Instalacja opcjonalna

Zamknij Revit, a następnie uruchom:

```powershell
.\scripts\Install.ps1 -RevitVersion 2024
```

Skrypt kopiuje wyłącznie Route Lab do `%APPDATA%/Autodesk/Revit/Addins/2024`. Nie nadpisuje istniejącej instalacji Route Lab; przy kolizji kończy działanie. Aby odinstalować dodatek, przy zamkniętym Revicie usuń wyłącznie `RevitRouteLab.addin` i katalog `RevitRouteLab` z katalogu dodatków danej wersji. Pliki INP_IE pozostają oddzielne.

## Użycie

1. **Między punktami** — wskaż dwa punkty na prostych korytkach, ustaw parametry conduitu i przejrzyj plan. Wybierz utworzenie geometrii albo zaznaczenie korytek trasy bez tworzenia elementów.
2. **Przez korytko** — dodatkowo wskaż proste korytko, które trasa musi faktycznie przejść.
3. **Między urządzeniami** — wskaż kolejno początek i koniec. Dobór pobliskich korytek uwzględnia koszt trasy; zejścia poza korytka i mostkowanie zależą od ustawień.
4. **Conduit w korytkach** — zaznacz korytka/kształtki lub wskaż je po uruchomieniu. Zachowano źródłowe zachowanie tworzenia pojedynczej reprezentacji conduitu.
5. **Auto korytka** — przed uruchomieniem zaznacz dokładnie dwa elementy źródłowe. Wybierz korytka, conduit 25 mm lub oba. Ten starszy moduł uruchamia tworzenie geometrii bez etapu przeglądu planu.

Wyszukiwanie i tworzenie wymagają odpowiedniej geometrii, typów conduitów/korytek oraz rodzin kształtek w modelu. Moduł pracuje na elementach aktywnego dokumentu; nie dodano trasowania po modelach podlinkowanych. `Esc` anuluje wybór elementów. Przegląd planu pokazuje długość, przejścia przez przerwy, zejścia i ostrzeżenia. Zaznaczenie korytek jest podglądem elementów źródłowych, a nie rysunkiem projektowanej osi conduitu.

## Weryfikacja

```powershell
.\scripts\Verify-Extraction.ps1
.\scripts\Verify-Extraction.ps1 -CompareWithSource
dotnet run --project .\tests\Routing.AlgorithmChecks -c Release
```

Pierwsze polecenie sprawdza zgodność kopii z zapisanym stanem. Drugie wymaga dostępu do oryginalnego katalogu i sprawdza także źródła. Po celowej modyfikacji skopiowanych modułów kontrola wykaże różnice — to oczekiwane.

Testy kompilują bezpośrednio skopiowany `ConduitPathfinder`, `TrayGraph`, `TrayGraphEdge` i klasyfikator. Minimalne zastępniki typów Autodesk są używane tylko w testach algorytmu; nie symulują geometrii ani transakcji Revita. Sprawdzono trasy ważone, brak połączenia, kierunki krawędzi, remisy, zerowe cykle, wymuszone korytko, karę mostka i 100 losowych grafów względem niezależnego algorytmu Floyda–Warshalla. Łącznie: **15 188 sprawdzeń**.

Kompilacja zgłasza odziedziczone ostrzeżenia dotyczące null i starszego API. Nie wykonywano testu wewnątrz działającego Revita. Scenariusze ręcznej weryfikacji opisano w `docs/REVIT-SMOKE-TESTS.md`, a wyniki kontroli w `docs/VALIDATION.md`.
