# Buy Order Funktionalität

## Übersicht

Das IB Monitor System wurde um Buy-Order-Funktionalität erweitert. Das System kann jetzt automatisch Buy-Orders platzieren und dabei sofort Stop-Loss-Orders erstellen.

## Neue Befehle

### B<quantity> - Market Buy mit Offset
Kauft die angegebene Anzahl Shares zum aktuellen Ask-Preis plus MarketOffset.

**Beispiele:**
- `B100` - Kauft 100 Shares
- `B50` - Kauft 50 Shares

**Funktionsweise:**
1. System fragt aktuellen Ask-Preis ab
2. Berechnet Limit-Preis: Ask + MarketOffset (aus config.json)
3. Platziert Buy-Limit-Order
4. Bei Ausführung: Automatische Stop-Loss-Order-Erstellung

### B<quantity>,<price> - Limit Buy
Kauft die angegebene Anzahl Shares zu einem spezifischen Limit-Preis.

**Beispiele:**
- `B100,4.36` - Kauft 100 Shares zu maximal $4.36
- `B50,12.50` - Kauft 50 Shares zu maximal $12.50

## Automatische Stop-Loss-Verwaltung

### Bei neuen Buy-Orders
- Sofortige Stop-Loss-Order-Erstellung nach Ausführung
- Stop-Loss-Distanz aus `config.json` (`stoploss`: 0.50 USD)
- Stop-Limit-Order mit MarketOffset (`marketoffset`: "3%")

### Bei mehreren Buy-Orders
- Automatische Anpassung des Stop-Loss an den neuen durchschnittlichen Einstandspreis
- Bestehende Stop-Loss-Orders werden modifiziert (nicht neu erstellt)
- Quantity wird automatisch an die neue Positionsgröße angepasst

## Break-Even-Funktionalität

Die bestehende Break-Even-Funktionalität bleibt vollständig erhalten:
- Automatische Umstellung auf Break-Even bei Erreichen des Profit-Schwellenwerts
- Manuelle Break-Even-Aktivierung mit `set breakeven force`
- Konfigurierbare Break-Even-Parameter

## Konfiguration

Relevante Parameter in `config.json`:
```json
{
  "symbol": "AAPL",           // Symbol für Trading
  "stoploss": 0.50,           // Stop-Loss-Distanz in USD
  "marketoffset": "3%",       // Offset für Limit-Preise (absolut oder %)
  "breakeven": 0.10,          // Break-Even-Schwellenwert in USD
  "breakevenoffset": 0.01     // Break-Even-Offset in USD
}
```

## Workflow-Beispiel

1. **Benutzer:** `B100`
2. **System:** Fragt Ask-Preis ab (z.B. $4.30)
3. **System:** Berechnet Limit-Preis: $4.30 + 3% = $4.43
4. **System:** Platziert Buy-Limit-Order für 100 Shares zu $4.43
5. **Bei Ausführung:** Automatische Stop-Loss-Order zu $3.93 (Einstandspreis - $0.50)

6. **Benutzer:** `B50,4.25` (weitere Order)
7. **System:** Platziert Buy-Limit-Order für 50 Shares zu $4.25
8. **Bei Ausführung:** Stop-Loss wird an neuen Average-Preis angepasst

## Sicherheitsfeatures

- Validierung der Order-Größen und Preise
- Überprüfung der Market-Data-Verfügbarkeit
- Umfassendes Logging aller Order-Aktivitäten
- Fehlerbehandlung bei fehlgeschlagenen Orders

## Voraussetzungen

- Symbol muss konfiguriert sein: `set symbol <SYMBOL>`
- Verbindung zu IB Gateway/TWS
- Market-Data-Subscription für das Symbol
- Ausreichende Buying Power im Account
