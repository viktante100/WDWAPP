function initializePortraits() {
    document.querySelectorAll('[data-player-image-editor]').forEach(editor => {
        if (editor.dataset.initialized) return;
        editor.dataset.initialized = 'true';
        const frame = editor.querySelector('[data-player-portrait]');
        const image = frame.querySelector('img');
        const zoom = editor.querySelector('[data-portrait-zoom]');
        const x = editor.querySelector('[data-portrait-x]');
        const y = editor.querySelector('[data-portrait-y]');
        const upload = editor.querySelector('input[type=file]');
        const remove = editor.querySelector('input[name="Input.RemoveImage"]');
        let objectUrl;
        let pointer;
        const hasPhoto = () => !!objectUrl || (!!editor.dataset.originalImage && !remove?.checked);
        function render() {
            const photo = hasPhoto();
            for (const control of [zoom, x, y]) control.disabled = !photo;
            const z = photo ? Number(zoom.value) : 1;
            const px = photo ? Number(x.value) : 50;
            const py = photo ? Number(y.value) : 25;
            frame.style.setProperty('--portrait-zoom', z);
            frame.style.setProperty('--portrait-x', `${px}%`);
            frame.style.setProperty('--portrait-y', `${py}%`);
            frame.style.setProperty('--portrait-tx', `${(50 - px) * (z - 1)}%`);
            frame.style.setProperty('--portrait-ty', `${(50 - py) * (z - 1)}%`);
        }
        function source() {
            image.src = objectUrl || (hasPhoto() ? editor.dataset.originalImage
                : `/Images/avatars/${editor.querySelector('input[type=radio]:checked').value}.svg`);
            render();
        }
        for (const control of [zoom, x, y]) control.addEventListener('input', render);
        editor.querySelectorAll('input[type=radio]').forEach(r => r.addEventListener('change', source));
        remove?.addEventListener('change', source);
        upload.addEventListener('change', () => {
            if (objectUrl) URL.revokeObjectURL(objectUrl);
            objectUrl = upload.files[0] ? URL.createObjectURL(upload.files[0]) : undefined;
            if (objectUrl) { zoom.value = 1; x.value = 50; y.value = 25; }
            source();
        });
        frame.addEventListener('pointerdown', event => {
            if (!hasPhoto() || !image.naturalWidth || event.button !== 0) return;
            pointer = { id: event.pointerId, x: event.clientX, y: event.clientY };
            frame.setPointerCapture(event.pointerId);
            event.preventDefault();
        });
        frame.addEventListener('pointermove', event => {
            if (!pointer || pointer.id !== event.pointerId) return;
            const rect = frame.getBoundingClientRect();
            const scale = Math.max(rect.width / image.naturalWidth, rect.height / image.naturalHeight) * Number(zoom.value);
            const overflowX = image.naturalWidth * scale - rect.width;
            const overflowY = image.naturalHeight * scale - rect.height;
            if (overflowX > 0) x.value = Math.max(0, Math.min(100, Number(x.value) - (event.clientX - pointer.x) * 100 / overflowX));
            if (overflowY > 0) y.value = Math.max(0, Math.min(100, Number(y.value) - (event.clientY - pointer.y) * 100 / overflowY));
            pointer.x = event.clientX;
            pointer.y = event.clientY;
            render();
        });
        for (const eventName of ['pointerup', 'pointercancel', 'lostpointercapture'])
            frame.addEventListener(eventName, () => { pointer = undefined; });
        source();
    });
}
initializePortraits();
if (window.Blazor) Blazor.addEventListener('enhancedload', initializePortraits);
