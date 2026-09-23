(() => {
    "use strict";

    const form = document.getElementById("youtube-management-form");
    const urlInput = document.getElementById("youtube-url");
    const loadButton = document.getElementById("youtube-load-metadata");
    const status = document.getElementById("youtube-metadata-status");
    const durationLabel = document.getElementById("youtube-duration");
    const preview = document.getElementById("youtube-preview");
    const fullMode = document.getElementById("youtube-mode-full");
    const startRow = document.getElementById("youtube-custom-start");
    const durationRow = document.getElementById("youtube-custom-duration");
    const maximumRow = document.getElementById("youtube-maximum-duration");
    const startInput = document.getElementById("youtube-start");
    const durationInput = document.getElementById("youtube-custom-duration-input");
    const maximumInput = document.getElementById("youtube-maximum-duration-input");
    if (!form || !urlInput || !loadButton || !status || !durationLabel || !preview || !fullMode ||
        !startRow || !durationRow || !maximumRow || !startInput || !durationInput || !maximumInput) return;

    const metadataTimeoutMs = 5000;
    let youtubeApiPromise = null;
    let player = null;
    let durationSeconds = null;
    let playerError = null;
    let metadataGeneration = 0;
    let downloadVideoId = null;
    let downloadPoll = null;
    let pendingAction = null;
    const actionButtons = form.querySelectorAll(".youtube-actions button");

    const playerErrorMessage = code => {
        switch (code) {
            case 100: return "Dieses YouTube-Video ist nicht verfügbar oder privat.";
            case 101:
            case 150: return "Der Videoanbieter erlaubt keine Wiedergabe außerhalb von YouTube.";
            case 153: return "YouTube konnte die Einbettung nicht zuordnen (Fehler 153).";
            default: return `YouTube Player Fehler ${code}: Die Wiedergabe ist nicht verfügbar.`;
        }
    };

    const showPlayerError = code => {
        playerError = playerErrorMessage(code);
        fullMode.disabled = true;
        status.textContent = playerError;
        status.className = "error";
        actionButtons.forEach(button => { button.disabled = code !== 101 && code !== 150; });
    };

    const formatDuration = value => {
        const totalSeconds = Math.max(0, Math.floor(value));
        const hours = Math.floor(totalSeconds / 3600);
        const minutes = Math.floor((totalSeconds % 3600) / 60);
        const seconds = totalSeconds % 60;
        return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
    };

    const selectedMode = () => form.querySelector('input[name="playbackMode"]:checked')?.value || "automatic";
    const updateMode = () => {
        const mode = selectedMode();
        const custom = mode === "custom";
        const automatic = mode === "automatic";
        startRow.hidden = !custom;
        durationRow.hidden = !custom;
        maximumRow.hidden = !automatic;
        startInput.required = custom;
        durationInput.required = custom;
        maximumInput.required = automatic;
    };

    const destroyPlayer = () => {
        if (player?.destroy) player.destroy();
        player = null;
        preview.replaceChildren();
        preview.hidden = true;
    };

    const resetMetadata = () => {
        metadataGeneration += 1;
        if (downloadPoll) window.clearInterval(downloadPoll);
        downloadPoll = null;
        downloadVideoId = null;
        pendingAction = null;
        destroyPlayer();
        durationSeconds = null;
        playerError = null;
        loadButton.disabled = false;
        fullMode.disabled = true;
        actionButtons.forEach(button => { button.disabled = false; });
        if (selectedMode() === "full") {
            form.querySelector('input[name="playbackMode"][value="automatic"]').checked = true;
        }
        status.textContent = "Noch nicht geprüft";
        status.className = "";
        durationLabel.textContent = "Dauer: –";
        updateMode();
    };

    const phaseText = {
        installing: "yt-dlp wird eingerichtet …",
        downloading: "Video wird lokal geladen …",
        analyzing: "Video wird analysiert …",
        ready: "Video ist in der Mediathek bereit.",
        failed: "Download fehlgeschlagen."
    };

    const showDownload = snapshot => {
        const phase = String(snapshot.phase || "").toLowerCase();
        if (phase === "ready" && downloadPoll) {
            window.clearInterval(downloadPoll);
            downloadPoll = null;
        }
        if (phase === "failed" && downloadPoll) {
            window.clearInterval(downloadPoll);
            downloadPoll = null;
        }
        const note = pendingAction && phase === "ready"
            ? (pendingAction === "Sofort" ? " Lokale Wiedergabe wurde gestartet." : " Lokale Wiedergabe wurde als Nächstes eingereiht.")
            : pendingAction && phase !== "failed" ? ` ${pendingAction} ist vorgemerkt.` : "";
        status.textContent = snapshot.error || `${phaseText[phase] || "Download wird vorbereitet …"}${note}`;
        status.className = phase === "failed" || snapshot.error ? "error" : phase === "ready" ? "success" : "loading";
        if (phase === "failed") actionButtons.forEach(button => { button.disabled = true; });
        if (phase === "ready") {
            fullMode.disabled = false;
            durationLabel.textContent = "Dauer: lokal analysiert";
        }
    };

    const pollDownload = async (videoId, generation) => {
        if (generation !== metadataGeneration) return;
        try {
            const response = await fetch(`/api/youtube/download/${encodeURIComponent(videoId)}`, {
                credentials: "same-origin", cache: "no-store", headers: { Accept: "application/json" }
            });
            if (!response.ok) throw new Error("Downloadstatus konnte nicht geladen werden.");
            if (generation === metadataGeneration) showDownload(await response.json());
        } catch (error) {
            if (generation === metadataGeneration) {
                status.textContent = error.message;
                status.className = "error";
            }
        }
    };

    const startDownload = async (reference, generation) => {
        if (generation !== metadataGeneration) return;
        downloadVideoId = reference.videoId;
        status.textContent = "Einbettung gesperrt. Lokaler Download wird vorbereitet …";
        status.className = "loading";
        try {
            const response = await fetch("/api/youtube/download", {
                method: "POST", credentials: "same-origin",
                headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
                body: new URLSearchParams({ url: reference.canonicalUrl })
            });
            const snapshot = await response.json();
            if (!response.ok) throw new Error(snapshot.error || "Der Download konnte nicht gestartet werden.");
            if (generation !== metadataGeneration) return;
            showDownload(snapshot);
            downloadPoll = window.setInterval(() => { void pollDownload(reference.videoId, generation); }, 1000);
            await pollDownload(reference.videoId, generation);
        } catch (error) {
            if (generation === metadataGeneration) {
                downloadVideoId = null;
                status.textContent = error.message;
                status.className = "error";
                actionButtons.forEach(button => { button.disabled = true; });
            }
        }
    };

    const ensureYouTubeApi = () => {
        if (window.YT?.Player) return Promise.resolve(window.YT);
        if (youtubeApiPromise) return youtubeApiPromise;
        youtubeApiPromise = new Promise((resolve, reject) => {
            const previousCallback = window.onYouTubeIframeAPIReady;
            const timeout = window.setTimeout(() => reject(new Error("YouTube Player API Timeout")), metadataTimeoutMs);
            window.onYouTubeIframeAPIReady = () => {
                window.clearTimeout(timeout);
                if (typeof previousCallback === "function") previousCallback();
                resolve(window.YT);
            };
            let script = document.getElementById("youtube-iframe-api");
            if (!script) {
                script = document.createElement("script");
                script.id = "youtube-iframe-api";
                script.src = "https://www.youtube.com/iframe_api";
                script.async = true;
                document.head.appendChild(script);
            }
            script.addEventListener("error", () => {
                window.clearTimeout(timeout);
                reject(new Error("YouTube Player API nicht erreichbar"));
            }, { once: true });
        }).catch(error => {
            youtubeApiPromise = null;
            throw error;
        });
        return youtubeApiPromise;
    };

    const waitForDuration = target => new Promise((resolve, reject) => {
        const started = Date.now();
        const check = () => {
            const value = target.getDuration?.();
            if (Number.isFinite(value) && value > 0) {
                resolve(value);
                return;
            }
            if (Date.now() - started >= metadataTimeoutMs) {
                reject(new Error("Die Videodauer konnte nicht ermittelt werden"));
                return;
            }
            window.setTimeout(check, 100);
        };
        check();
    });

    const createMetadataPlayer = (yt, reference) => new Promise((resolve, reject) => {
        const generation = metadataGeneration;
        preview.hidden = false;
        const host = document.createElement("div");
        preview.appendChild(host);
        const timeout = window.setTimeout(() => reject(new Error("YouTube Metadaten Timeout")), metadataTimeoutMs);
        player = new yt.Player(host, {
            width: "100%",
            height: "220",
            videoId: reference.videoId,
            playerVars: { autoplay: 0, controls: 1, modestbranding: 1, rel: 0 },
            events: {
                onReady: async event => {
                    try {
                        const value = await waitForDuration(event.target);
                        window.clearTimeout(timeout);
                        if (generation === metadataGeneration) resolve(value);
                        else reject(new Error("Die Metadatenanfrage wurde ersetzt."));
                    } catch (error) {
                        window.clearTimeout(timeout);
                        reject(error);
                    }
                },
                onError: event => {
                    window.clearTimeout(timeout);
                    if (generation === metadataGeneration) {
                        showPlayerError(event.data);
                        if (event.data === 101 || event.data === 150) void startDownload(reference, generation);
                    }
                    reject(new Error(playerErrorMessage(event.data)));
                }
            }
        });
    });

    const loadMetadata = async () => {
        resetMetadata();
        const generation = metadataGeneration;
        loadButton.disabled = true;
        status.textContent = "Video und Dauer werden geprüft …";
        status.className = "loading";
        try {
            const response = await fetch(`/api/youtube/reference?url=${encodeURIComponent(urlInput.value)}`, {
                credentials: "same-origin",
                headers: { "Accept": "application/json" },
                cache: "no-store"
            });
            if (!response.ok) {
                const problem = await response.json().catch(() => null);
                throw new Error(problem?.error || "YouTube-Link nicht erkannt");
            }
            const reference = await response.json();
            const yt = await ensureYouTubeApi();
            durationSeconds = await createMetadataPlayer(yt, reference);
            if (generation !== metadataGeneration) return;
            if (playerError) return;
            fullMode.disabled = false;
            status.textContent = `✓ Video erkannt (${reference.videoId})`;
            status.className = "success";
            durationLabel.textContent = `Dauer: ${formatDuration(durationSeconds)}`;
        } catch (error) {
            if (generation !== metadataGeneration) return;
            if (playerError) return;
            destroyPlayer();
            status.textContent = error instanceof Error ? error.message : "YouTube-Metadaten sind nicht verfügbar";
            status.className = "error";
            durationLabel.textContent = "Dauer: nicht verfügbar – maximale Laufzeit verwenden";
        } finally {
            if (generation === metadataGeneration) loadButton.disabled = false;
        }
    };

    form.addEventListener("change", event => {
        if (event.target?.name === "playbackMode") updateMode();
    });
    form.addEventListener("submit", event => {
        if (playerError && !downloadVideoId) {
            event.preventDefault();
            return;
        }
        const mode = selectedMode();
        if (mode === "full" && !downloadVideoId) {
            if (!Number.isFinite(durationSeconds) || durationSeconds <= 0) {
                event.preventDefault();
                status.textContent = "Bitte zuerst die YouTube-Metadaten laden.";
                status.className = "error";
                return;
            }
            startInput.value = "00:00:00";
            durationInput.value = formatDuration(durationSeconds);
            maximumInput.value = "";
        } else if (mode === "automatic") {
            startInput.value = "";
            durationInput.value = "";
            maximumInput.value ||= "00:10:00";
        } else {
            maximumInput.value = "";
        }
        if (!downloadVideoId) return;
        event.preventDefault();
        const action = event.submitter?.formAction?.endsWith("/now") ? "now" : "next";
        const actionLabel = action === "now" ? "Sofort" : "Als Nächstes";
        const data = new URLSearchParams({ action, mode, start: startInput.value,
            duration: durationInput.value, maximumDuration: maximumInput.value });
        void (async () => {
            try {
                const response = await fetch(`/api/youtube/download/${encodeURIComponent(downloadVideoId)}/intent`, {
                    method: "POST", credentials: "same-origin",
                    headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" }, body: data
                });
                const snapshot = await response.json();
                if (!response.ok) throw new Error(snapshot.error || "Die Wiedergabe konnte nicht vorgemerkt werden.");
                pendingAction = actionLabel;
                showDownload(snapshot);
            } catch (error) {
                status.textContent = error.message;
                status.className = "error";
            }
        })();
    });
    urlInput.addEventListener("input", resetMetadata);
    loadButton.addEventListener("click", loadMetadata);
    updateMode();
})();
