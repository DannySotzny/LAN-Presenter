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

    let generation = 0;
    let downloadVideoId = null;
    let downloadPoll = null;
    let pendingAction = null;
    const actionButtons = form.querySelectorAll(".youtube-actions button");

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
        startRow.hidden = !custom;
        durationRow.hidden = !custom;
        maximumRow.hidden = mode !== "automatic";
        startInput.required = custom;
        durationInput.required = custom;
        maximumInput.required = mode === "automatic";
    };

    const reset = () => {
        generation += 1;
        if (downloadPoll) window.clearInterval(downloadPoll);
        downloadPoll = null;
        downloadVideoId = null;
        pendingAction = null;
        preview.replaceChildren();
        preview.hidden = true;
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

    const pendingDownloadNote = phase => {
        if (!pendingAction) return "";
        if (phase === "ready") {
            return pendingAction === "Sofort"
                ? " Lokale Wiedergabe wurde gestartet."
                : " Lokale Wiedergabe wurde als Nächstes eingereiht.";
        }
        return phase === "failed" ? "" : ` ${pendingAction} ist vorgemerkt.`;
    };

    const showDownloadStatus = (snapshot, phase) => {
        const note = pendingDownloadNote(phase);
        status.textContent = snapshot.error || `${phaseText[phase] || "Download wird vorbereitet …"}${note}`;
        if (phase === "failed" || snapshot.error) status.className = "error";
        else if (phase === "ready") status.className = "success";
        else status.className = "loading";
        if (phase === "failed") actionButtons.forEach(button => { button.disabled = true; });
    };

    const showDownloadPreview = snapshot => {
        fullMode.disabled = false;
        durationLabel.textContent = Number.isFinite(snapshot.durationSeconds) && snapshot.durationSeconds > 0
            ? `Dauer: ${formatDuration(snapshot.durationSeconds)}` : "Dauer: lokal analysiert";
        if (snapshot.mediaId && !preview.querySelector(`video[data-media-id="${snapshot.mediaId}"]`)) {
            const video = document.createElement("video");
            video.controls = true;
            video.preload = "none";
            video.dataset.mediaId = String(snapshot.mediaId);
            video.src = `/media/${snapshot.mediaId}`;
            preview.replaceChildren(video);
            preview.hidden = false;
        }
    };

    const showDownload = snapshot => {
        const phase = String(snapshot.phase || "").toLowerCase();
        if ((phase === "ready" || phase === "failed") && downloadPoll) {
            window.clearInterval(downloadPoll);
            downloadPoll = null;
        }
        showDownloadStatus(snapshot, phase);
        if (phase === "ready") showDownloadPreview(snapshot);
    };

    const showError = error => {
        status.textContent = error instanceof Error ? error.message : "Das Video konnte nicht geladen werden.";
        status.className = "error";
    };

    const normalizeUrl = async () => {
        const response = await fetch(`/api/youtube/reference?url=${encodeURIComponent(urlInput.value)}`, {
            credentials: "same-origin", cache: "no-store", headers: { Accept: "application/json" }
        });
        const result = await response.json().catch(() => null);
        if (!response.ok) throw new Error(result?.error || "YouTube-Link nicht erkannt.");
        return result;
    };

    const pollDownload = async (videoId, requestGeneration) => {
        if (requestGeneration !== generation) return;
        try {
            const response = await fetch(`/api/youtube/download/${encodeURIComponent(videoId)}`, {
                credentials: "same-origin", cache: "no-store", headers: { Accept: "application/json" }
            });
            if (!response.ok) throw new Error("Downloadstatus konnte nicht geladen werden.");
            const snapshot = await response.json();
            if (requestGeneration === generation) showDownload(snapshot);
        } catch (error) {
            if (requestGeneration === generation) showError(error);
        }
    };

    const startDownload = async (reference, requestGeneration) => {
        if (requestGeneration !== generation) return false;
        downloadVideoId = reference.videoId;
        status.textContent = "Lokaler Download wird vorbereitet …";
        status.className = "loading";
        try {
            const response = await fetch("/api/youtube/download", {
                method: "POST", credentials: "same-origin",
                headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
                body: new URLSearchParams({ url: reference.canonicalUrl })
            });
            const snapshot = await response.json();
            if (!response.ok) throw new Error(snapshot.error || "Der Download konnte nicht gestartet werden.");
            if (requestGeneration !== generation) return false;
            showDownload(snapshot);
            if (snapshot.phase === "failed") return false;
            if (downloadPoll) window.clearInterval(downloadPoll);
            if (snapshot.phase !== "ready" && snapshot.phase !== "failed") {
                downloadPoll = window.setInterval(() => { void pollDownload(reference.videoId, requestGeneration); }, 1000);
                await pollDownload(reference.videoId, requestGeneration);
            }
            return true;
        } catch (error) {
            if (requestGeneration === generation) {
                downloadVideoId = null;
                showError(error);
                actionButtons.forEach(button => { button.disabled = true; });
            }
            return false;
        }
    };

    const loadMetadata = async () => {
        reset();
        const requestGeneration = generation;
        loadButton.disabled = true;
        status.textContent = "YouTube-Link wird geprüft …";
        status.className = "loading";
        try {
            const reference = await normalizeUrl();
            if (requestGeneration === generation) await startDownload(reference, requestGeneration);
        } catch (error) {
            if (requestGeneration === generation) showError(error);
        } finally {
            if (requestGeneration === generation) loadButton.disabled = false;
        }
    };

    form.addEventListener("change", event => {
        if (event.target?.name === "playbackMode") updateMode();
    });
    form.addEventListener("submit", event => {
        event.preventDefault();
        const requestGeneration = generation;
        const action = event.submitter?.formAction?.endsWith("/now") ? "now" : "next";
        const actionLabel = action === "now" ? "Sofort" : "Als Nächstes";
        const mode = selectedMode();
        const data = new URLSearchParams({
            action, mode,
            start: mode === "custom" ? startInput.value : "",
            duration: mode === "custom" ? durationInput.value : "",
            maximumDuration: mode === "automatic" ? maximumInput.value || "00:10:00" : ""
        });
        void (async () => {
            try {
                if (!downloadVideoId) {
                    const reference = await normalizeUrl();
                    if (!await startDownload(reference, requestGeneration)) return;
                }
                if (requestGeneration !== generation) return;
                const response = await fetch(`/api/youtube/download/${encodeURIComponent(downloadVideoId)}/intent`, {
                    method: "POST", credentials: "same-origin",
                    headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" }, body: data
                });
                const snapshot = await response.json();
                if (!response.ok) throw new Error(snapshot.error || "Die Wiedergabe konnte nicht vorgemerkt werden.");
                if (requestGeneration !== generation) return;
                pendingAction = actionLabel;
                showDownload(snapshot);
            } catch (error) {
                if (requestGeneration === generation) showError(error);
            }
        })();
    });
    urlInput.addEventListener("input", reset);
    loadButton.addEventListener("click", loadMetadata);
    updateMode();
})();
