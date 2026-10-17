// Rhodes home page: start a chat, pick one up, and look after Closure's memory.
// Needs rhodes-common.js loaded first.
'use strict';

const memoryTypes = ['fact', 'preference', 'project', 'event', 'persona', 'archive'];
const memoryPageSize = 30;
const recentChatCount = 6;

const startForm = document.getElementById('startForm');
const startInput = document.getElementById('startInput');
const modelChip = document.getElementById('modelChip');
const typeTabsElement = document.getElementById('typeTabs');
const memoryListElement = document.getElementById('memoryList');
const memorySearchInput = document.getElementById('memorySearch');
const memorySortSelect = document.getElementById('memorySort');
const loadMoreButton = document.getElementById('loadMoreButton');

let memoryFilter = 'all';
let memorySearchText = '';
let memorySortOrder = 'newest';
let loadedMemoryCount = 0;
let memoryTotal = 0;
let memoryRequestNumber = 0;
let memoryStatistics = null;
let searchDelayTimer = null;

// ---------- greeting and status ----------

function showGreeting() {
    const hour = new Date().getHours();
    const greetingByHour = hour < 5 ? "it's late" : hour < 12 ? 'morning' : hour < 18 ? 'afternoon' : 'evening';
    const dateText = new Date().toLocaleDateString([], { weekday: 'long', day: 'numeric', month: 'long' });
    document.getElementById('greetingLine').textContent = greetingByHour + ' · ' + dateText;
}

function showModelStatus(modelStatus) {
    modelChip.dataset.state = modelStatus.state;
    modelChip.textContent = describeModelChip(modelStatus.state, modelStatus.tokensPerSecond);
    modelChip.title = modelStatus.detail || '';
    document.getElementById('moduleCore').textContent = modelStatus.state === 'unreachable' ? 'offline' : 'online';
    document.getElementById('moduleModel').textContent = modelStatus.state === 'unreachable' ? '—' : modelStatus.state;
    document.getElementById('moduleModel').title = modelStatus.detail || '';
    document.getElementById('moduleSpeed').textContent = modelStatus.tokensPerSecond ? modelStatus.tokensPerSecond.toFixed(1) + ' t/s' : '—';
}

// ---------- chats ----------

function openChat(conversationId) {
    writeStorage(conversationStorageKey, conversationId);
    window.location.href = 'chat.html';
}

async function loadChats() {
    const recentChatsElement = document.getElementById('recentChats');
    let chatSummaries;
    try {
        const response = await fetch('/chats');
        chatSummaries = response.ok ? await response.json() : [];
    } catch {
        recentChatsElement.replaceChildren();
        addElement(recentChatsElement, 'span', 'readout-empty', "can't reach the core");
        return;
    }

    const resumePanel = document.getElementById('resumePanel');
    resumePanel.hidden = chatSummaries.length === 0;
    if (chatSummaries.length > 0) {
        const latestChat = chatSummaries[0];
        document.getElementById('resumeTitle').textContent = latestChat.title || 'untitled';
        document.getElementById('resumeMeta').textContent = latestChat.messageCount + ' messages · last active ' + formatAge(latestChat.updatedAt) + (latestChat.compacted ? ' · compacted' : '');
        document.getElementById('resumeButton').onclick = () => openChat(latestChat.id);
    }

    const recentFragment = document.createDocumentFragment();
    for (const chatSummary of chatSummaries.slice(1, 1 + recentChatCount)) {
        const chatButton = addElement(recentFragment, 'button', 'recent-chat');
        chatButton.type = 'button';
        addElement(chatButton, 'span', 'chat-title', chatSummary.title || 'untitled');
        addElement(chatButton, 'span', 'chat-meta', chatSummary.messageCount + ' msg · ' + formatAge(chatSummary.updatedAt));
        chatButton.addEventListener('click', () => openChat(chatSummary.id));
    }
    if (chatSummaries.length <= 1) addElement(recentFragment, 'span', 'readout-empty', chatSummaries.length === 0 ? 'no chats yet' : 'no other chats yet');
    recentChatsElement.replaceChildren(recentFragment);
}

