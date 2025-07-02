# Bar-basiertes Trailing Stop-Loss System

## Übersicht

Das Bar-basierte Trailing Stop-Loss System erweitert das bestehende IB Monitor System um eine erweiterte Trailing-Funktionalität, die auf 10-Sekunden-Bars basiert. Anstatt nur auf Tick-Daten zu reagieren, analysiert das System vollständige Bars und passt den Stop-Loss basierend auf definierten Kriterien an.

## Funktionsweise

### Grundprinzip
- Das System abonniert Real-Time Bars über die IB API (Standard: 10 Sekunden)
- Jede neue Bar wird analysiert, ob sie die Kriterien für eine Stop-Loss Anpassung erfüllt
- Der Stop wird unter das niedrigste Low der letzten X Bars (konfigurierbar) plus Offset gesetzt

### Trigger-Bedingungen
Eine Bar löst eine Stop-Loss Anpassung aus, wenn:
1. **Bar schließt positiv**: `Close > Open`
2. **Bar schließt über Einstiegspreis**: `Close > AveragePrice`

### Stop-Berechnung
```
Neuer Stop = MIN(Low der letzten X Bars) - BarTrailingOffset
```

## Konfiguration

### Neue Parameter in config.json

```json
{
  "usebarbasedtrailing": true,     // Aktiviert/deaktiviert das Feature
  "bartrailingoffset": 0.05,       // Abstand unter dem niedrigsten Low (in $)
  "bartrailingleokback": 2,        // Anzahl vorheriger Bars zu berücksichtigen
  "barinterval": 10                // Bar-Intervall in Sekunden
}
```

### Parameter-Erklärung

#### `usebarbasedtrailing` (boolean)
- **Standard**: `false`
- **Beschreibung**: Aktiviert oder deaktiviert das Bar-basierte Trailing
- **Beispiel**: `true` = Feature aktiv, `false` = Feature inaktiv

#### `bartrailingoffset` (double)
- **Standard**: `0.05` (5 Cent)
- **Beschreibung**: Abstand in Dollar unter dem niedrigsten Low
- **Beispiel**: Bei `0.05` wird der Stop 5 Cent unter dem niedrigsten Low gesetzt

#### `bartrailingleokback` (integer)
- **Standard**: `0` (nur aktuelle Bar)
- **Beschreibung**: Anzahl vorheriger Bars, die in die Berechnung einbezogen werden
- **Beispiele**:
  - `0` = Nur aktuelle Bar (aggressiv)
  - `1` = Aktuelle + 1 vorherige Bar
  - `2` = Aktuelle + 2 vorherige Bars (konservativ)
  - `5` = Aktuelle + 5 vorherige Bars (sehr konservativ)

#### `barinterval` (integer)
- **Standard**: `10` Sekunden
- **Beschreibung**: Zeitintervall für Real-Time Bars
- **Hinweis**: IB API unterstützt 5, 10, 15, 30 Sekunden

## Beispiel-Szenarien

### Szenario 1: Aggressive Einstellung
```json
{
  "usebarbasedtrailing": true,
  "bartrailingoffset": 0.03,
  "bartrailingleokback": 0,
  "barinterval": 10
}
```
- Stop wird unter jede positive Bar gesetzt (nur aktuelle Bar)
- Sehr enge Verfolgung des Kurses
- Höheres Risiko von vorzeitigen Ausstiegen

### Szenario 2: Konservative Einstellung
```json
{
  "usebarbasedtrailing": true,
  "bartrailingoffset": 0.10,
  "bartrailingleokback": 5,
  "barinterval": 10
}
```
- Stop wird unter das niedrigste Low der letzten 6 Bars gesetzt
- Schutz vor kurzfristigen Schwankungen
- Größerer Abstand zum aktuellen Kurs

