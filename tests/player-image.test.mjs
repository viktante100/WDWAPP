import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const script = readFileSync(new URL('../wwwroot/player-image.js', import.meta.url), 'utf8');
function setup() {
    const control = value => ({ value, listeners: {}, addEventListener(name, fn) { this.listeners[name] = fn; } });
    const zoom = control('1'), x = control('50'), y = control('25');
    const upload = control(''), remove = control(''), radio = control('person-circle');
    const image = { naturalWidth: 400, naturalHeight: 400 };
    const style = {};
    const frame = { ...control(''), querySelector: () => image, setPointerCapture() {},
        getBoundingClientRect: () => ({ width: 120, height: 144 }),
        style: { setProperty: (key, value) => { style[key] = String(value); } } };
    const elements = { '[data-player-portrait]': frame, '[data-portrait-zoom]': zoom,
        '[data-portrait-x]': x, '[data-portrait-y]': y, 'input[type=file]': upload,
        'input[name="Input.RemoveImage"]': remove, 'input[type=radio]:checked': radio };
    const editor = { dataset: { originalImage: '/saved.png' }, querySelector: key => elements[key], querySelectorAll: () => [radio] };
    const revoked = [];
    runInNewContext(script, { document: { querySelectorAll: () => [editor] }, window: {},
        URL: { createObjectURL: () => 'blob:new-photo', revokeObjectURL: value => revoked.push(value) } });
    return { zoom, x, y, upload, remove, radio, frame, image, style, revoked };
}

test('zoom and dragging update saved controls and clamp the crop to image bounds', () => {
    const s = setup();
    s.zoom.value = '2'; s.zoom.listeners.input();
    s.frame.listeners.pointerdown({ pointerId: 1, button: 0, clientX: 0, clientY: 0, preventDefault() {} });
    s.frame.listeners.pointermove({ pointerId: 1, clientX: 42, clientY: -36 });
    assert.equal(Number(s.x.value), 25);
    assert.equal(Number(s.y.value), 50);
    assert.equal(s.style['--portrait-tx'], '25%');
    assert.equal(s.style['--portrait-zoom'], '2');
    s.frame.listeners.pointermove({ pointerId: 1, clientX: 10000, clientY: -10000 });
    assert.equal(Number(s.x.value), 0);
    assert.equal(Number(s.y.value), 100);
    s.frame.listeners.pointerup();
    s.frame.listeners.pointermove({ pointerId: 1, clientX: 0, clientY: 0 });
    assert.equal(Number(s.x.value), 0);
});

test('new upload resets the preview crop and removing the saved image previews the avatar', () => {
    const s = setup();
    s.zoom.value = '3';
    s.upload.files = [{}]; s.upload.listeners.change();
    assert.equal(s.image.src, 'blob:new-photo');
    assert.equal(Number(s.zoom.value), 1);
    s.upload.files = []; s.upload.listeners.change();
    assert.deepEqual(s.revoked, ['blob:new-photo']);
    assert.equal(s.image.src, '/saved.png');
    s.remove.checked = true; s.remove.listeners.change();
    assert.equal(s.image.src, '/Images/avatars/person-circle.svg');
    assert.equal(s.zoom.disabled, true);
    s.remove.checked = false; s.remove.listeners.change();
    assert.equal(s.image.src, '/saved.png');
    assert.equal(s.zoom.disabled, false);
});