// ---------- memory statistics and tabs ----------

async function loadStatistics() {
    try {
        const response = await fetch('/memory/statistics');
        if (!response.ok) return;
        memoryStatistics = await response.json();
    } catch {
        return;
    }

    document.getElementById('statisticsTotal').textContent = memoryStatistics.total.toLocaleString();
    document.getElementById('statisticsWeek').textContent = memoryStatistics.savedThisWeek.toLocaleString();
    document.getElementById('statisticsReview').textContent = memoryStatistics.needsReview.toLocaleString();
    document.getElementById('memoryTotalNote').textContent = memoryStatistics.total.toLocaleString() + ' memories';

    const largestTypeCount = Math.max(1, ...memoryTypes.map(memoryType => memoryStatistics.byType[memoryType] || 0));
    const barsFragment = document.createDocumentFragment();
    for (const memoryType of memoryTypes) {
        const typeCount = memoryStatistics.byType[memoryType] || 0;
        const barRow = addElement(barsFragment, 'div', 'type-bar-row');
        barRow.dataset.type = memoryType;
        addElement(barRow, 'span', null, memoryType);
        const barTrack = addElement(barRow, 'div', 'type-bar-track');
        addElement(barTrack, 'div', 'type-bar-fill').style.width = (typeCount / largestTypeCount * 100).toFixed(1) + '%';
        addElement(barRow, 'span', 'type-bar-count', String(typeCount));
    }
    document.getElementById('typeBars').replaceChildren(barsFragment);
    renderTypeTabs();
}

function renderTypeTabs() {
    const tabDefinitions = [{ filter: 'all', label: 'ALL', count: memoryStatistics ? memoryStatistics.total : null }];
    for (const memoryType of memoryTypes) {
        tabDefinitions.push({ filter: memoryType, label: memoryType.toUpperCase(), count: memoryStatistics ? memoryStatistics.byType[memoryType] || 0 : null });
    }
    if (memoryStatistics && memoryStatistics.needsReview > 0) {
        tabDefinitions.push({ filter: 'review', label: 'NEEDS REVIEW', count: memoryStatistics.needsReview });
    }
    // The review tab disappears once everything is reviewed; fall back to ALL instead of an empty view.
    if (!tabDefinitions.some(tabDefinition => tabDefinition.filter === memoryFilter)) {
        memoryFilter = 'all';
        loadMemories(true);
    }

    const tabsFragment = document.createDocumentFragment();
    for (const tabDefinition of tabDefinitions) {
        const tabButton = addElement(tabsFragment, 'button', 'type-tab' + (tabDefinition.filter === 'review' ? ' review' : ''));
        tabButton.type = 'button';
        tabButton.setAttribute('role', 'tab');
        tabButton.setAttribute('aria-selected', String(tabDefinition.filter === memoryFilter));
        tabButton.appendChild(document.createTextNode(tabDefinition.label));
        if (tabDefinition.count !== null) addElement(tabButton, 'span', 'tab-count', String(tabDefinition.count));
        tabButton.addEventListener('click', () => {
            if (memoryFilter === tabDefinition.filter) return;
            memoryFilter = tabDefinition.filter;
            renderTypeTabs();
            loadMemories(true);
        });
    }
    typeTabsElement.replaceChildren(tabsFragment);
}

// ---------- memory list ----------

