// Rhodes chat page: talks to the core's /chat/stream, /chats, /uploads and /status endpoints.
'use strict';

const page = document.getElementById('page');
const messagesElement = document.getElementById('messages');
const chatListElement = document.getElementById('chatList');
const chatSearchInput = document.getElementById('chatSearch');
const messageInput = document.getElementById('messageInput');
const messageForm = document.getElementById('messageForm');
const sendButton = document.getElementById('sendButton');
const stopButton = document.getElementById('stopButton');
const attachButton = document.getElementById('attachButton');
const fileInput = document.getElementById('fileInput');
const jumpButton = document.getElementById('jumpButton');
const conversationSection = document.getElementById('conversationSection');
const identityLine = document.getElementById('identityLine');
const modelChip = document.getElementById('modelChip');
const modelBanner = document.getElementById('modelBanner');
const editBanner = document.getElementById('editBanner');
const compactionBar = document.getElementById('compactionBar');
const compactionText = document.getElementById('compactionText');
const pendingAttachmentsElement = document.getElementById('pendingAttachments');
const drawerBackdrop = document.getElementById('drawerBackdrop');

const conversationStorageKey = 'rhodesConversationId';
const compactionStartFraction = 0.7;
const pastedTextAttachmentCharacters = 6000;
const baseDocumentTitle = 'Closure · Rhodes';
const markdownAvailable = typeof window.marked !== 'undefined' && typeof window.DOMPurify !== 'undefined';
const highlightingAvailable = typeof window.hljs !== 'undefined';

let currentConversationId = readStoredConversationId();
let thinkingSetting = 'auto';
let activeTurnController = null;
let compactionInProgress = false;
let rerenderAfterTurn = false;
let modelReady = false;
let editingLastMessage = false;
let renameInProgress = false;
let pendingAttachments = [];
let unreadReply = false;

if (markdownAvailable) {
    DOMPurify.addHook('afterSanitizeAttributes', node => {
        if (node.tagName === 'A') {
            node.setAttribute('target', '_blank');
            node.setAttribute('rel', 'noopener noreferrer');
        }
    });
}

if (highlightingAvailable) {
    hljs.configure({ ignoreUnescapedHTML: true });
}

// ---------- small helpers ----------

function readStoredConversationId() {
    try { return localStorage.getItem(conversationStorageKey); } catch { return null; }
}

