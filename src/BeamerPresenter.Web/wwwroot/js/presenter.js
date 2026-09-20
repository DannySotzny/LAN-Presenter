(() => {
    "use strict";

    const recordSeparator = "\u001e";
    const video = document.getElementById("presenter-video");
    const idle = document.getElementById("presenter-idle");
    const statusText = document.getElementById("presenter-status");
    if (!video || !idle || !statusText) {
        return;
    }

    let socket;
    let segmentEndSeconds = null;
    let reconnectTimer;

    const setStatus = (text) => {
        statusText.textContent = text;
    };

    const sendInvocation = (target, argumentsList) => {
        if (socket?.readyState === WebSocket.OPEN) {
            socket.send(JSON.stringify({ type: 1, target, arguments: argumentsList }) + recordSeparator);
        }
    };

    const report = (status, message = null) => {
        sendInvocation("ReportStatus", [status, video.currentTime || null, Number.isFinite(video.duration) ? video.duration : null, message]);
    };

    const loadLocalVideo = async (mediaId, startSeconds, endSeconds, autoPlay) => {
        segmentEndSeconds = Number.isFinite(endSeconds) ? endSeconds : null;
        video.src = `/media/${encodeURIComponent(mediaId)}`;
        video.load();
        video.onloadedmetadata = async () => {
            if (Number.isFinite(startSeconds) && startSeconds > 0) {
                video.currentTime = Math.min(startSeconds, video.duration || startSeconds);
            }

            idle.classList.add("presenter-idle-hidden");
            report("Ready");
            if (autoPlay) {
                try {
                    await video.play();
                } catch (error) {
                    report("Error", error instanceof Error ? error.message : "Autoplay wurde abgelehnt.");
                }
            }
        };
    };

    const stopPlayback = () => {
        video.pause();
        video.removeAttribute("src");
        video.load();
        segmentEndSeconds = null;
        idle.classList.remove("presenter-idle-hidden");
        setStatus("Bereit für die nächste Wiedergabe.");
        report("Stopped");
    };

    const handleInvocation = async (message) => {
        const target = String(message.target || "").toLowerCase();
        const args = message.arguments || [];
        switch (target) {
            case "loadlocalvideo":
                await loadLocalVideo(args[0], args[1], args[2], args[3]);
                break;
            case "play":
                await video.play();
                break;
            case "pause":
                video.pause();
                break;
            case "stop":
                stopPlayback();
                break;
            case "seek":
                if (Number.isFinite(args[0])) video.currentTime = Math.max(0, args[0]);
                break;
            case "setvolume":
                if (Number.isFinite(args[0])) video.volume = Math.max(0, Math.min(1, args[0]));
                break;
        }
    };

    const handleMessages = async (payload) => {
        for (const record of payload.split(recordSeparator)) {
            if (!record) continue;
            const message = JSON.parse(record);
            if (message.type === 1) await handleInvocation(message);
            if (message.type === 7) socket?.close();
        }
    };

    const connect = async () => {
        clearTimeout(reconnectTimer);
        try {
            setStatus("Echtzeitverbindung wird hergestellt …");
            const negotiation = await fetch("/hubs/presenter/negotiate?negotiateVersion=1", { method: "POST" });
            if (!negotiation.ok) throw new Error(`SignalR negotiation failed (${negotiation.status}).`);
            const details = await negotiation.json();
            const scheme = location.protocol === "https:" ? "wss" : "ws";
            socket = new WebSocket(`${scheme}://${location.host}/hubs/presenter?id=${encodeURIComponent(details.connectionToken)}`);
            socket.addEventListener("open", () => socket.send(JSON.stringify({ protocol: "json", version: 1 }) + recordSeparator));
            socket.addEventListener("message", async (event) => {
                await handleMessages(String(event.data));
                if (String(event.data).startsWith("{}")) {
                    setStatus("Verbunden. Bereit für die nächste Wiedergabe.");
                    report("Connected");
                }
            });
            socket.addEventListener("close", () => {
                setStatus("Verbindung getrennt. Neuer Versuch …");
                reconnectTimer = setTimeout(connect, 2000);
            });
            socket.addEventListener("error", () => socket?.close());
        } catch {
            setStatus("Verbindung nicht verfügbar. Neuer Versuch …");
            reconnectTimer = setTimeout(connect, 2000);
        }
    };

    video.addEventListener("playing", () => report("Playing"));
    video.addEventListener("pause", () => video.currentSrc && report("Paused"));
    video.addEventListener("waiting", () => report("Buffering"));
    video.addEventListener("ended", () => report("Ended"));
    video.addEventListener("error", () => report("Error", video.error?.message || "Das Video konnte nicht wiedergegeben werden."));
    video.addEventListener("timeupdate", () => {
        if (segmentEndSeconds !== null && video.currentTime >= segmentEndSeconds) {
            video.pause();
            report("Ended");
        }
    });

    setInterval(() => report("Heartbeat"), 5000);
    window.addEventListener("beforeunload", () => clearTimeout(reconnectTimer));
    connect();
})();
