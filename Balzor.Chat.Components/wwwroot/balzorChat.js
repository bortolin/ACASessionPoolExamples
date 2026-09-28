export function scrollToBottom(el) {
    if (!el) {
        return;
    }
    el.scrollTop = el.scrollHeight;
}
