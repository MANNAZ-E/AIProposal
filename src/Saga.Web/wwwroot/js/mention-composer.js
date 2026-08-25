// The mention picker's keys have to beat the textarea's own defaults - Up/Down moving the caret
// between lines, Tab leaving the field, Enter inserting a newline whose input event lands after
// the commit and undoes it. Blazor's preventDefault is fixed at render time and so cannot be
// aimed at four keys, which is why this listener exists rather than a razor directive. It is
// gated on the attribute the composer renders while the picker is open, so it is inert
// everywhere else, and it never stops propagation: Blazor still gets every keydown.
const PICKER_KEYS = ['ArrowUp', 'ArrowDown', 'Tab', 'Enter'];

document.addEventListener('keydown', e => {
    if (e.target?.dataset?.mentionPicker !== 'open') return;
    // Shift+Enter is still the newline and Shift+Tab still walks back; modifier combos are the
    // browser's own.
    if (e.shiftKey || e.ctrlKey || e.altKey || e.metaKey) return;
    if (PICKER_KEYS.includes(e.key)) e.preventDefault();
}, true);

window.sagaMention = {
    // Blazor rewrites the whole value when a name is committed; leave the caret after it.
    caretToEnd: el => {
        el.focus();
        const end = el.value.length;
        el.setSelectionRange(end, end);
    }
};