async function loadMemories(reset) {
    const requestNumber = ++memoryRequestNumber;
    const offset = reset ? 0 : loadedMemoryCount;
    const query = new URLSearchParams({ type: memoryFilter, sort: memorySortOrder, offset: String(offset), limit: String(memoryPageSize) });
    if (memorySearchText) query.set('search', memorySearchText);

    let memoryPage;
    try {
        const response = await fetch('/memory/list?' + query.toString());
        if (!response.ok) throw new Error(await readErrorText(response));
        memoryPage = await response.json();
    } catch (loadError) {
        if (requestNumber !== memoryRequestNumber) return;
        memoryListElement.replaceChildren();
        addElement(memoryListElement, 'div', 'memory-empty', "Couldn't load memories: " + loadError.message);
        loadMoreButton.hidden = true;
        return;
    }
    // A newer request (another tab or search) started meanwhile; its result wins.
    if (requestNumber !== memoryRequestNumber) return;

    const rowsFragment = document.createDocumentFragment();
    for (const memory of memoryPage.memories) rowsFragment.appendChild(createMemoryRow(memory));
    if (reset) memoryListElement.replaceChildren(rowsFragment);
    else memoryListElement.appendChild(rowsFragment);

    loadedMemoryCount = offset + memoryPage.memories.length;
    memoryTotal = memoryPage.total;
    if (memoryTotal === 0) addElement(memoryListElement, 'div', 'memory-empty', memorySearchText ? 'No memories match that search.' : 'Nothing here.');
    updateLoadMore();
    memoryListElement.querySelectorAll('.memory-content.clamped').forEach(addExpandToggleIfNeeded);
}

function updateLoadMore() {
    const remainingCount = memoryTotal - loadedMemoryCount;
    loadMoreButton.hidden = remainingCount <= 0;
    loadMoreButton.textContent = 'LOAD MORE · ' + remainingCount + ' LEFT';
}

function addExpandToggleIfNeeded(contentElement) {
    if (contentElement.scrollHeight <= contentElement.clientHeight + 2) {
        contentElement.classList.remove('clamped');
        return;
    }
    const expandButton = document.createElement('button');
    expandButton.className = 'memory-expand';
    expandButton.textContent = 'SHOW ALL';
    expandButton.type = 'button';
    contentElement.after(expandButton);
    expandButton.addEventListener('click', () => {
        const nowClamped = contentElement.classList.toggle('clamped');
        expandButton.textContent = nowClamped ? 'SHOW ALL' : 'SHOW LESS';
    });
}

function describeMemoryMeta(memory) {
    const parts = [memory.source, 'saved ' + formatAge(memory.createdAt)];
    parts.push(memory.accessCount > 0 ? 'used ' + memory.accessCount + '× · last ' + formatAge(memory.lastAccessedAt) : 'never used');
    return parts.join(' · ');
}

function createMemoryRow(memory) {
    const rowElement = document.createElement('article');
    rowElement.className = 'memory-row';
    rowElement.dataset.type = memory.type;

    const topLine = addElement(rowElement, 'div', 'memory-top');
    const typeBadge = addElement(topLine, 'span', 'type-badge', memory.type.toUpperCase());
    if (memory.needsReview) addElement(topLine, 'span', 'review-badge', 'NEEDS REVIEW');
    addElement(topLine, 'span', null, describeMemoryMeta(memory));

    addElement(rowElement, 'p', 'memory-content clamped', memory.content);

    if (memory.tags && memory.tags.length > 0) {
        const tagsLine = addElement(rowElement, 'div', 'memory-tags');
        for (const tag of memory.tags) addElement(tagsLine, 'span', null, '#' + tag);
    }

    const actionsLine = addElement(rowElement, 'div', 'memory-actions');
    const typeSelect = addElement(actionsLine, 'select', 'type-select');
    typeSelect.setAttribute('aria-label', 'Memory type');
    for (const memoryType of memoryTypes) {
        const typeOption = addElement(typeSelect, 'option', null, memoryType);
        typeOption.value = memoryType;
    }
    typeSelect.value = memory.type;
    typeSelect.addEventListener('change', () => changeMemoryType(memory, typeSelect.value, rowElement, typeBadge, typeSelect));

    if (memory.needsReview) {
        const keepButton = addElement(actionsLine, 'button', 'turn-action', 'OK AS ' + memory.type.toUpperCase());
        keepButton.type = 'button';
        keepButton.title = 'The type is right; clear the review flag';
        keepButton.addEventListener('click', () => changeMemoryType(memory, memory.type, rowElement, typeBadge, typeSelect));
    }

    if (memory.type !== 'archive') {
        const archiveButton = addElement(actionsLine, 'button', 'turn-action', 'ARCHIVE');
        archiveButton.type = 'button';
        archiveButton.title = 'Keep it searchable, but never inject it automatically';
        archiveButton.addEventListener('click', () => changeMemoryType(memory, 'archive', rowElement, typeBadge, typeSelect));
    }

    const deleteButton = addElement(actionsLine, 'button', 'turn-action danger', 'DELETE');
    deleteButton.type = 'button';
    deleteButton.addEventListener('click', () => deleteMemory(memory, rowElement));
    return rowElement;
}

