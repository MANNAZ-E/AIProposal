// A cell that turns into a <select> on click has cost the user their click: the list they were
// after is still shut, and only a second click opens it. showPicker() drops it open with the
// element, which no Blazor markup can ask for. It needs transient user activation, so it only
// works on the render that follows the click that opened the editor - which is exactly when it
// is called. Anything else (an expired activation, a browser without the API) falls back to a
// focused select the user can open themselves.
window.sagaSelect = {
    openPicker: el => {
        if (!el) return;
        el.focus();
        try {
            el.showPicker();
        } catch {
            // Not allowed here, or not implemented; the focus above still stands.
        }
    }
};