function storeConversationId(conversationId) {
    currentConversationId = conversationId;
    try {
        if (conversationId) localStorage.setItem(conversationStorageKey, conversationId);
        else localStorage.removeItem(conversationStorageKey);
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

function isNearBottom() {
    return window.innerHeight + window.scrollY >= document.body.scrollHeight - 80;
}

function scrollToBottom() {
    window.scrollTo(0, document.body.scrollHeight);
    updateJumpButton();
}

function updateJumpButton() {
    jumpButton.hidden = isNearBottom() || messagesElement.childElementCount === 0;
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

// "14:02" today, "Fri 14:02" within a week, otherwise the date.
function formatSentTime(isoText) {
    if (!isoText) return '';
    const sentAt = new Date(isoText);
    const ageInDays = (Date.now() - sentAt.getTime()) / 86400000;
    const timeText = sentAt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    if (sentAt.toDateString() === new Date().toDateString()) return timeText;
    return ageInDays < 7 ? sentAt.toLocaleDateString([], { weekday: 'short' }) + ' ' + timeText : sentAt.toLocaleDateString();
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

// The core sends injected memories as a JSON array, because one memory can span several lines.
function parseMemoryList(memoriesText) {
    try {
        const parsed = JSON.parse(memoriesText);
        if (Array.isArray(parsed)) return parsed;
    } catch { }
    return memoriesText.split('\n');
}

// ---------- file viewer ----------

const languageByExtension = {
    cs: 'csharp', xml: 'xml', xaml: 'xml', csproj: 'xml', html: 'xml', json: 'json', ps1: 'powershell',
    js: 'javascript', ts: 'typescript', py: 'python', cpp: 'cpp', h: 'cpp', c: 'c', yaml: 'yaml', yml: 'yaml',
    ini: 'ini', cfg: 'ini', toml: 'ini', md: 'markdown', sql: 'sql', sh: 'bash', css: 'css',
};
const maximumHighlightedCharacters = 200000;
let fileViewer = null;

function closeFileViewer() {
    if (!fileViewer) return false;
    fileViewer.remove();
    fileViewer = null;
    return true;
}

async function openFileViewer(uploadId, fileName, estimatedTokens) {
    closeFileViewer();
    const viewerBackdrop = addElement(document.body, 'div', 'file-viewer-backdrop');
    fileViewer = viewerBackdrop;
    const viewerPanel = addElement(viewerBackdrop, 'div', 'file-viewer cut');
    viewerPanel.setAttribute('role', 'dialog');
    viewerPanel.setAttribute('aria-label', fileName);
    const viewerHeader = addElement(viewerPanel, 'div', 'file-viewer-header');
    addElement(viewerHeader, 'span', 'file-viewer-name mono', fileName);
    addElement(viewerHeader, 'span', 'file-viewer-size mono', estimatedTokens ? formatTokens(estimatedTokens) + ' TOK' : '');
    const copyButton = addElement(viewerHeader, 'button', 'turn-action', 'COPY');
    copyButton.type = 'button';
    const closeButton = addElement(viewerHeader, 'button', 'file-viewer-close', '×');
    closeButton.type = 'button';
    closeButton.setAttribute('aria-label', 'Close');
    const viewerBody = addElement(viewerPanel, 'pre', 'file-viewer-body');
    const viewerCode = addElement(viewerBody, 'code', null, 'loading…');

    viewerBackdrop.addEventListener('click', clickEvent => {
        if (clickEvent.target === viewerBackdrop) closeFileViewer();
    });
    closeButton.addEventListener('click', closeFileViewer);

    let fileText = '';
    try {
        const response = await fetch('/uploads/' + encodeURIComponent(uploadId));
        fileText = response.ok ? await response.text() : '';
        if (fileViewer !== viewerBackdrop) return;
        if (!response.ok) {
            viewerCode.textContent = "This file isn't stored on the core anymore.";
            return;
        }
    } catch (fetchError) {
        if (fileViewer === viewerBackdrop) viewerCode.textContent = "Couldn't load the file: " + fetchError.message;
        return;
    }

    const extension = fileName.includes('.') ? fileName.split('.').pop().toLowerCase() : '';
    const language = languageByExtension[extension];
    if (highlightingAvailable && language && hljs.getLanguage(language) && fileText.length <= maximumHighlightedCharacters) {
        viewerCode.innerHTML = hljs.highlight(fileText, { language }).value;
    } else {
        viewerCode.textContent = fileText;
    }
    copyButton.addEventListener('click', async () => {
        await copyText(fileText);
        copyButton.textContent = 'COPIED';
        setTimeout(() => { copyButton.textContent = 'COPY'; }, 1200);
    });
}

// The file name in a chip opens the file, when the core has it stored (it has an id).
function addFileName(chipElement, attachment) {
    if (!attachment.id) return addElement(chipElement, 'span', 'file-name', attachment.fileName);
    const fileButton = addElement(chipElement, 'button', 'file-name file-open', attachment.fileName);
    fileButton.type = 'button';
    fileButton.title = 'Open ' + attachment.fileName;
    fileButton.addEventListener('click', clickEvent => {
        clickEvent.stopPropagation();
        openFileViewer(attachment.id, attachment.fileName, attachment.estimatedTokens);
    });
    return fileButton;
}

// ---------- markdown, code blocks, highlighting ----------

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

function decorateCodeBlock(preElement) {
    const codeElement = preElement.querySelector('code');
    if (codeElement && highlightingAvailable) hljs.highlightElement(codeElement);
    const copyButton = addElement(preElement, 'button', 'copy-button', 'COPY');
    copyButton.type = 'button';
    copyButton.addEventListener('click', async () => {
        await copyText((codeElement || preElement).innerText);
        copyButton.textContent = 'COPIED';
        setTimeout(() => { copyButton.textContent = 'COPY'; }, 1200);
    });
}

function renderAnswer(answer) {
    answer.renderScheduled = false;
    if (!markdownAvailable) {
        answer.element.textContent = answer.rawText;
        return;
    }
    answer.element.innerHTML = DOMPurify.sanitize(marked.parse(answer.rawText, { gfm: true, breaks: true }));
    answer.element.querySelectorAll('pre').forEach(decorateCodeBlock);
}

// While streaming, re-render at most once per frame instead of on every token.
function scheduleAnswerRender(answer) {
    if (answer.renderScheduled) return;
    answer.renderScheduled = true;
    requestAnimationFrame(() => {
        const wasNearBottom = isNearBottom();
        renderAnswer(answer);
        if (wasNearBottom) scrollToBottom();
        else updateJumpButton();
    });
}

function createAnswer(parentElement, beforeElement, rawText) {
    const className = 'text closure' + (markdownAvailable ? ' markdown' : '');
    const answerElement = beforeElement ? createElementBefore(beforeElement, 'div', className) : addElement(parentElement, 'div', className);
    const answer = { element: answerElement, rawText: rawText || '', renderScheduled: false };
    if (answer.rawText) renderAnswer(answer);
    return answer;
}

// ---------- message building ----------

function addAttachmentChips(parentElement, attachments) {
    const attachmentRow = addElement(parentElement, 'div', 'attachment-row');
    for (const attachment of attachments) {
        const tooBig = attachment.inline === false;
        const chipElement = addElement(attachmentRow, 'span', 'attachment-chip static' + (tooBig ? ' too-big' : ''));
        addFileName(chipElement, attachment);
        const sizeText = attachment.estimatedTokens !== undefined ? formatTokens(attachment.estimatedTokens) + ' TOK' : '';
        addElement(chipElement, 'span', 'file-size', tooBig ? sizeText + ' · TOO BIG TO READ YET' : sizeText);
    }
    return attachmentRow;
}

function createUserMessage(parentElement, messageText, attachments, sentAt, isSummarized) {
    const messageElement = addElement(parentElement, 'div', 'message from-user' + (isSummarized ? ' summarized' : ''));
    messageElement.messageText = messageText;
    messageElement.attachments = attachments || [];
    addElement(messageElement, 'div', 'avatar user cut-small label', 'S');
    const bodyElement = addElement(messageElement, 'div', 'message-body');
    const nameLine = addElement(bodyElement, 'div');
    addElement(nameLine, 'span', 'name user label', 'SHAPTO');
    const sentTimeElement = addElement(nameLine, 'span', 'sent-time mono', formatSentTime(sentAt));
    if (sentAt) sentTimeElement.title = new Date(sentAt).toLocaleString();
    if (messageText) addElement(bodyElement, 'p', 'text user', messageText);
    const attachmentRow = messageElement.attachments.length > 0 ? addAttachmentChips(bodyElement, messageElement.attachments) : null;
    return { messageElement, bodyElement, attachmentRow };
}

function createClosureMessage(parentElement, isSummarized) {
    const messageElement = addElement(parentElement, 'div', 'message from-closure' + (isSummarized ? ' summarized' : ''));
    addElement(messageElement, 'div', 'avatar closure cut-small label', 'C');
    const bodyElement = addElement(messageElement, 'div', 'message-body');
    const nameLine = addElement(bodyElement, 'div');
    addElement(nameLine, 'span', 'name closure label', 'CLOSURE');
    const sentTimeElement = addElement(nameLine, 'span', 'sent-time mono');
    const metaLine = addElement(nameLine, 'span', 'meta-line mono');
    const workedDetails = addElement(bodyElement, 'details', 'worked');
    const workedSummary = addElement(workedDetails, 'summary', null, 'THINKING');
    const thinkingText = addElement(workedDetails, 'div', 'thinking-text');
    workedDetails.hidden = true;
    return { messageElement, bodyElement, sentTimeElement, metaLine, workedDetails, workedSummary, thinkingText, toolCount: 0, thinkingLevelText: '' };
}

function showSentTime(sentTimeElement, sentAt) {
    sentTimeElement.textContent = formatSentTime(sentAt);
    if (sentAt) sentTimeElement.title = new Date(sentAt).toLocaleString();
}

// "thinking light · 2.1 s · 312 tok · 33.6 t/s" next to her name.
function describeStatistics(statistics, thinkingLevelText) {
    const parts = [];
    if (thinkingLevelText) parts.push('thinking ' + thinkingLevelText);
    if (statistics) {
        parts.push(statistics.seconds.toFixed(1) + ' s');
        parts.push(statistics.generatedTokens.toLocaleString() + ' tok');
        if (statistics.tokensPerSecond) parts.push(statistics.tokensPerSecond.toFixed(1) + ' t/s');
    }
    return parts.join(' · ');
}

function updateWorkedSummary(closureMessage) {
    const parts = [];
    if (closureMessage.thinkingText.textContent.length > 0) parts.push('THOUGHT · ' + closureMessage.thinkingText.textContent.length.toLocaleString() + ' CHARS');
    if (closureMessage.toolCount > 0) parts.push(closureMessage.toolCount + (closureMessage.toolCount === 1 ? ' TOOL' : ' TOOLS'));
    closureMessage.workedSummary.textContent = parts.join(' · ') || 'THINKING';
    closureMessage.workedDetails.hidden = parts.length === 0;
}

function addToolStep(closureMessage, toolName, argumentsText, resultText) {
    const stepElement = addElement(closureMessage.workedDetails, 'div', 'tool-step');
    addElement(stepElement, 'span', 'tool-chip', toolName + (argumentsText ? ' ' + argumentsText : ''));
    addElement(stepElement, 'span', null, '\n→ ' + resultText);
    closureMessage.toolCount++;
    updateWorkedSummary(closureMessage);
}

function addMemoryChip(closureMessage, memoryLine) {
    const chipElement = createElementBefore(closureMessage.workedDetails, 'span', 'memory-chip');
    addElement(chipElement, 'span', 'dot', '● ');
    chipElement.appendChild(document.createTextNode('RECALLED · ' + (memoryLine.length > 90 ? memoryLine.slice(0, 90) + '…' : memoryLine)));
}

function addCompactionSeparator(parentElement, summaryText, coveredMessageCount) {
    const separatorElement = addElement(parentElement, 'div', 'separator');
    separatorElement.setAttribute('role', 'separator');
    const separatorLabel = addElement(separatorElement, 'span', null, coveredMessageCount + ' EARLIER MESSAGES SUMMARIZED · ');
    const viewButton = addElement(separatorLabel, 'button', null, 'VIEW');
    viewButton.type = 'button';
    const summaryPanel = addElement(parentElement, 'div', 'summary-panel cut', summaryText || '(only old tool results were collapsed)');
    summaryPanel.hidden = true;
    viewButton.addEventListener('click', () => {
        summaryPanel.hidden = !summaryPanel.hidden;
        viewButton.textContent = summaryPanel.hidden ? 'VIEW' : 'HIDE';
    });
}

// ---------- regenerate / edit (the last turn only) ----------

function lastMessageElement(className) {
    const matchingElements = messagesElement.querySelectorAll('.message.' + className);
    return matchingElements.length > 0 ? matchingElements[matchingElements.length - 1] : null;
}

function canChangeLastTurn() {
    const lastUserElement = lastMessageElement('from-user');
    return lastUserElement !== null && !lastUserElement.classList.contains('summarized') && currentConversationId !== null;
}

function refreshTurnActions() {
    messagesElement.querySelectorAll('.turn-actions').forEach(actionRow => actionRow.remove());
    if (!canChangeLastTurn() || activeTurnController || compactionInProgress) return;

    const lastUserElement = lastMessageElement('from-user');
    const lastClosureElement = lastMessageElement('from-closure');
    const busy = !modelReady;

    const editRow = addElement(lastUserElement.querySelector('.message-body'), 'div', 'turn-actions');
    const editButton = addElement(editRow, 'button', 'turn-action', '✎ EDIT');
    editButton.type = 'button';
    editButton.title = 'Edit your last message and get a new reply (↑ in an empty box)';
    editButton.addEventListener('click', startEditingLastMessage);

    if (lastClosureElement && lastClosureElement.compareDocumentPosition(lastUserElement) & Node.DOCUMENT_POSITION_PRECEDING) {
        const regenerateRow = addElement(lastClosureElement.querySelector('.message-body'), 'div', 'turn-actions');
        const regenerateButton = addElement(regenerateRow, 'button', 'turn-action', '↻ REGENERATE');
        regenerateButton.type = 'button';
        regenerateButton.disabled = busy;
        regenerateButton.title = 'Throw this reply away and ask again';
        regenerateButton.addEventListener('click', () => regenerateLastTurn(null));
    }
}

function startEditingLastMessage() {
    if (!canChangeLastTurn() || activeTurnController || compactionInProgress) return;
    const lastUserElement = lastMessageElement('from-user');
    editingLastMessage = true;
    editBanner.hidden = false;
    messageInput.value = lastUserElement.messageText || '';
    resizeMessageInput();
    messageInput.focus();
    messageInput.setSelectionRange(messageInput.value.length, messageInput.value.length);
    updateFormState();
}

function stopEditingLastMessage(clearInput) {
    editingLastMessage = false;
    editBanner.hidden = true;
    if (clearInput) {
        messageInput.value = '';
        resizeMessageInput();
    }
    updateFormState();
}

// Removes the last turn from the page and asks the core to run it again; with editedText, it's an edit.
function regenerateLastTurn(editedText) {
    if (!canChangeLastTurn() || activeTurnController || compactionInProgress || !modelReady) return;
    const lastUserElement = lastMessageElement('from-user');
    const messageText = editedText !== null ? editedText : lastUserElement.messageText;
    const attachments = lastUserElement.attachments;
    while (lastUserElement.nextSibling) lastUserElement.nextSibling.remove();
    lastUserElement.remove();
    sendMessage(messageText, attachments, { regenerate: true, editedText });
}

// ---------- context and readouts ----------

function showContext(contextTokens, contextLimit, contextSplit) {
    const meters = document.querySelectorAll('[data-context-meter]');
    const texts = document.querySelectorAll('[data-context-text]');
    const usedFraction = contextLimit && contextTokens ? Math.min(1, contextTokens / contextLimit) : 0;
    const split = contextSplit || (contextTokens ? { system: 0, memory: 0, chat: contextTokens } : { system: 0, memory: 0, chat: 0 });

    for (const meter of meters) {
        const segmentWidths = {
            system: contextLimit ? split.system / contextLimit * 100 : 0,
            memory: contextLimit ? split.memory / contextLimit * 100 : 0,
            chat: contextLimit ? split.chat / contextLimit * 100 : 0,
        };
        for (const [segmentName, widthPercent] of Object.entries(segmentWidths)) {
            meter.querySelector('.context-segment.' + segmentName).style.width = Math.min(100, widthPercent).toFixed(1) + '%';
        }
        meter.classList.toggle('near-compaction', usedFraction >= compactionStartFraction * 0.85);
    }

    const totalText = contextLimit ? (contextTokens ? formatTokens(contextTokens) : '—') + ' / ' + formatTokens(contextLimit) : '—';
    texts.forEach(textElement => { textElement.textContent = totalText; });
    document.getElementById('readoutContextSystem').textContent = contextSplit ? '≈' + formatTokens(contextSplit.system) : '—';
    document.getElementById('readoutContextMemory').textContent = contextSplit ? '≈' + formatTokens(contextSplit.memory) : '—';
    document.getElementById('readoutContextChat').textContent = contextSplit ? '≈' + formatTokens(contextSplit.chat) : '—';
}

function showReplyStatistics(statistics) {
    const readoutValues = {
        readoutPrompt: statistics && statistics.promptTokens !== null ? statistics.promptTokens.toLocaleString() : '—',
        readoutCached: statistics && statistics.cachedTokens !== null ? statistics.cachedTokens.toLocaleString() + (statistics.promptTokens ? ' (' + Math.round(statistics.cachedTokens / statistics.promptTokens * 100) + '%)' : '') : '—',
        readoutGenerated: statistics ? statistics.generatedTokens.toLocaleString() : '—',
        readoutReplySpeed: statistics && statistics.tokensPerSecond ? statistics.tokensPerSecond.toFixed(1) + ' t/s' : '—',
        readoutTime: statistics ? statistics.seconds.toFixed(1) + ' s' : '—',
        readoutCalls: statistics ? String(statistics.modelCalls) : '—',
    };
    for (const [elementId, valueText] of Object.entries(readoutValues)) {
        document.getElementById(elementId).textContent = valueText;
    }
}

function resetTurnReadout() {
    document.getElementById('readoutTurnEmpty').hidden = false;
    document.getElementById('readoutTurnEmpty').textContent = 'nothing recalled, saved or used';
    document.getElementById('readoutMemories').replaceChildren();
    document.getElementById('readoutTools').replaceChildren();
}

function addTurnMemory(memoryText, wasSaved) {
    document.getElementById('readoutTurnEmpty').hidden = true;
    const memoryItem = addElement(document.getElementById('readoutMemories'), 'li', wasSaved ? 'saved' : null, (wasSaved ? 'SAVED · ' : 'RECALLED · ') + memoryText);
    memoryItem.title = memoryText;
}

function addTurnTool(toolName) {
    document.getElementById('readoutTurnEmpty').hidden = true;
    addElement(document.getElementById('readoutTools'), 'span', 'tool-chip', toolName);
}

// ---------- chat list, search, rename, delete ----------

function showTitle(titleText) {
    identityLine.textContent = titleText || 'new chat';
}

function applyChatFilter() {
    const searchText = chatSearchInput.value.trim().toLowerCase();
    let visibleEntries = 0;
    let groupLabel = null;
    let groupHasVisibleEntry = false;
    for (const listItem of chatListElement.children) {
        if (listItem.classList.contains('group-label')) {
            if (groupLabel) groupLabel.hidden = !groupHasVisibleEntry;
            groupLabel = listItem;
            groupHasVisibleEntry = false;
            continue;
        }
        if (!listItem.classList.contains('chat-entry')) continue;
        const matches = searchText.length === 0 || (listItem.chatTitle || '').toLowerCase().includes(searchText);
        listItem.hidden = !matches;
        if (matches) {
            visibleEntries++;
            groupHasVisibleEntry = true;
        }
    }
    if (groupLabel) groupLabel.hidden = !groupHasVisibleEntry;

    let emptyNote = chatListElement.querySelector('.list-empty');
    if (visibleEntries === 0 && !emptyNote) emptyNote = addElement(chatListElement, 'div', 'list-empty');
    if (emptyNote) {
        emptyNote.hidden = visibleEntries > 0;
        emptyNote.textContent = searchText ? 'no chat titles match' : 'no chats yet';
    }
}

async function refreshChatList() {
    if (renameInProgress) return;
    let chatSummaries;
    try {
        const response = await fetch('/chats');
        if (!response.ok) return;
        chatSummaries = await response.json();
    } catch {
        return;
    }

    const listFragment = document.createDocumentFragment();
    let currentGroup = null;
    for (const chatSummary of chatSummaries) {
        const isToday = new Date(chatSummary.updatedAt).toDateString() === new Date().toDateString();
        const groupName = isToday ? 'TODAY' : 'EARLIER';
        if (groupName !== currentGroup) {
            addElement(listFragment, 'div', 'group-label mono', groupName);
            currentGroup = groupName;
        }

        const isOpenChat = chatSummary.id === currentConversationId;
        const chatEntry = addElement(listFragment, 'div', 'chat-entry' + (isOpenChat ? ' active' : ''));
        chatEntry.chatTitle = chatSummary.title || 'untitled';
        chatEntry.setAttribute('role', 'button');
        chatEntry.tabIndex = 0;
        if (isOpenChat) chatEntry.title = 'Double-click to rename';
        addElement(chatEntry, 'span', 'chat-title', chatEntry.chatTitle);
        addElement(chatEntry, 'span', 'chat-meta', chatSummary.messageCount + ' msg · ' + formatWhen(chatSummary.updatedAt) + (chatSummary.compacted ? ' · compacted' : ''));

        const deleteButton = addElement(chatEntry, 'button', 'chat-delete', '×');
        deleteButton.type = 'button';
        deleteButton.title = 'Delete this chat';
        deleteButton.setAttribute('aria-label', 'Delete ' + chatEntry.chatTitle);
        deleteButton.addEventListener('click', clickEvent => {
            clickEvent.stopPropagation();
            deleteChat(chatSummary);
        });

        // Clicking the open chat does nothing, so the first click of a double-click doesn't rebuild the list under the rename box.
        chatEntry.addEventListener('click', () => {
            if (!isOpenChat) openConversation(chatSummary.id);
        });
        chatEntry.addEventListener('keydown', keyEvent => {
            if (keyEvent.target === chatEntry && (keyEvent.key === 'Enter' || keyEvent.key === ' ')) {
                keyEvent.preventDefault();
                if (!isOpenChat) openConversation(chatSummary.id);
            }
        });
        chatEntry.addEventListener('dblclick', () => {
            if (isOpenChat) startRename(chatEntry, chatSummary);
        });
        if (isOpenChat) showTitle(chatSummary.title);
    }
    chatListElement.replaceChildren(listFragment);
    applyChatFilter();
}

function startRename(chatEntry, chatSummary) {
    const titleElement = chatEntry.querySelector('.chat-title');
    if (!titleElement || renameInProgress) return;
    renameInProgress = true;
    const renameInput = document.createElement('input');
    renameInput.className = 'rename-input';
    renameInput.value = chatSummary.title || '';
    renameInput.maxLength = 60;
    titleElement.replaceWith(renameInput);
    renameInput.focus();
    renameInput.select();

    let renameFinished = false;
    async function finishRename(saveTitle) {
        if (renameFinished) return;
        renameFinished = true;
        renameInProgress = false;
        const newTitle = renameInput.value.trim();
        if (saveTitle && newTitle && newTitle !== chatSummary.title) {
            await fetch('/chats/' + chatSummary.id + '/title', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ title: newTitle }),
            });
        }
        refreshChatList();
    }

    renameInput.addEventListener('click', clickEvent => clickEvent.stopPropagation());
    renameInput.addEventListener('dblclick', clickEvent => clickEvent.stopPropagation());
    renameInput.addEventListener('keydown', keyEvent => {
        keyEvent.stopPropagation();
        if (keyEvent.key === 'Enter') finishRename(true);
        if (keyEvent.key === 'Escape') finishRename(false);
    });
    renameInput.addEventListener('blur', () => finishRename(true));
}

async function deleteChat(chatSummary) {
    if (!confirm('Delete "' + (chatSummary.title || 'untitled') + '"? This can\'t be undone.')) return;
    const response = await fetch('/chats/' + chatSummary.id, { method: 'DELETE' });
    if (!response.ok && response.status !== 404) {
        alert(await readErrorText(response));
        return;
    }
    if (chatSummary.id === currentConversationId) startNewChat();
    else refreshChatList();
}

function startNewChat() {
    if (activeTurnController || compactionInProgress) return;
    storeConversationId(null);
    stopEditingLastMessage(false);
    messagesElement.replaceChildren();
    showTitle(null);
    showContext(null, null, null);
    showReplyStatistics(null);
    refreshChatList();
    closeDrawers();
    updateJumpButton();
    messageInput.focus();
}

// ---------- loading a chat (built off-screen, swapped in at once) ----------

async function fetchConversationView(conversationId) {
    const response = await fetch('/chats/' + conversationId);
    return response.ok ? await response.json() : null;
}

function buildConversationView(conversationView) {
    const newMessagesContainer = document.createElement('div');
    const summaryBoundary = conversationView.summaryCoversThrough;
    let separatorPlaced = summaryBoundary === 0;
    let closureMessage = null;
    let lastStatistics = null;

    for (const displayMessage of conversationView.messages) {
        if (!separatorPlaced && displayMessage.index >= summaryBoundary) {
            addCompactionSeparator(newMessagesContainer, conversationView.summary, summaryBoundary);
            separatorPlaced = true;
            closureMessage = null;
        }
        const isSummarized = displayMessage.index < summaryBoundary;
        if (displayMessage.role === 'user') {
            createUserMessage(newMessagesContainer, displayMessage.text, displayMessage.attachments, displayMessage.sentAt, isSummarized);
            closureMessage = null;
            continue;
        }
        if (!closureMessage) closureMessage = createClosureMessage(newMessagesContainer, isSummarized);
        if (displayMessage.role === 'tool') {
            addToolStep(closureMessage, displayMessage.toolName || 'tool', '', displayMessage.text.length > 300 ? displayMessage.text.slice(0, 300) + '…' : displayMessage.text);
        } else if (displayMessage.role === 'assistant') {
            if (displayMessage.reasoning) {
                closureMessage.thinkingText.textContent += (closureMessage.thinkingText.textContent ? '\n\n' : '') + displayMessage.reasoning;
                updateWorkedSummary(closureMessage);
            }
            if (displayMessage.text) createAnswer(closureMessage.bodyElement, null, displayMessage.text);
            if (displayMessage.sentAt) showSentTime(closureMessage.sentTimeElement, displayMessage.sentAt);
            if (displayMessage.statistics) {
                closureMessage.metaLine.textContent = describeStatistics(displayMessage.statistics, '');
                lastStatistics = displayMessage.statistics;
            }
        }
    }
    if (!separatorPlaced) addCompactionSeparator(newMessagesContainer, conversationView.summary, summaryBoundary);
    return { newMessagesContainer, lastStatistics };
}

async function loadConversation(conversationId) {
    const conversationView = await fetchConversationView(conversationId);
    if (!conversationView) {
        storeConversationId(null);
        messagesElement.replaceChildren();
        showTitle(null);
        showContext(null, null, null);
        return;
    }
    const { newMessagesContainer, lastStatistics } = buildConversationView(conversationView);
    messagesElement.replaceChildren(...newMessagesContainer.childNodes);
    showTitle(conversationView.title);
    showContext(conversationView.contextTokens, conversationView.contextLimit, conversationView.contextSplit);
    showReplyStatistics(lastStatistics);
    refreshTurnActions();
    scrollToBottom();
}

async function openConversation(conversationId) {
    if (activeTurnController || compactionInProgress) return;
    stopEditingLastMessage(true);
    storeConversationId(conversationId);
    closeDrawers();
    await loadConversation(conversationId);
    refreshChatList();
}

// ---------- form state ----------

function resizeMessageInput() {
    messageInput.style.height = 'auto';
    messageInput.style.height = Math.min(messageInput.scrollHeight + 2, 260) + 'px';
}

function updateFormState() {
    const uploadsRunning = pendingAttachments.some(attachment => attachment.status === 'uploading');
    messageInput.disabled = compactionInProgress;
    attachButton.disabled = compactionInProgress || editingLastMessage;
    sendButton.disabled = compactionInProgress || !modelReady || activeTurnController !== null || uploadsRunning;
    sendButton.textContent = editingLastMessage ? 'RESEND' : 'SEND';
    stopButton.hidden = activeTurnController === null;
    document.title = activeTurnController ? '● Closure is writing…' : unreadReply ? '✓ Closure replied' : baseDocumentTitle;
    refreshTurnActions();
}

function showCompaction(progressText) {
    compactionInProgress = true;
    compactionBar.hidden = false;
    compactionText.textContent = progressText;
    updateFormState();
}

function hideCompaction() {
    compactionInProgress = false;
    compactionBar.hidden = true;
    updateFormState();
}

// ---------- model status ----------

function showModelStatus(state, detail, tokensPerSecond) {
    modelChip.dataset.state = state;
    const speedText = tokensPerSecond ? ' · ' + tokensPerSecond.toFixed(1) + ' TOK/S' : '';
    const chipTexts = { online: 'RHODES · ONLINE' + speedText, loading: 'RHODES · LOADING MODEL', offline: 'RHODES · MODEL OFFLINE', unreachable: 'RHODES · CORE OFFLINE' };
    modelChip.textContent = chipTexts[state] || 'RHODES · ' + state.toUpperCase();
    modelChip.title = detail || '';

    document.getElementById('readoutCore').textContent = state === 'unreachable' ? 'offline' : 'online';
    document.getElementById('readoutModel').textContent = state === 'unreachable' ? '—' : state;
    document.getElementById('readoutModel').title = detail || '';
    document.getElementById('readoutSpeed').textContent = tokensPerSecond ? tokensPerSecond.toFixed(1) + ' t/s' : '—';

    const wasReady = modelReady;
    modelReady = state === 'online';
    modelBanner.hidden = modelReady;
    modelBanner.classList.toggle('loading', state === 'loading');
    const bannerTexts = {
        loading: "Closure's model is loading. Sending comes back on its own when it's ready.",
        offline: "Closure's model server isn't reachable (" + detail + "). You can keep typing; sending comes back on its own.",
        unreachable: "Can't reach the Rhodes core. Is it running?",
    };
    modelBanner.textContent = bannerTexts[state] || '';
    if (wasReady !== modelReady) updateFormState();
    else sendButton.disabled = compactionInProgress || !modelReady || activeTurnController !== null || pendingAttachments.some(attachment => attachment.status === 'uploading');
}

async function pollModelStatus() {
    try {
        const response = await fetch('/status', { cache: 'no-store', signal: AbortSignal.timeout(3000) });
        const modelStatus = await response.json();
        showModelStatus(modelStatus.state, modelStatus.detail, modelStatus.tokensPerSecond);
    } catch {
        showModelStatus('unreachable', "can't reach the Rhodes core", null);
    }
}

// ---------- attachments ----------

function renderPendingAttachments() {
    pendingAttachmentsElement.hidden = pendingAttachments.length === 0;
    const chipsFragment = document.createDocumentFragment();
    for (const attachment of pendingAttachments) {
        const chipElement = addElement(chipsFragment, 'span', 'attachment-chip ' + attachment.status);
        addFileName(chipElement, attachment);
        const sizeTexts = { uploading: 'UPLOADING…', ready: formatTokens(attachment.estimatedTokens || 0) + ' TOK', error: attachment.errorText };
        addElement(chipElement, 'span', 'file-size', sizeTexts[attachment.status]);
        const removeButton = addElement(chipElement, 'button', 'chip-remove', '×');
        removeButton.type = 'button';
        removeButton.setAttribute('aria-label', 'Remove ' + attachment.fileName);
        removeButton.addEventListener('click', () => {
            pendingAttachments = pendingAttachments.filter(otherAttachment => otherAttachment !== attachment);
            renderPendingAttachments();
        });
    }
    pendingAttachmentsElement.replaceChildren(chipsFragment);
    updateFormState();
}

async function uploadFiles(files) {
    for (const file of files) {
        const attachment = { fileName: file.name, status: 'uploading' };
        pendingAttachments.push(attachment);
        renderPendingAttachments();

        const formData = new FormData();
        formData.append('file', file);
        try {
            const response = await fetch('/uploads', { method: 'POST', body: formData });
            if (response.ok) {
                const uploadRecord = await response.json();
                Object.assign(attachment, { status: 'ready', id: uploadRecord.id, fileName: uploadRecord.fileName, estimatedTokens: uploadRecord.estimatedTokens });
            } else {
                Object.assign(attachment, { status: 'error', errorText: await readErrorText(response) });
            }
        } catch (uploadError) {
            Object.assign(attachment, { status: 'error', errorText: uploadError.message });
        }
        renderPendingAttachments();
    }
}

// ---------- tab title and notifications ----------

function askForNotificationsOnce() {
    if (typeof Notification === 'undefined' || !window.isSecureContext || Notification.permission !== 'default') return;
    Notification.requestPermission().catch(() => { });
}

function announceReplyIfHidden(replyText) {
    if (!document.hidden) return;
    unreadReply = true;
    updateFormState();
    if (typeof Notification !== 'undefined' && Notification.permission === 'granted') {
        const preview = replyText.replace(/\s+/g, ' ').slice(0, 140);
        const notification = new Notification('Closure replied', { body: preview, tag: 'rhodes-reply' });
        notification.onclick = () => { window.focus(); notification.close(); };
    }
}

document.addEventListener('visibilitychange', () => {
    if (!document.hidden && unreadReply) {
        unreadReply = false;
        updateFormState();
    }
});

// ---------- sending a message ----------

async function sendMessage(messageText, attachmentsToSend, options) {
    const regenerate = options && options.regenerate;
    const attachmentIds = attachmentsToSend.filter(attachment => attachment.id).map(attachment => attachment.id);
    const userMessage = createUserMessage(messagesElement, messageText, attachmentsToSend, new Date().toISOString(), false);
    const closureMessage = createClosureMessage(messagesElement, false);
    const statusLine = addElement(closureMessage.bodyElement, 'div', 'status', regenerate ? 'regenerating…' : 'waiting…');
    let answer = null;
    let turnConversationId = currentConversationId;
    let answerFinished = false;
    let compactedDuringTurn = false;
    let turnFailed = false;
    const turnController = new AbortController();
    activeTurnController = turnController;
    askForNotificationsOnce();
    resetTurnReadout();
    updateFormState();
    scrollToBottom();

    function currentAnswer() {
        if (!answer) answer = createAnswer(closureMessage.bodyElement, statusLine, '');
        return answer;
    }

    function finishAnswer() {
        if (activeTurnController === turnController) activeTurnController = null;
        updateFormState();
        if (rerenderAfterTurn && turnConversationId === currentConversationId) {
            rerenderAfterTurn = false;
            loadConversation(currentConversationId);
        }
    }

    function handleEvent(chatEvent) {
        const wasNearBottom = isNearBottom();
        const isCurrentChat = turnConversationId === currentConversationId;
        switch (chatEvent.type) {
            case 'conversation':
                turnConversationId = chatEvent.text;
                if (!currentConversationId) storeConversationId(chatEvent.text);
                break;
            case 'thinkingLevel':
                closureMessage.thinkingLevelText = chatEvent.text;
                closureMessage.metaLine.textContent = 'thinking ' + chatEvent.text;
                break;
            case 'memories':
                for (const memoryLine of parseMemoryList(chatEvent.text)) {
                    addMemoryChip(closureMessage, memoryLine);
                    addTurnMemory(memoryLine, false);
                }
                break;
            case 'attachments':
                if (userMessage.attachmentRow) userMessage.attachmentRow.remove();
                userMessage.messageElement.attachments = JSON.parse(chatEvent.text);
                userMessage.attachmentRow = addAttachmentChips(userMessage.bodyElement, userMessage.messageElement.attachments);
                break;
            case 'reasoning':
                closureMessage.thinkingText.textContent += chatEvent.text;
                updateWorkedSummary(closureMessage);
                statusLine.textContent = 'thinking… ' + closureMessage.thinkingText.textContent.length.toLocaleString() + ' chars';
                break;
            case 'content':
                currentAnswer().rawText += chatEvent.text;
                scheduleAnswerRender(answer);
                statusLine.textContent = 'writing…';
                break;
            case 'moveToReasoning':
                if (answer) {
                    closureMessage.thinkingText.textContent += answer.rawText;
                    answer.rawText = '';
                    renderAnswer(answer);
                    updateWorkedSummary(closureMessage);
                }
                break;
            case 'status':
                // Before the answer is done, a status is about the turn (a tool running).
                // After it, the only statuses left are compaction's.
                if (answerFinished) {
                    if (isCurrentChat) showCompaction(chatEvent.text);
                } else {
                    statusLine.hidden = false;
                    statusLine.textContent = chatEvent.text;
                }
                break;
            case 'tool':
                addToolStep(closureMessage, chatEvent.toolName, chatEvent.text, chatEvent.result);
                addTurnTool(chatEvent.toolName);
                if (chatEvent.toolName === 'save_memory' && !String(chatEvent.result).startsWith('Error')) {
                    let savedContent = chatEvent.text;
                    try { savedContent = JSON.parse(chatEvent.text).content || savedContent; } catch { }
                    addTurnMemory(savedContent, true);
                }
                if (answer) renderAnswer(answer);
                answer = null;
                break;
            case 'done':
                answerFinished = true;
                closureMessage.metaLine.textContent = describeStatistics(chatEvent.statistics, closureMessage.thinkingLevelText);
                showSentTime(closureMessage.sentTimeElement, chatEvent.sentAt);
                statusLine.hidden = true;
                if (answer) renderAnswer(answer);
                if (isCurrentChat) {
                    showContext(chatEvent.contextTokens, chatEvent.contextLimit, chatEvent.contextSplit);
                    showReplyStatistics(chatEvent.statistics);
                }
                announceReplyIfHidden(answer ? answer.rawText : '');
                refreshChatList();
                finishAnswer();
                break;
            case 'title':
                if (isCurrentChat) showTitle(chatEvent.text);
                refreshChatList();
                break;
            case 'compacted':
                compactedDuringTurn = true;
                if (isCurrentChat) {
                    showTitle(chatEvent.text);
                    showContext(chatEvent.contextTokens, chatEvent.contextLimit, chatEvent.contextSplit);
                }
                break;
            case 'error':
                turnFailed = true;
                statusLine.hidden = false;
                statusLine.className = 'status error';
                statusLine.textContent = chatEvent.text;
                break;
        }
        if (wasNearBottom) scrollToBottom();
        else updateJumpButton();
    }

    try {
        const response = await fetch('/chat/stream', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                message: regenerate ? (options.editedText || '') : messageText,
                client: 'test page',
                thinking: thinkingSetting,
                conversationId: currentConversationId,
                attachmentIds: regenerate ? [] : attachmentIds,
                regenerate: Boolean(regenerate),
            }),
            signal: turnController.signal,
        });
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let bufferedText = '';

        while (true) {
            const { value, done } = await reader.read();
            if (done) break;
            bufferedText += decoder.decode(value, { stream: true });
            let separatorIndex;
            while ((separatorIndex = bufferedText.indexOf('\n\n')) >= 0) {
                const eventBlock = bufferedText.slice(0, separatorIndex);
                bufferedText = bufferedText.slice(separatorIndex + 2);
                if (eventBlock.startsWith('data: ')) handleEvent(JSON.parse(eventBlock.slice(6)));
            }
        }
        if (!turnFailed && !answerFinished) statusLine.hidden = true;
    } catch (requestError) {
        if (!answerFinished) {
            const wasStopped = requestError.name === 'AbortError';
            statusLine.hidden = false;
            statusLine.className = wasStopped ? 'status stopped' : 'status error';
            statusLine.textContent = wasStopped ? 'stopped' : requestError.message;
        }
    } finally {
        if (!answerFinished) finishAnswer();
        if (turnConversationId === currentConversationId) hideCompaction();
    }

    // A failed regenerate may have removed the old turn on the core already; show what the core really has.
    if (regenerate && turnFailed && turnConversationId === currentConversationId) {
        await loadConversation(currentConversationId);
        return;
    }

    if (compactedDuringTurn && turnConversationId === currentConversationId) {
        // Another message may have started meanwhile; redraw once it's finished instead of under it.
        if (activeTurnController) rerenderAfterTurn = true;
        else await loadConversation(currentConversationId);
        refreshChatList();
    }
}

