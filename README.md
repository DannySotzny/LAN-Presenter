# Beamer Presenter for LAN-Parties

Lokale Windows-Anwendung zur Steuerung von Videos auf einem Beamer bei LAN-Parties. Die Anwendung kombiniert eine WinForms-Tray-App mit einer im selben Prozess gestarteten ASP.NET-Core-Weboberfläche.

## Aktueller Stand

Die Fundament-Stufe ist implementiert:

- WinForms-Status-/Einstellungsfenster mit vollständigem Tray-Menü für Aktivieren, Pausieren, Ausblenden und Stoppen sowie Liveanzeige von Presenter-, Chrome- und lokalem/LAN-Webstatus
- Statusanzeige mit tatsächlicher Version, Buildzeit, Git-Commit und .NET-Runtime aus dem Buildartefakt
- Single-Instance pro Windows-Benutzer: ein zweiter Start aktiviert über eine Named Pipe das Fenster der laufenden Instanz
- optionaler Autostart ohne Administratorrechte über den benutzerbezogenen Windows-Run-Schlüssel
- Windows-Monitorerkennung mit Friendly Name, DeviceName, Auflösung und Position; ein verschwundener Zielmonitor blockiert die Aktivierung bis zur bewussten Fallback-Auswahl
- strukturiertes JSONL-Logging mit täglicher Rotation und 14 Tagen Aufbewahrung unter `%LOCALAPPDATA%\HouseOfLAN\Presenter\Logs`
- Kestrel-Weboberfläche auf Port 8765, inklusive Health-Endpunkt und MudBlazor-Management-UI
- Passwortschutz per Cookie-Login; das Passwort wird ausschließlich in der Desktop-App gesetzt
- standardmäßig ausschließlich an `127.0.0.1` gebundene Web UI; LAN-Bindung wird bewusst in der Desktop-App aktiviert und bleibt passwortgeschützt
- PBKDF2-SHA512-Hash mit zufälligem Salt, kein Klartextpasswort
- Upload unterstützter Videoformate über die geschützte Web UI
- responsive MudBlazor-Medienbibliothek mit Suche, technischen Metadaten, Kennzahlen und klar erkennbaren Analyse-/Kompatibilitätsfehlern
- persistentes Aktivieren/Deaktivieren einzelner Videos und erneute FFprobe-Analyse direkt aus der geschützten Medienbibliothek; deaktivierte oder zur Laufzeit fehlgeschlagene Medien werden nicht erneut eingeplant
- anonyme, ID-basierte lokale Medienauslieferung mit HTTP-Range-Support und erneuter Pfadvalidierung gegen aktive Medienordner
- zusätzliche Loopback-Sperre für `/presenter`, `/media` und `/hubs/presenter`, sodass LAN-Teilnehmer weder Kiosk noch Mediendateien direkt abrufen können
- dauerhaft geladene Fullscreen-Presenter-Seite mit dediziertem SignalR-Hub, Reconnect, lokalen Video-/Segmentkommandos und Status-/Heartbeat-Rückmeldungen
- mehrere persistente Videoordner mit optional rekursiver Erfassung; der bisherige Einzelpfad wird automatisch migriert
- Full Scan beim Start und Reconciliation alle 30 Minuten für neue, geänderte und fehlende lokale Videos
- dynamische FileSystemWatcher für alle aktiven Medienordner; Ereignisse werden debounct und anschließend über denselben vollständigen Abgleich verarbeitet
- automatische FFprobe-Erkennung mit echtem `-version`-Prozesscheck, manueller Pfadwahl und optionaler WinGet-Installation
- FFprobe-Metadatenanalyse für Dauer, Container, Video-/Audio-Codec, Auflösung, Framerate und Audiokanäle mit getrenntem Analyse- und Browser-Wiedergabestatus
- automatische, deduplizierte Analyse-Queue mit höchstens zwei parallelen FFprobe-Prozessen und Stabilitätsprüfung vor der Analyse großer Kopiervorgänge
- lokale Auslieferung der MudBlazor-Assets für Debug, portable Ausgabe und Inno-Setup-Installation
- SQLite-Persistenz unter `%LOCALAPPDATA%\HouseOfLAN\Presenter\Data\presenter.db`
- versionierte EF-Core-Migrationen mit verlustfreier Übernahme vorhandener `EnsureCreated`-Datenbanken
- konsistente tägliche SQLite-Sicherung über die SQLite-Backup-API mit atomarer Ablage und Aufbewahrung der letzten sieben Tage
- getrennte lokale Verzeichnisse für Daten, Logs, Backups, Chrome-Profil und Tools
- persistente Presenter-Optionen für Chrome-Pfad, Zielmonitor, Always-On-Top und Display-/System-Standby-Schutz
- kontrollierter Chrome-Kiosk-Kindprozess mit separatem Profil, Autoplay-Policy, Zielmonitorpositionierung und Show/Hide/Stop-Steuerung
- zentral serialisierte Presenter-Zustände, die Chrome, SignalR-Wiedergabe und Windows-Power-Requests gemeinsam aktivieren, pausieren, ausblenden und stoppen
- deterministische Segmentplanung mit vollständiger Wiedergabe kurzer Videos, zufälligen 7- bis 10-Minuten-Ausschnitten langer Videos, Cooldowns und Ausschluss bereits tatsächlich gespielter Bereiche
- persistente Queue- und Wiedergabehistorie mit konfigurierbaren Schwellenwerten, Segmentlängen, Cooldowns und Zielgröße
- automatische Queue-Auffüllung im Hintergrund sowie priorisierte Aktionen für „Als Nächstes“ und „Sofort abspielen“, ohne manuelle Einträge zu überschreiben
- geschützte MudBlazor-Queue-Verwaltung mit direktem „Als Nächstes“/„Sofort“, manueller Segmentwahl, Verschieben/Entfernen wartender Einträge und gezielter Neugenerierung der automatischen Einträge
- sichtbare Wiedergabehistorie für lokale und YouTube-Segmente mit Status, tatsächlichem Zeitraum und bewusstem Zurücksetzen
- Presenter-Ende und Wiedergabefehler schalten automatisch zum nächsten Eintrag weiter
- sichere Normalisierung von YouTube-Watch-, Kurz- und Shorts-Links auf stabile `youtube:<video-id>`-Quellschlüssel
- geschützter YouTube-Metadatencheck über die offizielle IFrame Player API mit sichtbarer Dauer, Vorschau und Auswahl zwischen vollständiger, begrenzter und eigener Segmentwiedergabe
- persistente YouTube-Queue- und Verlaufseinträge mit begrenzter Wiedergabezeit und fortlaufenden, nicht ständig am Anfang beginnenden Segmenten
- YouTube-Wiedergabe über die offizielle IFrame Player API mit Dauer-/Positionsmeldungen, Fünf-Sekunden-Timeout und automatischem Fallback zum nächsten Queue-Eintrag
- persistente, validierte News-Einträge für Ticker, 50:50-Split-Screen und Fullscreen mit Dauer/Permanent, Gültigkeitsfenster und Priorität
- Presenter-News mit animiertem Ticker, 50:50-Split und Fullscreen-Priorität; Fullscreen pausiert das Video und stellt danach Wiedergabe und verdrängte Overlay-News wieder her
- geschützte MudBlazor-Newsverwaltung für Erstellen, Planen, sofortiges Anzeigen, Beenden und Löschen sowie ein Fünf-Sekunden-Scheduler für Gültigkeitsfenster
- Presenter-Watchdog für Chrome-, SignalR- und Heartbeat-Ausfälle im aktiven und pausierten Zustand, echte Topmost-Prüfung sowie einmaliges Reload und anschließendes Überspringen dauerhaft festhängender aktiver Wiedergaben
- live aktualisiertes Management-Dashboard mit Presenter-/Browserstatus, aktuellem Titel und Position, FFprobe-/Scannerzustand sowie direkten Pause-, Resume-, Hide- und Stop-Befehlen
- anonymer datensparsamer `/health`-Endpunkt und authentifizierte Detailzustände unter `/health/details` beziehungsweise `/api/status`
- GitHub Actions für Build/Test und Release-Artefakte auf Git-Tags

