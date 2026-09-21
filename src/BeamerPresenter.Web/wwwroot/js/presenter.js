(() => {
    "use strict";

    const recordSeparator = "\u001e";
    const youtubeApiTimeoutMs = 5000;
    const video = document.getElementById("presenter-video");
    const youtubeHost = document.getElementById("presenter-youtube-host");
    const idle = document.getElementById("presenter-idle");
    const statusText = document.getElementById("presenter-status");
    if (!video || !youtubeHost || !idle || !statusText) return;

    let socket;
    let reconnectTimer;
    let segmentEndSeconds = null;
    let activeSource = "none";
    let youtubePlayer = null;
    let youtubeApiPromise = null;
    let youtubeSegmentTimer = null;
    let terminalReported = false;

    const setStatus = (text) => { statusText.textContent = text; };
    const sendInvocation = (target, argumentsList) => {
        if (socket?.readyState === WebSocket.OPEN) {
            socket.send(JSON.stringify({ type: 1, target, arguments: argumentsList }) + recordSeparator);
        }
    };
    const currentPosition = () => {
        if (activeSource === "youtube" && youtubePlayer?.getCurrentTime) return youtubePlayer.getCurrentTime() || null;
        if (activeSource === "local") return video.currentTime || null;
        return null;
    };
    const currentDuration = () => {
        if (activeSource === "youtube" && youtubePlayer?.getDuration) return youtubePlayer.getDuration() || null;
        if (activeSource === "local" && Number.isFinite(video.duration)) return video.duration;
        return null;
    };
    const report = (status, message = null) =>
        sendInvocation("ReportStatus", [status, currentPosition(), currentDuration(), message]);
    const reportTerminal = (status, message = null) => {
        if (terminalReported) return;
        terminalReported = true;
        report(status, message);
    };

    const destroyYouTubePlayer = () => {
        clearInterval(youtubeSegmentTimer);
        youtubeSegmentTimer = null;
        if (youtubePlayer?.destroy) youtubePlayer.destroy();
        youtubePlayer = null;
        youtubeHost.replaceChildren();
        youtubeHost.classList.add("presenter-media-hidden");
    };
    const stopLocalVideo = () => {
        video.pause();
        video.removeAttribute("src");
        video.load();
        video.classList.add("presenter-media-hidden");
    };
    const showPlayback = (source) => {
        activeSource = source;
        terminalReported = false;
        idle.classList.add("presenter-idle-hidden");
        video.classList.toggle("presenter-media-hidden", source !== "local");
        youtubeHost.classList.toggle("presenter-media-hidden", source !== "youtube");
    };

    const ensureYouTubeApi = () => {
        if (window.YT?.Player) return Promise.resolve(window.YT);
        if (youtubeApiPromise) return youtubeApiPromise;
        youtubeApiPromise = new Promise((resolve, reject) => {
            const previousCallback = window.onYouTubeIframeAPIReady;
            const timeout = window.setTimeout(() => reject(new Error("YouTube Player API timeout")), youtubeApiTimeoutMs);
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
                reject(new Error("YouTube Player API unavailable"));
            }, { once: true });
        }).catch(error => {
            youtubeApiPromise = null;
            throw error;
        });
        return youtubeApiPromise;
    };

    const loadLocalVideo = async (mediaId, startSeconds, endSeconds, autoPlay) => {
        destroyYouTubePlayer();
        segmentEndSeconds = Number.isFinite(endSeconds) ? endSeconds : null;
        showPlayback("local");
        video.src = `/media/${encodeURIComponent(mediaId)}`;
        video.load();
        video.onloadedmetadata = async () => {
            if (Number.isFinite(startSeconds) && startSeconds > 0) {
                video.currentTime = Math.min(startSeconds, video.duration || startSeconds);
            }
            report("Ready");
            if (autoPlay) {
                try { await video.play(); }
                catch (error) { reportTerminal("Error", error instanceof Error ? error.message : "Autoplay wurde abgelehnt."); }
            }
        };
    };

    const loadYouTubeVideo = async (videoId, startSeconds, endSeconds, autoPlay) => {
        stopLocalVideo();
        destroyYouTubePlayer();
        segmentEndSeconds = Number.isFinite(endSeconds) ? endSeconds : null;
        showPlayback("youtube");
        setStatus("YouTube Player wird geladen …");
        try {
            const yt = await ensureYouTubeApi();
            const playerElement = document.createElement("div");
            youtubeHost.appendChild(playerElement);
            youtubePlayer = new yt.Player(playerElement, {
                videoId,
                playerVars: { autoplay: 0, controls: 0, disablekb: 1, modestbranding: 1, rel: 0 },
                events: {
                    onReady: event => {
                        const duration = event.target.getDuration();
                        if (Number.isFinite(startSeconds) && startSeconds > 0) {
                            event.target.seekTo(Math.min(startSeconds, duration || startSeconds), true);
                        }
                        report("Ready");
                        if (autoPlay) event.target.playVideo();
                    },
                    onStateChange: event => {
                        if (event.data === yt.PlayerState.PLAYING) report("Playing");
                        else if (event.data === yt.PlayerState.PAUSED) report("Paused");
                        else if (event.data === yt.PlayerState.BUFFERING) report("Buffering");
                        else if (event.data === yt.PlayerState.ENDED) reportTerminal("Ended");
                    },
                    onError: event => reportTerminal("Error", `YouTube Player Fehler ${event.data}`)
                }
            });
            youtubeSegmentTimer = window.setInterval(() => {
                if (segmentEndSeconds !== null && youtubePlayer?.getCurrentTime?.() >= segmentEndSeconds) {
                    youtubePlayer.pauseVideo();
                    reportTerminal("Ended");
                }
            }, 250);
        } catch (error) {
            reportTerminal("Error", error instanceof Error ? error.message : "YouTube ist nicht verfügbar.");
        }
    };

    const stopPlayback = () => {
        stopLocalVideo();
        destroyYouTubePlayer();
        activeSource = "none";
        segmentEndSeconds = null;
        terminalReported = false;
        idle.classList.remove("presenter-idle-hidden");
        setStatus("Bereit für die nächste Wiedergabe.");
        report("Stopped");
    };
    const handleInvocation = async (message) => {
        const target = String(message.target || "").toLowerCase();
        const args = message.arguments || [];
        switch (target) {
            case "loadlocalvideo": await loadLocalVideo(args[0], args[1], args[2], args[3]); break;
            case "loadyoutubevideo": await loadYouTubeVideo(args[0], args[1], args[2], args[3]); break;
            case "play":
                if (activeSource === "youtube") youtubePlayer?.playVideo?.(); else await video.play();
                break;
            case "pause":
                if (activeSource === "youtube") youtubePlayer?.pauseVideo?.(); else video.pause();
                break;
            case "stop": stopPlayback(); break;
            case "seek":
                if (Number.isFinite(args[0])) {
                    if (activeSource === "youtube") youtubePlayer?.seekTo?.(Math.max(0, args[0]), true);
                    else video.currentTime = Math.max(0, args[0]);
                }
                break;
            case "setvolume":
                if (Number.isFinite(args[0])) {
                    const volume = Math.max(0, Math.min(1, args[0]));
                    if (activeSource === "youtube") youtubePlayer?.setVolume?.(volume * 100);
                    else video.volume = volume;
                }
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
            socket.addEventListener("message", async event => {
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

    video.addEventListener("playing", () => activeSource === "local" && report("Playing"));
    video.addEventListener("pause", () => activeSource === "local" && video.currentSrc && report("Paused"));
    video.addEventListener("waiting", () => activeSource === "local" && report("Buffering"));
    video.addEventListener("ended", () => activeSource === "local" && reportTerminal("Ended"));
    video.addEventListener("error", () => activeSource === "local" && reportTerminal("Error", video.error?.message || "Das Video konnte nicht wiedergegeben werden."));
    video.addEventListener("timeupdate", () => {
        if (activeSource === "local" && segmentEndSeconds !== null && video.currentTime >= segmentEndSeconds) {
            video.pause();
            reportTerminal("Ended");
        }
    });
    setInterval(() => report("Heartbeat"), 5000);
    window.addEventListener("beforeunload", () => {
        clearTimeout(reconnectTimer);
        clearInterval(youtubeSegmentTimer);
    });
    connect();
})();