// ---------- drawers (narrow screens) ----------

function closeDrawers() {
    page.classList.remove('chats-open', 'readouts-open');
    drawerBackdrop.hidden = true;
}

function toggleDrawer(drawerClass) {
    const opening = !page.classList.contains(drawerClass);
    closeDrawers();
    if (opening) {
        page.classList.add(drawerClass);
        drawerBackdrop.hidden = false;
    }
}

// ---------- wiring ----------

messageForm.addEventListener('submit', submitEvent => {
    submitEvent.preventDefault();
    if (sendButton.disabled) return;
    const messageText = messageInput.value.trim();

    if (editingLastMessage) {
        if (!messageText) return;
        stopEditingLastMessage(true);
        regenerateLastTurn(messageText);
        return;
    }

    const readyAttachments = pendingAttachments.filter(attachment => attachment.status === 'ready');
    if (!messageText && readyAttachments.length === 0) return;
    messageInput.value = '';
    resizeMessageInput();
    pendingAttachments = [];
    renderPendingAttachments();
    sendMessage(messageText, readyAttachments, null);
});

messageInput.addEventListener('input', resizeMessageInput);

messageInput.addEventListener('keydown', keyEvent => {
    if (keyEvent.key === 'Enter' && !keyEvent.shiftKey) {
        keyEvent.preventDefault();
        messageForm.requestSubmit();
    } else if (keyEvent.key === 'ArrowUp' && messageInput.value === '' && !editingLastMessage) {
        keyEvent.preventDefault();
        startEditingLastMessage();
    }
});