### Szenario 3: Ausgewogene Einstellung
```json
{
  "usebarbasedtrailing": true,
  "bartrailingoffset": 0.05,
  "bartrailingleokback": 2,
  "barinterval": 10
}
```
- Stop wird unter das niedrigste Low der letzten 3 Bars gesetzt
- Gute Balance zwischen Schutz und Flexibilität

## Integration mit bestehendem System

### Kompatibilität
- Das Bar-basierte System arbeitet **parallel** zum bestehenden Break-Even System
- Beide Systeme können gleichzeitig aktiv sein
- Das Bar-basierte System überschreibt nur dann den Stop, wenn der neue Stop höher ist

### Prioritäten
1. **Break-Even**: Hat Vorrang, wenn ausgelöst
2. **Bar-basiertes Trailing**: Wird nur angewendet, wenn kein Break-Even aktiv ist
3. **Standard Stop-Loss**: Wird durch beide Systeme überschrieben

## Logging und Monitoring

### Log-Nachrichten
```
[INFO] Bar-based trailing stop triggered for AAPL: Bar Close:150.25 > Entry:149.50, New Stop:149.95 (Lookback: 2 bars)
[DEBUG] Trailing stop calculation: Lowest Low of 3 bars = 150.00, Offset = 0.05, Stop = 149.95
[INFO] Bar-based trailing stop updated for AAPL: New Stop:149.95 Limit:149.85 based on bar Close:150.25
```

### Status-Abfrage
Das System bietet eine Status-Funktion über die CommandService:
```
> status
Bar-based trailing: AAPL: 3 bars, Latest: O:150.10 H:150.30 L:150.00 C:150.25, Lowest Low: 150.00, Calculated Stop: 149.95
```

## Technische Details

### Real-Time Bar Subscription
- Automatische Anmeldung bei IB-Verbindung (wenn Feature aktiviert)
- Verwendung von TRADES-Daten für präzise OHLC-Werte
- Ticker-ID-Bereich: 2000+ (um Konflikte zu vermeiden)

### Thread-Sicherheit
- Alle Bar-Verarbeitungen sind thread-safe
- Verwendung von ConcurrentDictionary und Locks
- Keine Race-Conditions zwischen Bar-Updates und Order-Modifikationen

### Fehlerbehandlung
- Robuste Behandlung von IB-Verbindungsfehlern
- Automatische Wiederanmeldung bei Reconnect
- Graceful Degradation bei API-Fehlern

## Vorteile gegenüber Tick-basiertem Trailing

1. **Weniger Rauschen**: Bars filtern kurzfristige Schwankungen
2. **Bessere Trendfolge**: Vollständige Bar-Analyse statt einzelner Ticks
3. **Konfigurierbare Sensitivität**: Lookback-Parameter für verschiedene Strategien
4. **Reduzierte Order-Frequenz**: Weniger Stop-Loss Modifikationen

## Limitierungen

1. **IB API Beschränkungen**: Real-Time Bars nur für bestimmte Instrumente verfügbar
2. **Marktdaten-Abhängigkeit**: Benötigt aktive Marktdaten-Subscription
3. **Latenz**: Leichte Verzögerung durch Bar-Bildung (max. Bar-Intervall)
4. **Nur Long-Positionen**: Aktuell nur für Long-Positionen implementiert

## Troubleshooting

### Häufige Probleme

#### "Cannot subscribe to real-time bars - not connected to IB"
- **Lösung**: IB Gateway/TWS Verbindung prüfen
- **Check**: Port und Client-ID in config.json

#### "No bar history for SYMBOL"
- **Lösung**: Warten auf erste Bars nach Aktivierung
- **Check**: Symbol in config.json korrekt konfiguriert

#### "Bar does not qualify for trailing stop update"
- **Normal**: Bar war negativ oder unter Einstiegspreis
- **Check**: Log-Level auf DEBUG für Details

### Debug-Modus
```json
{
  "loglevel": "DEBUG"
}
```
Zeigt detaillierte Bar-Verarbeitung und Berechnungen.
