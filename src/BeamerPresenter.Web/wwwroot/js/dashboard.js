(() => {
    const elements = {
        presenter: document.getElementById("dashboard-presenter-state"),
        browser: document.getElementById("dashboard-browser-state"),
        title: document.getElementById("dashboard-current-title"),
        position: document.getElementById("dashboard-position"),
        ffprobe: document.getElementById("dashboard-ffprobe"),
        scanner: document.getElementById("dashboard-scanner")
    };
    if (Object.values(elements).some(element => !element)) return;

    const formatClock = value => {
        if (!value) return "--:--:--";
        const text = String(value);
        const match = /\d{2}:\d{2}:\d{2}/.exec(text);
        return match ? match[0] : text;
    };

    const refresh = async () => {
        try {
            const response = await fetch("/api/status", {
                credentials: "same-origin",
                headers: { "Accept": "application/json" },
                cache: "no-store"
            });
            if (!response.ok) return;
            const status = await response.json();
            elements.presenter.textContent = status.presenterState;
            elements.browser.textContent = `Browser: ${status.browserConnected ? "Verbunden" : "Getrennt"}`;
            elements.title.textContent = status.currentTitle || "–";
            elements.position.textContent = `${formatClock(status.position)} / ${formatClock(status.duration)}`;
            elements.ffprobe.textContent = status.ffprobeAvailable ? "OK" : "Nicht verfügbar";
            if (status.mediaScannerRunning) elements.scanner.textContent = "Läuft";
            else if (status.mediaScannerError) elements.scanner.textContent = "Fehler";
            else elements.scanner.textContent = "Bereit";
        } catch {
            elements.browser.textContent = "Browser: Status nicht erreichbar";
        }
    };

    refresh();
    window.setInterval(refresh, 2000);
})();