function removeRow(rowElement) {
    rowElement.classList.add('leaving');
    setTimeout(() => {
        rowElement.remove();
        loadedMemoryCount = Math.max(0, loadedMemoryCount - 1);
        memoryTotal = Math.max(0, memoryTotal - 1);
        updateLoadMore();
        if (memoryListElement.querySelectorAll('.memory-row').length === 0) loadMemories(true);
    }, 250);
}

async function changeMemoryType(memory, newType, rowElement, typeBadge, typeSelect) {
    typeSelect.disabled = true;
    try {
        const response = await fetch('/memory/' + encodeURIComponent(memory.id) + '/type?type=' + encodeURIComponent(newType), { method: 'POST' });
        if (!response.ok) throw new Error(await readErrorText(response));
    } catch (changeError) {
        typeSelect.value = memory.type;
        typeSelect.disabled = false;
        await showNotice("COULDN'T CHANGE THE TYPE", changeError.message);
        return;
    }

    const wasReview = memory.needsReview;
    memory.type = newType;
    memory.needsReview = false;
    loadStatistics();

    // A row that no longer belongs in the open tab leaves; otherwise it's redrawn in place.
    const stillMatches = memoryFilter === 'all' || memoryFilter === newType;
    if (!stillMatches || (memoryFilter === 'review' && wasReview)) {
        removeRow(rowElement);
        return;
    }
    const newRow = createMemoryRow(memory);
    rowElement.replaceWith(newRow);
    newRow.querySelectorAll('.memory-content.clamped').forEach(addExpandToggleIfNeeded);
}

async function deleteMemory(memory, rowElement) {
    const confirmed = await showDialog({
        title: 'DELETE MEMORY',
        message: "Closure forgets this for good. If you only want it out of her way, archive it instead.",
        detail: memory.content.length > 400 ? memory.content.slice(0, 400) + '…' : memory.content,
        confirmText: 'DELETE',
        cancelText: 'CANCEL',
        danger: true,
    });
    if (!confirmed) return;

    try {
        const response = await fetch('/memory/' + encodeURIComponent(memory.id) + '/delete', { method: 'POST' });
        if (!response.ok && response.status !== 404) throw new Error(await readErrorText(response));
    } catch (deleteError) {
        await showNotice("COULDN'T DELETE", deleteError.message);
        return;
    }
    removeRow(rowElement);
    loadStatistics();
}

// ---------- wiring ----------

startForm.addEventListener('submit', submitEvent => {
    submitEvent.preventDefault();
    const startText = startInput.value.trim();
    if (!startText) return;
    // The chat page picks this up, starts a fresh chat and sends it.
    writeStorage(startMessageStorageKey, startText);
    writeStorage(conversationStorageKey, null);
    window.location.href = 'chat.html';
});

startInput.addEventListener('keydown', keyEvent => {
    if (keyEvent.key === 'Enter' && !keyEvent.shiftKey) {
        keyEvent.preventDefault();
        startForm.requestSubmit();
    }
});

startInput.addEventListener('input', () => {
    startInput.style.height = 'auto';
    startInput.style.height = Math.min(startInput.scrollHeight + 2, 200) + 'px';
});

memorySearchInput.addEventListener('input', () => {
    clearTimeout(searchDelayTimer);
    searchDelayTimer = setTimeout(() => {
        memorySearchText = memorySearchInput.value.trim();
        loadMemories(true);
    }, 250);
});

memorySortSelect.addEventListener('change', () => {
    memorySortOrder = memorySortSelect.value;
    loadMemories(true);
});

loadMoreButton.addEventListener('click', () => loadMemories(false));

showGreeting();
startModelStatusPolling(showModelStatus);
renderTypeTabs();
loadStatistics();
loadMemories(true);
loadChats();