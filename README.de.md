# MSSQL Tool für SSMS 22

[English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | **Deutsch** | [Français](README.fr.md) | [Español](README.es.md)

[![Install from VSIX Gallery](https://ssmsgallery.azurewebsites.net/badge/MSSQLTool.svg)](https://ssmsgallery.azurewebsites.net/extension/MSSQLTool/) [![Open VSIX Gallery](https://www.vsixgallery.com/badge/MSSQLTool.svg)](https://www.vsixgallery.com/extension/MSSQLTool)

🔥 **Über 100 Downloads – danke für die Unterstützung!**

## Links

GitHub-Repository

https://github.com/PuLuShen/MSSQLTOOL

Neuestes Release (ZIP + VSIX)

https://github.com/PuLuShen/MSSQLTOOL/releases/latest

SSMS Gallery (für SQL Server Management Studio)

https://ssmsgallery.azurewebsites.net/extension/MSSQLTool

VSIX Gallery (für Visual Studio)

https://www.vsixgallery.com/extension/MSSQLTool

Dokumentation (Wiki)

https://github.com/PuLuShen/MSSQLTOOL/wiki

## Installation

1. Laden Sie das ZIP aus dem neuesten Release oben herunter und führen Sie `安装或重装.cmd` aus (empfohlen).
2. Oder installieren Sie das aktuelle VSIX aus der SSMS Gallery oben.
3. Updates sind integriert: **Einstellungen → Updates** kann „bei Beenden von SSMS installieren" planen.

> **MSSQL Tool ist eine kostenlose, quelloffene SSMS-Erweiterung (SQL Server Management Studio) für SSMS 22** –
> ein Add-In für SQL Server Management Studio mit kontextbezogener SQL-Vervollständigung, T-SQL-Formatierung,
> Abfrageverlauf, Überwachung der Serverintegrität und Export der Ergebnisse nach Excel/Google Sheets/E-Mail.
>
> Nutzer von SSMS 22 (SQL Server Management Studio, Version 22) suchen häufig kostenlose Plugins als Ersatz für
> SQL Prompt; MSSQL Tool ist genau dafür gedacht: eine kostenlose SSMS-Erweiterung mit integrierter SQL-Vervollständigung,
> Formatierung und Ergebnisverarbeitung – für SQL-Server-Entwickler und DBAs, die Abfragen schneller schreiben und
> Ergebnisse direkt in SQL Server Management Studio weiterverarbeiten möchten.

> MSSQL Tool ist ein Fork von [Axial-SQL/AxialSqlTools](https://github.com/Axial-SQL/AxialSqlTools) (Apache-2.0). Die SQL-Vervollständigung, die Formatierungsoptionen sowie die Einstellungs- und Lokalisierungsebene wurden darauf neu umgesetzt.

## Highlights

- **Mehrsprachige Oberfläche (8 Sprachen: English / 简体中文 / 繁體中文 / 日本語 / 한국어 / Deutsch / Français / Español)** – die meisten SSMS-Erweiterungen sind nur auf Englisch verfügbar.
- **Kostenlose Alternative zu SQL Prompt** für SQL-Vervollständigung, T-SQL-Formatierung, Abfrageverlauf und Export nach Excel/E-Mail.
- **Aufbauend auf Axial SQL Tools** (gepflegt seit 2016), mit verbesserter Vervollständigungs-Engine und Lokalisierung.

*Hinweis: Der Export nach Google Sheets und die GitHub-Skriptsynchronisierung sind experimentell und befinden sich in aktiver Entwicklung.*

## Screenshots

**Intelligente SQL-Vervollständigung** – kontextbezogene Vorschläge während der Eingabe: Schemas, Tabellen, Sichten, Spalten, Prozeduren, Funktionen und Parameter, jeweils mit Typsymbol, Kategoriefilter und Details zum markierten Objekt.

<img src="pics/1.png" width="620" alt="SQL-Vervollständigungsliste während der Eingabe von SELECT * FROM">

**Einstellungen** – jede Funktion hat eigene Optionen, die sofort gespeichert werden. Die Oberfläche folgt der Sprache von SSMS (hier Chinesisch (vereinfacht); Englisch und weitere Sprachen sind ebenfalls verfügbar).

<img src="pics/2.png" width="760" alt="Einstellungsfenster von MSSQL Tool mit dem Tab SQL-Vervollständigung">

**Extras-Menü** – die Befehle der Erweiterung, mit Tastenkürzel, sofern eines konfiguriert ist.

<img src="pics/3.png" width="520" alt="MSSQL-Tool-Befehle im Extras-Menü von SSMS">

**Ergebniststatistik** – Zellen im Ergebnisraster markieren und Anzahl, Summe, Durchschnitt, Minimum, Maximum und eindeutige Werte ablesen; dasselbe Fenster kann eine Spalte finden, einen Wert suchen und Spalten fixieren.

<img src="pics/4.png" width="820" alt="Statistikfenster von MSSQL Tool neben einem Ergebnisraster">

## Funktionen

### SQL-Bearbeitung

- **Intelligente SQL-Vervollständigung:** kontextbezogene Vorschläge für Datenbanken, Schemas, Tabellen, Sichten, Spalten, Prozeduren, Funktionen, Parameter, Synonyme und Objekte verknüpfter Server.
- **Vervollständigung folgt Ihren Aliassen:** `c.` listet nur die Spalten dieser Tabelle und fügt den reinen Spaltennamen ein, sodass der eingegebene Alias davor stehen bleibt.
- **UPDATE-Unterstützung:** `UPDATE t SET` schlägt die schreibbaren Spalten des Ziels vor, auch wenn das Ziel ein weiter unten in der `FROM`-Klausel deklarierter Alias ist.
- **Datentypen und Hinweise:** `INT`, `NVARCHAR(50)`, `DATETIME2` und die üblichen Formen `WITH (NOLOCK)` / `OPTION (RECOMPILE)` werden an der passenden Stelle angeboten.
- **Funktionshilfe im Popup:** mehr als 36 integrierte Funktionen zeigen Signatur, eine einzeilige Erklärung und ein Beispiel zum direkten Kopieren.
- **Erweiterbare Schlüsselwörter:** die eingebaute Schlüsselwortliste lässt sich unter Einstellungen → SQL-Vervollständigung erweitern (ein eigenes Schlüsselwort pro Zeile).
- **Metadaten-Cache der Vervollständigung:** Laden im Hintergrund mit Fortschrittsanzeige, manuelles Leeren/Aktualisieren und optionales Vertrauen in SQL-Server-Zertifikate.
- **F12 Gehe zu Definition:** öffnet Objektdefinitionen aus der Datenbank und springt zu lokalen SQL-Definitionen.
- **T-SQL-Formatierung** (für bestehende Gewohnheiten beibehalten; SSMS 22 formatiert selbst – siehe *Formatierung* unten)
- **Abfragevorlagen:** direkte Bearbeitung im Vorschaufenster mit Ctrl+S, Favoriten, **Tags** (`-- tags: 报表, 日结`) mit Tag-Suche (`#tag`) und eine Liste der **letzten 10** oben in der Auswahl.
- **Codeausschnitte**
- **`SELECT *` zu einer Spaltenliste erweitern**
- **Aktuelle Anweisung auswählen**
- **Blockkommentar umschalten**
- **Schnellsuche:** Objektsuche über mehrere Datenbanken, mit aus dem Objekt-Explorer übernommenem Ziel, Filtern, Skripterstellung und Öffnen in einer neuen Abfrage.
- **Objektdefinition skripten**

### Ergebnisgruppen-Werkzeuge

- **Statistikfenster (schwebend):** öffnet sich beim Klick in die Ergebnisse und schließt beim Klick außerhalb. Markieren Sie Zellen, und es zeigt **Anzahl, Summe, Durchschnitt, Minimum, Maximum und eindeutige Werte** für die Auswahl sowie den echten Spaltennamen; es ist in der Größe veränderbar, merkt sich Größe und Position und kann die **ersten N Spalten fixieren**.
- **Spaltensuche:** einen Teil eines Spaltennamens eingeben, und die Ergebnisse springen dorthin (`DDZ` trifft nie `DDZT`).
- **Fenster für Ergebnisgruppen-Werkzeuge:** dieselbe Suche plus Statistik je Spalte in einem angedockten Dialog (Extras → *Ergebnissuche und -statistik*), mit Kopieren in die Zwischenablage.

### Ergebnisverarbeitung und Datentransfer

- **Raster nach Excel exportieren**
- **Rasterergebnisse per E-Mail senden**
- **Raster als temporäre Tabelle exportieren**
- **Spaltennamen und Werte als INSERT, CSV, JSON, XML oder HTML kopieren**
- **Massendatenübertragung**

### Überwachung und Arbeitsabläufe

- **Abfrageverlauf**
- **Dashboard zur Serverintegrität**
- **Zusammenfassung der Ausführungsstatistik**
- **Warnungen zu Transaktionen und Always Encrypted**
- **Verbindungsfarben für Server- und Datenbankumgebungen**
- **Abfrageausführungszeit in Millisekunden**
- **Rechtsbündige Zahlen im Ergebnisraster**
- **Versionsinformationen zum SQL Server**
- **Konfigurierbarer Daten- und Einstellungsordner** (anderes Laufwerk, synchronisierter Ordner oder portables Gerät)

## Experimentelle Funktionen

Sie funktionieren, ihr Design ist aber noch in Bewegung: Änderungen zwischen Releases sind zu erwarten, prüfen Sie die zugehörigen Einstellungen nach dem Upgrade erneut.

| Funktion | Status |
| --- | --- |
| **Export nach Google Sheets** | Beta – OAuth-Einrichtung und Spaltenzuordnung ändern sich noch. |
| **GitHub-Skriptsynchronisierung** | Beta – Push und Pull ganzer Dateien, Konfliktlösung von Hand. |

## Formatierung

SSMS 22 bringt einen eigenen T-SQL-Formatierer mit, daher wird **die Formatierungsfunktion dieser Erweiterung nicht weiterentwickelt**. Der Befehl funktioniert weiterhin und bleibt für bestehende Gewohnheiten erhalten – er liegt jetzt im **Extras**-Menü (Ctrl+Shift+Alt+F) –, neue Formatierungsoptionen wird es jedoch nicht geben.

## Unterstützung

<img src="pics/赞赏码.png" width="320" alt="QR-Code für eine Spende" />

## FAQ

**Was ist MSSQL Tool?**
Eine kostenlose SSMS-22-Erweiterung für SQL-Vervollständigung, Formatierung und Ergebnisverarbeitung.

**Ist MSSQL Tool kostenlos?**
Ja, es ist Open Source unter Apache-2.0.

**Wie schlägt es sich gegen SQL Prompt oder dbForge?**
MSSQL Tool ist kostenlos und quelloffen, mit integrierter GitHub-Synchronisierung und Export nach Google Sheets. SQL Prompt und dbForge sind kommerzielle Alternativen.