Noch offen ist die abschließende Qualitäts- und Release-Härtung.

## Lokaler Start

```powershell
dotnet build BeamerPresenterForLanParties.slnx -c Release
dotnet run --project src/BeamerPresenter.App
```

Beim ersten Start in der Desktop-App einen Videoordner und ein Web-Passwort festlegen. Dann ist die Web UI unter `http://localhost:8765` erreichbar. Für LAN-Zugriff zusätzlich „Web UI im LAN freigeben“ aktivieren, die Anwendung neu starten und den gewählten Port in der Windows-Firewall erlauben; dabei ein starkes Passwort verwenden. Die Kiosk- und Medienrouten bleiben unabhängig davon auf lokale Zugriffe beschränkt.

Der authentifizierte Ablauf ist durch einen Integrationstest mit temporärer SQLite-Datenbank abgesichert. Er prüft den gültigen Login, das Auth-Cookie und das anschließende Rendering der Managementseite:

```powershell
dotnet test tests/BeamerPresenter.Web.Tests -c Release
```

Die CI sammelt Coverage über alle Testprojekte, führt Mehrfachmessungen derselben Produktionszeile zusammen und bricht unter 80 Prozent Line Coverage ab. Generierte Migrationen sowie rein visuelle WinForms-/Razor- und Composition-Root-Dateien sind von dieser Metrik ausgenommen:

