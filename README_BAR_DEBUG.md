# Bar Debug Feature

## Übersicht
Das Bar-Debug Feature hilft bei der Diagnose von Problemen mit dem zeitbasierten Bar-Completion System für das Bar-Based Trailing.

## Konfiguration

### config.json
```json
{
  "bardebug": true,
  // ... andere Parameter
}
```

### Parameter
- **bardebug**: `true` aktiviert Debug-Ausgaben für abgeschlossene Bars, `false` deaktiviert sie (Standard: `false`)

## Funktionsweise

### Was wird ausgegeben?
Wenn `bardebug: true` gesetzt ist, werden **mehrere Debug-Ausgaben** im Log angezeigt, die den kompletten Datenfluss verfolgen:

```
RAW BAR RECEIVED: ELWS 14:41:00 O:1.23 H:1.25 L:1.22 C:1.24 V:1000
BAR FORWARDED TO MANAGER: ELWS 14:41:00 O:1.23 H:1.25 L:1.22 C:1.24
BAR PROCESSING START: ELWS 14:41:00 O:1.23 H:1.25 L:1.22 C:1.24
COMPLETED BAR: ELWS 14:41:10 O:1.23 H:1.25 L:1.22 C:1.24
```

### Format der Debug-Ausgaben

#### 1. RAW BAR RECEIVED
```
RAW BAR RECEIVED: {Symbol} {Zeit} O:{Open} H:{High} L:{Low} C:{Close} V:{Volume}
```
- Zeigt an, dass eine Real-Time Bar von IB empfangen wurde

#### 2. BAR FORWARDED TO MANAGER  
```
BAR FORWARDED TO MANAGER: {Symbol} {Zeit} O:{Open} H:{High} L:{Low} C:{Close}
```
- Zeigt an, dass die Bar an den BarTrailingStopManager weitergeleitet wurde

#### 3. BAR PROCESSING START
```
BAR PROCESSING START: {Symbol} {Zeit} O:{Open} H:{High} L:{Low} C:{Close}
```
- Zeigt an, dass die Bar-Verarbeitung im BarTrailingStopManager gestartet wurde

#### 4. COMPLETED BAR
```
COMPLETED BAR: {Symbol} {Zeit} O:{Open} H:{High} L:{Low} C:{Close}
```
- Zeigt an, dass eine Bar als **abgeschlossen** erkannt wurde (zeitbasierte Detection)

### Parameter-Bedeutung
- **Symbol**: Das gehandelte Symbol (z.B. ELWS)
- **Zeit**: Zeitstempel der Bar (HH:mm:ss Format)
- **O**: Open-Preis der Bar
- **H**: High-Preis der Bar  
- **L**: Low-Preis der Bar
- **C**: Close-Preis der Bar
- **V**: Volumen der Bar (nur bei RAW BAR RECEIVED)

## Diagnose

### Erwartetes Verhalten
Bei korrekter Funktion solltest du:
- **Alle 10 Sekunden** eine "COMPLETED BAR" Zeile sehen (bei 10s Bars)
- **Nur abgeschlossene Bars** in der Ausgabe sehen (nicht live Bars)

### Problemdiagnose

#### Keine "COMPLETED BAR" Ausgaben
- **Problem**: Keine Real-Time Bars werden empfangen
- **Mögliche Ursachen**: 
  - IB nicht verbunden
  - Real-Time Bar Subscription fehlgeschlagen
  - Symbol nicht korrekt konfiguriert

#### Zu viele "COMPLETED BAR" Ausgaben
- **Problem**: Zeitbasierte Bar-Completion funktioniert nicht korrekt
- **Mögliche Ursachen**:
  - Systemzeit nicht synchron
  - Bar-Interval Konfiguration falsch

#### Unregelmäßige "COMPLETED BAR" Ausgaben
- **Problem**: Bars kommen unregelmäßig an
- **Mögliche Ursachen**:
  - Markt geschlossen
  - Geringes Handelsvolumen
  - IB Datenprobleme

## Deaktivierung
Setze `"bardebug": false` in der config.json um die Debug-Ausgaben zu deaktivieren.

## Verwendung mit Bar-Based Trailing
Dieses Feature ist besonders nützlich um zu überprüfen, ob das zeitbasierte Bar-Completion System korrekt funktioniert, bevor das Bar-Based Trailing aktiviert wird.
