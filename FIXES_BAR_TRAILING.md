# Korrekturen für Bar-basiertes Trailing Stop-Loss

## Behobene Probleme

### 1. Symbol-Synchronisation für Real-Time Bars
**Problem:** Real-Time Bars wurden nicht auf neues Symbol umgeschaltet
- Bar-Daten von AAPL (~$208) wurden für CLRO (~$10.60) verwendet
- Trailing Stop berechnete falsche Werte

**Lösung:**
- `PositionMonitorService.UpdateSymbol()` erweitert
- Aufruf von `_realTimeBarService.UpdateSymbol(newSymbol)` hinzugefügt
- Bar-History für altes Symbol wird gelöscht: `_barTrailingStopManager.ClearHistory(oldSymbol)`

### 2. Stop-Loss Order Validation
**Problem:** Trailing arbeitete ohne aktive Stop-Loss Order
- Nach Order Cancel (Code 202) versuchte System weiterhin Order zu modifizieren
- Keine Überprüfung auf Order-Existenz

**Lösung:**
- In `OnRealTimeBarFromService()` Prüfung hinzugefügt: `position.StopLossOrderId.HasValue`
- Trailing wird nur bei aktiver Order ausgeführt
- Debug-Logging für bessere Nachverfolgung

## Code-Änderungen

### PositionMonitorService.cs

#### UpdateSymbol() Methode erweitert:
```csharp
// Update real-time bars subscription to new symbol
_realTimeBarService.UpdateSymbol(newSymbol);

// Clear bar history for old symbol
if (!string.IsNullOrEmpty(oldSymbol))
{
    _barTrailingStopManager.ClearHistory(oldSymbol);
}
```

#### OnRealTimeBarFromService() Validation hinzugefügt:
```csharp
if (position != null && position.IsLongPosition && position.StopLossOrderId.HasValue)
{
    // Only process trailing if we have an active stop-loss order
    var newStopPrice = _barTrailingStopManager.ProcessNewBar(bar, position);
    // ...
}
else if (position != null && position.IsLongPosition && !position.StopLossOrderId.HasValue)
{
    _logger.Debug("Bar-based trailing skipped for {Symbol} - no active stop-loss order (Bar Close:{Close:F2})", 
        _config.Symbol, bar.Close);
}
```

## Erwartete Verbesserungen

### ✅ Korrekte Symbol-Synchronisation
- Real-Time Bars werden automatisch auf neues Symbol umgestellt
- Keine falschen Bar-Daten mehr von vorherigem Symbol
- Saubere Bar-History für jedes Symbol

### ✅ Robuste Order-Verwaltung
- Trailing funktioniert nur bei aktiven Stop-Loss Orders
- Keine Versuche, nicht-existente Orders zu modifizieren
- Bessere Fehlerbehandlung bei Order-Cancellations

### ✅ Verbesserte Logging
- Klare Meldungen bei Symbol-Wechsel
- Debug-Informationen bei übersprungenen Trailing-Updates
- Nachverfolgbare Order-Referenzen

## Test-Szenarien

### Szenario 1: Symbol-Wechsel
1. System läuft mit AAPL
2. Symbol wird auf CLRO gewechselt
3. ✅ Real-Time Bars werden für CLRO abonniert
4. ✅ AAPL Bar-History wird gelöscht
5. ✅ Trailing verwendet korrekte CLRO Bar-Daten

### Szenario 2: Order-Cancellation
1. Position mit aktivem Stop-Loss
2. Stop-Loss wird manuell gelöscht
3. ✅ Trailing wird gestoppt
4. ✅ Keine Versuche, gelöschte Order zu modifizieren
5. ✅ Debug-Meldung über übersprungenes Trailing

## Konfiguration

Die bestehende Konfiguration bleibt unverändert:
```json
{
  "usebarbasedtrailing": true,
  "bartrailingoffset": 0.05,
  "bartrailingleokback": 2,
  "barinterval": 10
}
```

## Kompatibilität

- ✅ Vollständig rückwärtskompatibel
- ✅ Keine Breaking Changes
- ✅ Bestehende Funktionalität unverändert
- ✅ Zusätzliche Robustheit und Sicherheit
