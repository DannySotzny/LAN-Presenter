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

## Arbeitsweise

- Vor jedem Commit: `dotnet build BeamerPresenterForLanParties.slnx -c Release` und passende Tests ausführen.
- Kleine, abgeschlossene Conventional-Commit-Schritte verwenden. Nach fertigen, releasbaren Features `dotnet versionize --proj-name beamerpresenter.app` ausführen.
- `versionize` verwaltet Changelog, Release-Commit und Tag. Changelog-Dateien nicht manuell editieren.
- Bei Änderungen an Release-Dateien die erwarteten Namen `HouseOfLAN-Presenter-<Version>-Setup.exe` und `HouseOfLAN-Presenter-<Version>-win-x64-portable.zip` erhalten.
