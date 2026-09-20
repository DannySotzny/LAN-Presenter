# Hinweise für Mitwirkende

## Architektur und Grenzen

- `src/BeamerPresenter.Domain` darf keine Projektabhängigkeiten besitzen.
- `Application` referenziert nur `Domain`; UI und Infrastruktur bleiben außerhalb der Businesslogik.
- `App` ist der einzige Composition Root. Die WinForms-Anwendung und Kestrel laufen bewusst in einem Prozess.
- Nutzerdaten gehören unter `%LOCALAPPDATA%\HouseOfLAN\Presenter`, niemals in das Installationsverzeichnis oder Repository.

## Sicherheit

- Web-Passwörter niemals protokollieren, testen, committen oder in `appsettings` ablegen.
- Das Passwort wird mit PBKDF2-SHA512 und individuellem Salt gespeichert. Änderungen daran brauchen einen sicheren Migrationspfad.
- Uploads bleiben authentifizierungspflichtig; Dateinamen immer mit `Path.GetFileName` normalisieren und die erlaubten Endungen zentral prüfen.
- `/presenter` bleibt absichtlich anonym erreichbar, damit der lokale Kiosk ohne Anmeldung funktioniert. Keine Management-Endpunkte dort hinzufügen.
- Razor Components benötigen `UseAntiforgery()` nach `UseAuthentication()` und `UseAuthorization()`; ohne diese Middleware antwortet selbst `/login` mit HTTP 500.
- Der WinForms-Host erstellt kein zusammengeführtes Static-Asset-Manifest. RCL- und MudBlazor-Assets werden daher per MSBuild als `wwwroot/_content/...` in den App-Output kopiert; `UseStaticFiles()` liefert sie aus.
- Der Kestrel-Web-Root wird explizit auf `AppContext.BaseDirectory/wwwroot` gesetzt, da das Arbeitsverzeichnis beim Start aus Visual Studio oder per Installer abweichen kann.
- `Directory.Build.targets` schreibt `BuildTimestampUtc` und `GitCommitSha` als Assembly-Metadaten. Diese Werte werden im Statusfenster aus dem tatsächlichen App-Assembly gelesen und dürfen dort nicht hart codiert werden.
- Single-Instance verwendet einen benutzerspezifischen Named Mutex und eine benutzerspezifische Named Pipe. Der zweite Prozess darf keinen Host starten, sondern fordert ausschließlich das Öffnen des bestehenden Statusfensters an.
- Tray-Befehle ändern den Zustand ausschließlich über den zentralen `PlaybackController`; Chrome-, Power- und Presenter-Seiteneffekte werden dort in späteren Phasen orchestriert und gehören nicht direkt in das Form.
- Autostart wird unter `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registriert. Der Wert enthält immer den vollständig quotierten Executable-Pfad plus `--autostart`; dieser Startmodus öffnet nicht automatisch das Statusfenster.
- Serilog schreibt strukturierte Tageslogs nach `%LOCALAPPDATA%\HouseOfLAN\Presenter\Logs` und bewahrt höchstens 14 Dateien auf. Passwörter, Cookies, Tokens und Request-Bodies dürfen nie geloggt werden.
- SQLite kann `DateTimeOffset` nicht serverseitig in `ORDER BY` übersetzen. Die kleine Videoliste wird deshalb zuerst geladen und anschließend im Speicher nach `AddedAtUtc` sortiert; Änderungen daran müssen den authentifizierten Web-Routen-Test bestehen.
- Datenbankschemata werden ausschließlich über EF-Core-Migrationen weiterentwickelt. `PresenterDatabase` baselinet einmalig ältere `EnsureCreated`-Datenbanken auf `InitialSchema`; diese Kompatibilität muss durch einen echten SQLite-Test erhalten bleiben.
- Vor ausstehenden Schema-Migrationen wird die SQLite-Datenbank per SQLite-Backup-API nach `Backup/` kopiert; maximal sieben Migrationsbackups bleiben erhalten.
- Medienordner sind eigene persistente Entitäten mit `NOCASE`-eindeutigem Vollpfad. Uploads verwenden den ersten aktivierten Ordner und dürfen nicht auf das Legacy-Feld `PresenterSettings.MediaFolder` zurückfallen.
- `IMediaScanner` ist die verlässliche Quelle für den Dateibestand: Startscan plus 30-Minuten-Reconciliation. Der Scanner ignoriert unzugängliche Pfade und Reparse Points, erfasst nur `MediaFileSupport`-Endungen und markiert verschwundene Dateien, statt Datensätze zu löschen.
- FFprobe-Kandidaten werden in der Reihenfolge konfigurierter Pfad, lokales `Tools`-Verzeichnis, `PATH`, bekannte Installationsorte geprüft und gelten ausschließlich nach erfolgreichem `ffprobe -version` als verfügbar. Automatische Installation läuft nur über die exakte WinGet-ID `Gyan.FFmpeg`.
- FFprobe liefert JSON über den zentralen `IFfprobeService`. Analysezustand (`ProbeStatus`) und erwartete Browser-Kompatibilität (`PlaybackStatus`) sind getrennte persistente Werte; ein fehlendes FFprobe darf daher nicht als inkompatibles Medium gespeichert werden.
- `MediaProbeQueue` ist ein deduplizierter `Channel` mit genau zwei Consumern. Reconciliation und Uploads reihen ausschließlich IDs plus Pfad ein; erst nach zwei Sekunden stabiler Dateigröße und Schreibzeit darf FFprobe starten. Ergebnisse dürfen nur gespeichert werden, wenn die Datei während der Analyse unverändert blieb.

## Arbeitsweise

- Vor jedem Commit: `dotnet build BeamerPresenterForLanParties.slnx -c Release` und passende Tests ausführen.
- Der Test `AuthenticatedWebRouteTests.Valid_login_renders_management_page` prüft mit temporärer Datenbank den vollständigen Ablauf aus gültigem Login, Auth-Cookie und Rendering der Managementseite.
- Kleine, abgeschlossene Conventional-Commit-Schritte verwenden. Nach fertigen, releasbaren Features `dotnet versionize --proj-name beamerpresenter.app` ausführen.
- `versionize` verwaltet Changelog, Release-Commit und Tag. Changelog-Dateien nicht manuell editieren.
- `v0.x`-Tags markieren nur Entwicklungsstände und dürfen keinen GitHub-Release erzeugen. Der Release-Workflow akzeptiert erst stabile Tags ab `v1.0.0`.
- Bei Änderungen an Release-Dateien die erwarteten Namen `HouseOfLAN-Presenter-<Version>-Setup.exe` und `HouseOfLAN-Presenter-<Version>-win-x64-portable.zip` erhalten.