```powershell
$results = Join-Path $env:TEMP "beamer-presenter-coverage"
dotnet test BeamerPresenterForLanParties.slnx -c Release --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory $results
./scripts/Assert-Coverage.ps1 -ResultsDirectory $results -Threshold 80
```

Paketversionen werden zentral in `Directory.Packages.props` gepflegt. Jedes Projekt besitzt ein eingechecktes `packages.lock.json`; CI und Release stellen ausschließlich im Locked Mode wieder her. Nach einer bewussten Paketänderung werden die Lockfiles lokal mit `dotnet restore BeamerPresenterForLanParties.slnx --force-evaluate` aktualisiert.

## Versionen und Changelog

`versionize` ist als lokales .NET-Tool in `dotnet-tools.json` festgeschrieben. Commit-Nachrichten nutzen Conventional Commits, beispielsweise `feat: add media scan` oder `fix: reject unsafe upload names`.

```powershell
dotnet tool restore
dotnet versionize --workingDir src/BeamerPresenter.App --configDir ../..
```

Der zweite Befehl versioniert die ausführbare App, wertet dabei aber die Conventional Commits des gesamten Repositorys aus. Er erzeugt/aktualisiert den Changelog, erstellt den Release-Commit und den Git-Tag `v<Version>`. Tags der Entwicklungsreihe `v0.x` bleiben reine Git-Versionen; der GitHub-Release-Workflow veröffentlicht erst stabile Versionen ab `v1.0.0`.

## Release-Artefakte

Ein stabiles Tag ab `v1.0.0`, beispielsweise `v1.2.0`, veröffentlicht zwei Downloads:

- `HouseOfLAN-Presenter-1.2.0-Setup.exe` – Inno-Setup-Installer
- `HouseOfLAN-Presenter-1.2.0-win-x64-portable.zip` – selbstenthaltende portable Variante

Zum lokalen Bauen des Installers wird [Inno Setup](https://jrsoftware.org/isinfo.php) benötigt. Das Skript liegt in `installer/BeamerPresenter.iss`.

## Architektur

`Domain` enthält ausschließlich Modelle. `Application` enthält Contracts und Playback-Regeln. `Infrastructure` implementiert die SQLite-Persistenz. `Web` stellt die geschützte UI und HTTP-Endpunkte bereit. `App` ist der WinForms-Composition-Root und hostet Kestrel im gleichen Prozess.
