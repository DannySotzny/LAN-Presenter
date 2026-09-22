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
        destroyPlayer();
        durationSeconds = null;
        fullMode.disabled = true;
        if (selectedMode() === "full") {
            form.querySelector('input[name="playbackMode"][value="automatic"]').checked = true;
        }
        status.textContent = "Noch nicht geprüft";
        status.className = "";
        durationLabel.textContent = "Dauer: –";
        updateMode();
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

    const createMetadataPlayer = (yt, videoId) => new Promise((resolve, reject) => {
        preview.hidden = false;
        const host = document.createElement("div");
        preview.appendChild(host);
        const timeout = window.setTimeout(() => reject(new Error("YouTube Metadaten Timeout")), metadataTimeoutMs);
        player = new yt.Player(host, {
            width: "100%",
            height: "220",
            videoId,
            playerVars: { autoplay: 0, controls: 1, modestbranding: 1, rel: 0 },
            events: {
                onReady: async event => {
                    try {
                        const value = await waitForDuration(event.target);
                        window.clearTimeout(timeout);
                        resolve(value);
                    } catch (error) {
                        window.clearTimeout(timeout);
                        reject(error);
                    }
                },
                onError: event => {
                    window.clearTimeout(timeout);
                    reject(new Error(`YouTube Player Fehler ${event.data}`));
                }
            }
        });
    });

    const loadMetadata = async () => {
        resetMetadata();
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
            durationSeconds = await createMetadataPlayer(yt, reference.videoId);
            fullMode.disabled = false;
            status.textContent = `✓ Video erkannt (${reference.videoId})`;
            status.className = "success";
            durationLabel.textContent = `Dauer: ${formatDuration(durationSeconds)}`;
        } catch (error) {
            destroyPlayer();
            status.textContent = error instanceof Error ? error.message : "YouTube-Metadaten sind nicht verfügbar";
            status.className = "error";
            durationLabel.textContent = "Dauer: nicht verfügbar – maximale Laufzeit verwenden";
        } finally {
            loadButton.disabled = false;
        }
    };

    form.addEventListener("change", event => {
        if (event.target?.name === "playbackMode") updateMode();
    });
    form.addEventListener("submit", event => {
        const mode = selectedMode();
        if (mode === "full") {
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
    });
    urlInput.addEventListener("input", resetMetadata);
    loadButton.addEventListener("click", loadMetadata);
    updateMode();
})();
