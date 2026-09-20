// Delegated listeners also work after Blazor enhanced navigation replaces the page.
const selections = new WeakMap();

export function appendFiles(previous, incoming) {
    const result = [...previous];
    for (const file of incoming) {
        if (!result.some(existing => existing.name === file.name && existing.size === file.size && existing.lastModified === file.lastModified))
            result.push(file);
    }
    return result;
}

function setFiles(input, files) {
    const transfer = new DataTransfer();
    files.forEach(file => transfer.items.add(file));
    input.files = transfer.files;
    selections.set(input, files);
}

function renderSelection(input, message = "") {
    const fieldset = input.closest('fieldset');
    const files = selections.get(input) ?? [];
    const list = fieldset.querySelector('[data-upload-list]');
    list.replaceChildren();
    files.forEach((file, index) => {
        const row = document.createElement('li');
        const name = document.createElement('span');
        name.textContent = file.name;
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.textContent = 'Ta bort';
        remove.setAttribute('aria-label', `Ta bort ${file.name}`);
        remove.addEventListener('click', () => {
            setFiles(input, files.filter((_, i) => i !== index));
            renderSelection(input);
            input.focus();
        });
        row.append(name, remove);
        list.append(row);
    });
    fieldset.querySelector('[data-upload-status]').textContent = message || `${files.length} nya bilder valda.`;
}

export function handleUploadSelection(input) {
    const previous = selections.get(input) ?? [];
    const combined = appendFiles(previous, Array.from(input.files));
    const removed = input.closest('form').querySelectorAll('input[name="Input.RemoveImageIds"]:checked').length;
    const existing = Number(input.dataset.existingCount) - removed;
    let message = '';
    if (existing + combined.length > 10) message = 'Högst 10 bilder totalt. Det senaste urvalet lades inte till.';
    else if (combined.some(file => file.size > 5 * 1024 * 1024 || file.size === 0)) message = 'Varje bild måste vara mellan 1 byte och 5 MB. Det senaste urvalet lades inte till.';
    setFiles(input, message ? previous : combined);
    renderSelection(input, message);
}

if (typeof document !== 'undefined') {
    document.addEventListener('change', event => {
        if (event.target.matches('[data-market-upload]')) handleUploadSelection(event.target);
    });
    document.addEventListener('click', event => {
        const imageButton = event.target.closest('[data-market-image]');
        if (imageButton) {
            const dialog = document.getElementById('market-image-popup');
            const image = dialog.querySelector('img');
            image.src = imageButton.dataset.marketImage;
            image.alt = imageButton.dataset.imageTitle;
            dialog.querySelector('h2').textContent = imageButton.dataset.imageTitle;
            dialog.showModal();
            return;
        }
        const opener = event.target.closest('[data-market-dialog]');
        if (opener) document.getElementById(opener.dataset.marketDialog)?.showModal();
        const closer = event.target.closest('[data-market-close]');
        if (closer) closer.closest('dialog').close();
        if (event.target.matches('dialog.market-dialog')) {
            const rect = event.target.getBoundingClientRect();
            if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom)
                event.target.close();
        }
    });
}