// Pasted files become attachments; so does a very long pasted text, so it doesn't swamp the chat.
messageInput.addEventListener('paste', pasteEvent => {
    const clipboard = pasteEvent.clipboardData;
    if (!clipboard || editingLastMessage || compactionInProgress) return;
    if (clipboard.files.length > 0) {
        pasteEvent.preventDefault();
        uploadFiles([...clipboard.files]);
        return;
    }
    const pastedText = clipboard.getData('text/plain');
    if (pastedText.length >= pastedTextAttachmentCharacters) {
        pasteEvent.preventDefault();
        const pastedNumber = pendingAttachments.length + 1;
        uploadFiles([new File([pastedText], 'pasted text ' + pastedNumber + '.txt', { type: 'text/plain' })]);
    }
});

document.addEventListener('keydown', keyEvent => {
    if (keyEvent.key === 'Escape') {
        if (closeFileViewer()) return;
        if (activeTurnController) activeTurnController.abort();
        else if (editingLastMessage) stopEditingLastMessage(true);
        else closeDrawers();
    }
    if (keyEvent.ctrlKey && keyEvent.shiftKey && keyEvent.key.toLowerCase() === 'o') {
        keyEvent.preventDefault();
        startNewChat();
    }
});

document.querySelectorAll('.think-option').forEach(optionButton => {
    optionButton.addEventListener('click', () => {
        thinkingSetting = optionButton.dataset.value;
        document.querySelectorAll('.think-option').forEach(otherButton => otherButton.setAttribute('aria-checked', String(otherButton === optionButton)));
    });
});

