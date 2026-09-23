(() => {
    const previews = [...document.querySelectorAll('.media-preview[data-preview-url]')];
    if (!previews.length) return;

    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const pending = new Set(previews);
    const visible = new Set();
    const loading = new Set();

    const showFrame = (element, frame) => {
        element.style.backgroundPosition = `${-(frame % 6) * 160}px ${-Math.floor(frame / 6) * 90}px`;
    };

    const load = element => {
        if (loading.has(element)) return;
        loading.add(element);
        const image = new Image();
        image.onload = () => {
            element.style.backgroundImage = `url("${image.src}")`;
            element.classList.add('is-ready');
            showFrame(element, 0);
            pending.delete(element);
            loading.delete(element);
        };
        image.onerror = () => loading.delete(element);
        image.src = `${element.dataset.previewUrl}?retry=${Date.now()}`;
    };

    const observer = new IntersectionObserver(entries => {
        for (const entry of entries) {
            if (entry.isIntersecting) {
                visible.add(entry.target);
                if (pending.has(entry.target)) load(entry.target);
            } else {
                visible.delete(entry.target);
            }
        }
    });

    for (const element of previews) {
        observer.observe(element);
        let timer;
        const stop = () => {
            clearInterval(timer);
            timer = undefined;
            showFrame(element, 0);
        };
        element.addEventListener('mouseenter', () => {
            if (!element.classList.contains('is-ready') || reducedMotion.matches) return;
            let frame = 0;
            const count = Number(element.dataset.previewCount) || 1;
            timer = setInterval(() => showFrame(element, frame = (frame + 1) % count), 450);
        });
        element.addEventListener('mouseleave', stop);
    }

    setInterval(() => {
        for (const element of visible) if (pending.has(element)) load(element);
    }, 10000);
})();
