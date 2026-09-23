# Beamer Presenter for LAN-Parties

[![CI](https://github.com/sotzny/LAN-Presenter/actions/workflows/ci.yml/badge.svg)](https://github.com/sotzny/LAN-Presenter/actions/workflows/ci.yml)
[![Windows](https://img.shields.io/badge/platform-Windows%20x64-0078D4)](https://github.com/sotzny/LAN-Presenter)

**Videos für den Beamer automatisch abspielen und die Wiedergabe bequem vom Browser aus steuern.**

Beamer Presenter ist eine Windows-Desktopanwendung für LAN-Parties und andere Veranstaltungen. Sie verwaltet eine lokale Videobibliothek, stellt daraus eine abwechslungsreiche Wiedergabe-Queue zusammen und zeigt die Clips im Chrome-Kioskmodus auf dem ausgewählten Beamer. Ein passwortgeschütztes Webinterface bietet die Steuerung und Verwaltung – am Rechner selbst oder auf einem Gerät im freigegebenen LAN.

## Warum Beamer Presenter?

Bei einer Veranstaltung soll der Beamer Inhalte zeigen, ohne dass jemand fortlaufend Videos auswählen und weiterschalten muss. Beamer Presenter übernimmt diese Routine: Es findet Videos in den konfigurierten Ordnern, prüft ihre Abspielbarkeit und plant Clips automatisch ein. Lange Videos können in wechselnden Ausschnitten laufen; Verlauf und Segmentplanung helfen dabei, nicht immer dieselben Stellen zu zeigen.

Die Wiedergabe lässt sich jederzeit pausieren, ausblenden, fortsetzen oder beenden. Für kurzfristige Ansagen können News als Lauftext, geteilter Bildschirm oder Vollbild eingeblendet werden.

## Funktionen

- **Automatische Video-Queue:** lokale Videos werden erkannt, analysiert und nach konfigurierbaren Regeln für die Wiedergabe eingeplant.
- **Kontrolle über den Browser:** Queue, Verlauf, Bibliothek, News und Presenter-Zustand sind in getrennten Verwaltungsbereichen erreichbar.
- **Gezielte Wiedergabe:** Clips manuell sofort starten, als Nächstes einreihen, verschieben oder aus der Queue entfernen.
- **YouTube-Import:** gültige YouTube-Links werden lokal geladen und nach der Medienanalyse in die Queue übernommen. Ganze Videos oder ausgewählte Segmente sind möglich.
- **News-Einblendungen:** Meldungen als Ticker, 50:50-Split-Screen oder Vollbild erstellen, planen, priorisieren und anzeigen.
- **Beamer-Steuerung:** Chrome startet im Kioskmodus auf dem gewählten Monitor. Aktivieren, Pause, Ausblenden und Stop lassen sich zentral steuern.
- **Übersicht und Wiederherstellung:** Dashboard mit Live-Status, Medienanalyse und Scannerzustand; automatische Erholung bei Browser- oder Wiedergabeproblemen.
- **Lokale Datenhaltung:** Einstellungen, Bibliothek, Queue, Verlauf, Logs und Backups liegen im Benutzerprofil unter `%LOCALAPPDATA%\HouseOfLAN\Presenter`.

Unterstützte lokale Videoformate: `.mp4`, `.m4v`, `.mkv`, `.webm`, `.avi` und `.mov`.

## Voraussetzungen

- Windows x64
- Google Chrome
- Für automatische Medienanalyse: FFprobe (FFmpeg). Die Anwendung kann eine vorhandene Installation finden; alternativ lässt sich der Pfad angeben oder FFmpeg über WinGet installieren.
- Für den YouTube-Import wird `yt-dlp` benötigt. Die Anwendung verwaltet das benötigte Werkzeug und speichert geladene Videos im ersten aktiven Medienordner.

## Installation und erster Start

Die stabilen Releases veröffentlichen einen Installer und eine portable Ausgabe. Öffne [Releases](https://github.com/sotzny/LAN-Presenter/releases), lade eine der Dateien herunter und starte die Anwendung:

- `HouseOfLAN-Presenter-<Version>-Setup.exe` – Installation ohne Administratorrechte
- `HouseOfLAN-Presenter-<Version>-win-x64-portable.zip` – portable Ausgabe zum Entpacken und Starten

Der Release-Workflow veröffentlicht stabile Versionen ab `v1.0.0`. Entwicklungs-Tags der Reihe `v0.x` erzeugen keinen GitHub-Release.

Beim ersten Start:

1. Einen oder mehrere Medienordner festlegen.
2. Ein Web-Passwort setzen.
3. Den Zielmonitor auswählen und bei Bedarf Chrome- oder FFprobe-Pfade konfigurieren.
4. Die Weboberfläche auf dem lokalen Rechner unter [http://localhost:8765](http://localhost:8765) öffnen und anmelden.

Aktiviere „Web UI im LAN freigeben“ in der Desktop-Anwendung, wenn du von einem anderen Gerät im Netzwerk steuern möchtest. Die Änderung wird nach einem Neustart wirksam; gegebenenfalls muss der Web-Port in der Windows-Firewall freigegeben werden.

## Sicherheit und Netzwerk

Die Weboberfläche bindet standardmäßig nur an `127.0.0.1`. Die LAN-Freigabe muss bewusst in der Desktop-Anwendung aktiviert werden. Verwaltungsseiten und Steuerbefehle erfordern ein Passwort und ein Anmelde-Cookie. Das Passwort wird mit PBKDF2-SHA512 und individuellem Salt gespeichert.

Die Kioskseite des Beamers, der Presenter-Hub und die Medienauslieferung bleiben auf lokale Verbindungen beschränkt. Sie sind nicht für LAN-Teilnehmer freigegeben. Logs enthalten keine Passwörter, Cookies oder Request-Bodies.

## Entwicklung

Benötigt werden das .NET 10 SDK und unter Windows eine passende Entwicklungsumgebung für WinForms.

```powershell
git clone https://github.com/sotzny/LAN-Presenter.git
cd LAN-Presenter
dotnet tool restore
dotnet restore BeamerPresenterForLanParties.slnx --locked-mode
dotnet build BeamerPresenterForLanParties.slnx -c Release
dotnet run --project src/BeamerPresenter.App
```

Die Weboberfläche ist anschließend unter `http://localhost:8765` erreichbar. Einstellungen und Daten liegen im lokalen Benutzerprofil, nicht im Repository.

## Tests und Qualitätssicherung

Die CI baut die Solution, prüft das Format und führt die Tests inklusive Browser-Tests mit Chromium aus. Die Coverage-Grenze von 80 Prozent wird für Produktcode ermittelt: Coverlet misst C# und die Chromium-Browser-Tests schreiben Line Coverage für die ausgelieferten JavaScript-Dateien als LCOV. EF-Migrationen, generierte Dateien, der Composition Root, das WinForms-Formular und Razor-Markup sind von der zeilenbasierten Coverage ausgenommen; ihr Verhalten wird durch Migration-, Architektur-, Browser- und Route-Tests geprüft. SonarQube verwendet dieselben C#-Coverage-Ausnahmen und importiert zusätzlich den Browser-LCOV-Bericht.

```powershell
# Browser-Tests benötigen Chromium; zuerst die Solution bauen und dann den Browser installieren.
dotnet build BeamerPresenterForLanParties.slnx -c Release
pwsh tests/BeamerPresenter.Browser.Tests/bin/Release/net10.0/playwright.ps1 install chromium

# Gesamte Testsuite
dotnet test BeamerPresenterForLanParties.slnx -c Release
```

Nach Änderungen an Paketversionen die Lockfiles aktualisieren:

```powershell
dotnet restore BeamerPresenterForLanParties.slnx --force-evaluate
```

## Architektur

Die Anwendung besteht aus einer WinForms-Desktop-App und einer im selben Prozess gestarteten ASP.NET-Core-Weboberfläche. Die Desktop-App ist der Composition Root. Die Fachlogik ist in Domain und Application getrennt; Infrastructure stellt unter anderem SQLite-Persistenz und Medienanalyse bereit, Web enthält Oberfläche und HTTP-Endpunkte.

```text
src/
├── BeamerPresenter.Domain         Modelle
├── BeamerPresenter.Application    Fachlogik und Contracts
├── BeamerPresenter.Infrastructure SQLite, Scanner und Medienwerkzeuge
├── BeamerPresenter.Web            Management-UI, Presenter und Endpunkte
└── BeamerPresenter.App            WinForms-Host und Composition Root
```

## Versionierung und Beiträge

Das Projekt verwendet Conventional Commits. Versionen und Changelog werden mit dem festgeschriebenen `versionize`-Tool verwaltet:

```powershell
dotnet tool restore
dotnet versionize --workingDir src/BeamerPresenter.App --configDir ../..
```

Bitte vor einem Beitrag die Architektur- und Sicherheitsregeln in [AGENTS.md](AGENTS.md) beachten. Änderungen an Datenbankschemata erfolgen über EF-Core-Migrationen.

## Screenshots

Screenshots der Verwaltungsoberfläche sind in dieser Repository-Version noch nicht als Dateien abgelegt. Die Seiten sind passwortgeschützt; für aussagekräftige Projektbilder sollten Dashboard, Queue und Mediathek mit bereinigten Beispieldaten aufgenommen und anschließend hier ergänzt werden.