stopButton.addEventListener('click', () => {
    if (activeTurnController) activeTurnController.abort();
});

document.getElementById('cancelEditButton').addEventListener('click', () => stopEditingLastMessage(true));
jumpButton.addEventListener('click', scrollToBottom);
window.addEventListener('scroll', updateJumpButton, { passive: true });

attachButton.addEventListener('click', () => fileInput.click());
fileInput.addEventListener('change', () => {
    uploadFiles([...fileInput.files]);
    fileInput.value = '';
});

let dragDepth = 0;
conversationSection.addEventListener('dragenter', dragEvent => {
    if (!dragEvent.dataTransfer.types.includes('Files') || compactionInProgress) return;
    dragEvent.preventDefault();
    dragDepth++;
    conversationSection.classList.add('dragging');
});
conversationSection.addEventListener('dragover', dragEvent => {
    if (dragEvent.dataTransfer.types.includes('Files')) dragEvent.preventDefault();
});
conversationSection.addEventListener('dragleave', () => {
    dragDepth = Math.max(0, dragDepth - 1);
    if (dragDepth === 0) conversationSection.classList.remove('dragging');
});
conversationSection.addEventListener('drop', dropEvent => {
    dropEvent.preventDefault();
    dragDepth = 0;
    conversationSection.classList.remove('dragging');
    if (!compactionInProgress && !editingLastMessage && dropEvent.dataTransfer.files.length > 0) uploadFiles([...dropEvent.dataTransfer.files]);
});

chatSearchInput.addEventListener('input', applyChatFilter);
document.getElementById('newChatButton').addEventListener('click', startNewChat);
document.getElementById('chatsToggle').addEventListener('click', () => toggleDrawer('chats-open'));
document.getElementById('readoutsToggle').addEventListener('click', () => toggleDrawer('readouts-open'));
document.getElementById('readoutsClose').addEventListener('click', closeDrawers);
drawerBackdrop.addEventListener('click', closeDrawers);

updateFormState();
pollModelStatus();
setInterval(pollModelStatus, 5000);
refreshChatList();
if (currentConversationId) loadConversation(currentConversationId);