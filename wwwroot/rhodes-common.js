// Shared by the Rhodes pages (home and chat): small DOM helpers, formatting, the model status poll and the styled dialog.
'use strict';

const conversationStorageKey = 'rhodesConversationId';
const startMessageStorageKey = 'rhodesStartMessage';

function readStorage(storageKey) {
    try { return localStorage.getItem(storageKey); } catch { return null; }
}

function writeStorage(storageKey, value) {
    try {
        if (value === null || value === undefined) localStorage.removeItem(storageKey);
        else localStorage.setItem(storageKey, value);
    } catch { }
}

function addElement(parentElement, tagName, className, text) {
    const newElement = document.createElement(tagName);
    if (className) newElement.className = className;
    if (text !== undefined && text !== null) newElement.textContent = text;
    parentElement.appendChild(newElement);
    return newElement;
}

function createElementBefore(beforeElement, tagName, className, text) {
    const newElement = document.createElement(tagName);
    if (className) newElement.className = className;
    if (text !== undefined && text !== null) newElement.textContent = text;
    beforeElement.parentElement.insertBefore(newElement, beforeElement);
    return newElement;
}

function formatTokens(tokenCount) {
    if (tokenCount === null || tokenCount === undefined) return '—';
    return tokenCount >= 1000 ? (tokenCount / 1000).toFixed(tokenCount >= 10000 ? 0 : 1) + 'K' : String(tokenCount);
}

function formatWhen(isoText) {
    const when = new Date(isoText);
    const isToday = when.toDateString() === new Date().toDateString();
    return isToday ? when.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : when.toLocaleDateString();
}

// "just now", "5 min ago", "3 h ago", "2 days ago", then the date.
function formatAge(isoText) {
    const ageInMinutes = (Date.now() - new Date(isoText).getTime()) / 60000;
    if (ageInMinutes < 1) return 'just now';
    if (ageInMinutes < 60) return Math.floor(ageInMinutes) + ' min ago';
    if (ageInMinutes < 1440) return Math.floor(ageInMinutes / 60) + ' h ago';
    const ageInDays = Math.floor(ageInMinutes / 1440);
    if (ageInDays < 30) return ageInDays === 1 ? '1 day ago' : ageInDays + ' days ago';
    return new Date(isoText).toLocaleDateString();
}

async function readErrorText(response) {
    const responseText = await response.text();
    try {
        const parsed = JSON.parse(responseText);
        return typeof parsed === 'string' ? parsed : parsed.detail || parsed.title || responseText;
    } catch {
        return responseText || ('error ' + response.status);
    }
}

function copyText(textToCopy) {
    if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(textToCopy);
    // Over plain http from another machine the clipboard API isn't available, so fall back to the old way.
    const helperTextarea = addElement(document.body, 'textarea');
    helperTextarea.value = textToCopy;
    helperTextarea.style.position = 'fixed';
    helperTextarea.style.opacity = '0';
    helperTextarea.select();
    document.execCommand('copy');
    helperTextarea.remove();
    return Promise.resolve();
}

// ---------- styled dialog (replaces the browser's confirm and alert) ----------

let openDialogBackdrop = null;

function isDialogOpen() {
    return openDialogBackdrop !== null;
}

/**
 * Shows a board K dialog and resolves true for the confirm button, false for cancel, Esc or a click outside.
 * options: { title, message, detail, confirmText, cancelText, danger }. Without cancelText it's a notice with one button.
 */
function showDialog(options) {
    return new Promise(resolve => {
        if (openDialogBackdrop) openDialogBackdrop.remove();
        const previousFocus = document.activeElement;
        const dialogBackdrop = addElement(document.body, 'div', 'dialog-backdrop');
        openDialogBackdrop = dialogBackdrop;
        const dialogPanel = addElement(dialogBackdrop, 'div', 'dialog cut' + (options.danger ? ' danger' : ''));
        dialogPanel.setAttribute('role', 'alertdialog');
        dialogPanel.setAttribute('aria-modal', 'true');
        const titleElement = addElement(dialogPanel, 'div', 'dialog-title label', options.title);
        titleElement.id = 'dialogTitle';
        dialogPanel.setAttribute('aria-labelledby', 'dialogTitle');
        if (options.message) addElement(dialogPanel, 'p', 'dialog-message', options.message);
        if (options.detail) addElement(dialogPanel, 'blockquote', 'dialog-detail', options.detail);

        const buttonRow = addElement(dialogPanel, 'div', 'dialog-buttons');
        const cancelButton = options.cancelText ? addElement(buttonRow, 'button', 'dialog-button label', options.cancelText) : null;
        const confirmButton = addElement(buttonRow, 'button', 'dialog-button primary cut-small label', options.confirmText || 'OK');
        if (cancelButton) cancelButton.type = 'button';
        confirmButton.type = 'button';

        function finish(confirmed) {
            document.removeEventListener('keydown', handleKey, true);
            dialogBackdrop.remove();
            if (openDialogBackdrop === dialogBackdrop) openDialogBackdrop = null;
            if (previousFocus && previousFocus.focus) previousFocus.focus();
            resolve(confirmed);
        }

        function handleKey(keyEvent) {
            if (keyEvent.key === 'Escape') {
                keyEvent.preventDefault();
                keyEvent.stopPropagation();
                finish(false);
            } else if (keyEvent.key === 'Tab') {
                // Keep focus inside the dialog.
                const focusableButtons = [cancelButton, confirmButton].filter(Boolean);
                const focusIndex = focusableButtons.indexOf(document.activeElement);
                keyEvent.preventDefault();
                focusableButtons[(focusIndex + (keyEvent.shiftKey ? -1 : 1) + focusableButtons.length) % focusableButtons.length].focus();
            }
        }

        document.addEventListener('keydown', handleKey, true);
        confirmButton.addEventListener('click', () => finish(true));
        if (cancelButton) cancelButton.addEventListener('click', () => finish(false));
        dialogBackdrop.addEventListener('click', clickEvent => {
            if (clickEvent.target === dialogBackdrop) finish(false);
        });
        // A dangerous action starts on Cancel, so a stray Enter doesn't delete anything.
        (options.danger && cancelButton ? cancelButton : confirmButton).focus();
    });
}

function showNotice(title, message) {
    return showDialog({ title, message, confirmText: 'OK' });
}

// ---------- model status ----------

function describeModelChip(state, tokensPerSecond) {
    const speedText = tokensPerSecond ? ' · ' + tokensPerSecond.toFixed(1) + ' TOK/S' : '';
    const chipTexts = { online: 'RHODES · ONLINE' + speedText, loading: 'RHODES · LOADING MODEL', offline: 'RHODES · MODEL OFFLINE', unreachable: 'RHODES · CORE OFFLINE' };
    return chipTexts[state] || 'RHODES · ' + state.toUpperCase();
}

/**
 * Asks /status every 5 seconds and passes { state, detail, tokensPerSecond } to onStatus.
 * "unreachable" means the core itself didn't answer.
 */
function startModelStatusPolling(onStatus) {
    async function pollOnce() {
        try {
            const response = await fetch('/status', { cache: 'no-store', signal: AbortSignal.timeout(3000) });
            const modelStatus = await response.json();
            onStatus({ state: modelStatus.state, detail: modelStatus.detail, tokensPerSecond: modelStatus.tokensPerSecond });
        } catch {
            onStatus({ state: 'unreachable', detail: "can't reach the Rhodes core", tokensPerSecond: null });
        }
    }

    const firstPoll = pollOnce();
    setInterval(pollOnce, 5000);
    return firstPoll;
}
