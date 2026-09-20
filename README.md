# Beamer Presenter for LAN-Parties

Lokale Windows-Anwendung zur Steuerung von Videos auf einem Beamer bei LAN-Parties. Die Anwendung kombiniert eine WinForms-Tray-App mit einer im selben Prozess gestarteten ASP.NET-Core-Weboberfläche.

## Aktueller Stand

Die Fundament-Stufe ist implementiert:

- WinForms-Status-/Einstellungsfenster mit Tray-Icon
- Kestrel-Weboberfläche auf Port 8765, inklusive Health-Endpunkt und MudBlazor-Management-UI
- Passwortschutz per Cookie-Login; das Passwort wird ausschließlich in der Desktop-App gesetzt
- PBKDF2-SHA512-Hash mit zufälligem Salt, kein Klartextpasswort
- Upload unterstützter Videoformate über die geschützte Web UI
- lokale Auslieferung der MudBlazor-Assets für Debug, portable Ausgabe und Inno-Setup-Installation
- SQLite-Persistenz unter `%LOCALAPPDATA%\HouseOfLAN\Presenter\Data\presenter.db`
- GitHub Actions für Build/Test und Release-Artefakte auf Git-Tags

Noch nicht enthalten sind Chrome-Kiosk-Steuerung, FFprobe-Analyse, Playlist-/Segmentlogik, YouTube, SignalR und die Beamer-/Monitorsteuerung. Diese werden in den folgenden Phasen hinter den vorhandenen Application-Contracts ergänzt.

## Lokaler Start

```powershell
dotnet build BeamerPresenterForLanParties.slnx -c Release
dotnet run --project src/BeamerPresenter.App
```

Beim ersten Start in der Desktop-App einen Videoordner und ein Web-Passwort festlegen. Dann ist die Web UI unter `http://localhost:8765` erreichbar. Für LAN-Zugriff muss die Windows-Firewall den gewählten Port erlauben; in Produktion sollte ein starkes Passwort verwendet werden.

Der authentifizierte Ablauf ist durch einen Integrationstest mit temporärer SQLite-Datenbank abgesichert. Er prüft den gültigen Login, das Auth-Cookie und das anschließende Rendering der Managementseite:

```powershell
dotnet test tests/BeamerPresenter.Web.Tests -c Release
```

## Versionen und Changelog

`versionize` ist als lokales .NET-Tool in `dotnet-tools.json` festgeschrieben. Commit-Nachrichten nutzen Conventional Commits, beispielsweise `feat: add media scan` oder `fix: reject unsafe upload names`.

```powershell
dotnet tool restore
dotnet versionize --proj-name beamerpresenter.app
```

Der zweite Befehl erzeugt/aktualisiert den Changelog, erstellt den Release-Commit und den Git-Tag `v<Version>`. Das Tag startet den GitHub-Release-Workflow.

## Release-Artefakte

Ein Tag `v1.2.0` veröffentlicht zwei Downloads:

- `HouseOfLAN-Presenter-1.2.0-Setup.exe` – Inno-Setup-Installer
- `HouseOfLAN-Presenter-1.2.0-win-x64-portable.zip` – selbstenthaltende portable Variante

Zum lokalen Bauen des Installers wird [Inno Setup](https://jrsoftware.org/isinfo.php) benötigt. Das Skript liegt in `installer/BeamerPresenter.iss`.

## Architektur

`Domain` enthält ausschließlich Modelle. `Application` enthält Contracts und Playback-Regeln. `Infrastructure` implementiert die SQLite-Persistenz. `Web` stellt die geschützte UI und HTTP-Endpunkte bereit. `App` ist der WinForms-Composition-Root und hostet Kestrel im gleichen Prozess.
