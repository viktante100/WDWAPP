import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

// Minimal DOM/FileList stand-ins exercise the actual selection handler and removal callbacks.
class Element {
    children = [];
    listeners = {};
    textContent = '';
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren() { this.children = []; }
    setAttribute() {}
    addEventListener(name, callback) { this.listeners[name] = callback; }
}
globalThis.document = { createElement: () => new Element(), addEventListener() {} };
globalThis.DataTransfer = class {
    files = [];
    items = { add: file => this.files.push(file) };
};
const source = await readFile(new URL('../wwwroot/marketplace.js', import.meta.url), 'utf8');
const { handleUploadSelection } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const file = (name, size = 100) => ({ name, size, lastModified: 1 });
function input(existingCount = 0, removed = 0) {
    const list = new Element();
    const status = new Element();
    return {
        files: [], dataset: { existingCount }, list, status, focus() {},
        closest: selector => selector === 'form'
            ? { querySelectorAll: () => Array(removed) }
            : { querySelector: selector => selector === '[data-upload-list]' ? list : status }
    };
}
test('separate file selections append and submit both images', () => {
    const upload = input();
    upload.files = [file('first.png')]; handleUploadSelection(upload);
    upload.files = [file('second.png')]; handleUploadSelection(upload);
    assert.deepEqual(upload.files.map(f => f.name), ['first.png', 'second.png']);
    assert.equal(upload.list.children.length, 2);
    assert.equal(upload.status.textContent, '2 nya bilder valda.');
    upload.files = []; handleUploadSelection(upload); // Cancelled picker must keep prior selection.
    assert.equal(upload.files.length, 2);
});
test('selected files can be removed and subsequent selection preserves the remaining files', () => {
    const upload = input();
    upload.files = [file('first.png'), file('second.png')]; handleUploadSelection(upload);
    upload.list.children[0].children[1].listeners.click();
    upload.files = [file('third.png')]; handleUploadSelection(upload);
    assert.deepEqual(upload.files.map(f => f.name), ['second.png', 'third.png']);
});
test('existing images count toward ten and a rejected selection does not discard prior choices', () => {
    const upload = input(9);
    upload.files = [file('first.png')]; handleUploadSelection(upload);
    upload.files = [file('second.png')]; handleUploadSelection(upload);
    assert.deepEqual(upload.files.map(f => f.name), ['first.png']);
    assert.match(upload.status.textContent, /Högst 10/);
    const replacement = input(10, 1);
    replacement.files = [file('replacement.png')]; handleUploadSelection(replacement);
    assert.equal(replacement.files.length, 1);
});
test('oversized files are rejected and selecting the same file twice does not duplicate it', () => {
    const upload = input();
    upload.files = [file('first.png')]; handleUploadSelection(upload);
    upload.files = [file('huge.png', 6 * 1024 * 1024)]; handleUploadSelection(upload);
    assert.deepEqual(upload.files.map(f => f.name), ['first.png']);
    upload.files = [file('first.png')]; handleUploadSelection(upload);
    assert.equal(upload.files.length, 1);
});
