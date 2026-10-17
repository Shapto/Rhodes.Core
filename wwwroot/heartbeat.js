// Rhodes home page: the heartbeat's away log, her journal, and the heartbeat row in MODULES.
// Needs rhodes-common.js loaded first (uses addElement, readErrorText, showDialog, showNotice, formatAge, formatWhen).
'use strict';

const awayTickCount = 12;
const journalPageSize = 3;
const heartbeatRefreshMilliseconds = 60000;
const heartbeatRunningRefreshMilliseconds = 5000;
const outcomeLabels = { nothing: 'QUIET', looked: 'LOOKED', journal: 'JOURNAL', interrupted: 'STOPPED', failed: 'FAILED', running: 'AWAKE' };

const awayListElement = document.getElementById('awayList');
const journalListElement = document.getElementById('journalList');
const journalMoreButton = document.getElementById('journalMoreButton');
const wakeButton = document.getElementById('wakeButton');

let lastSeenTickAt = undefined;
let loadedJournalCount = 0;
let heartbeatRefreshTimer = null;
let wakeInProgress = false;

// ---------- status ----------

function describeHeartbeatModule(heartbeatStatus) {
    if (!heartbeatStatus.enabled) return 'off';
    if (heartbeatStatus.running) return 'awake now';
    return heartbeatStatus.waitingFor ? 'waiting' : 'due';
}

function showHeartbeatStatus(heartbeatStatus) {
    const moduleElement = document.getElementById('moduleHeartbeat');
    moduleElement.textContent = describeHeartbeatModule(heartbeatStatus);
    moduleElement.title = heartbeatStatus.waitingFor || '';

    const lastTickText = heartbeatStatus.lastTickAt ? 'last ' + formatAge(heartbeatStatus.lastTickAt) : 'no ticks yet';
    const nextText = !heartbeatStatus.enabled ? 'heartbeat off' : heartbeatStatus.running ? 'she is awake right now' : heartbeatStatus.waitingFor ? 'next: ' + heartbeatStatus.waitingFor : 'next: within a minute';
    document.getElementById('heartbeatNote').textContent = lastTickText + ' · ' + nextText + (heartbeatStatus.allowWeb ? ' · web on' : ' · offline');
    if (!wakeInProgress) wakeButton.disabled = heartbeatStatus.running;
}

async function refreshHeartbeat() {
    clearTimeout(heartbeatRefreshTimer);
    let heartbeatStatus = null;
    try {
        const response = await fetch('/heartbeat');
        if (!response.ok) throw new Error(await readErrorText(response));
        heartbeatStatus = await response.json();
        showHeartbeatStatus(heartbeatStatus);
    } catch {
        document.getElementById('heartbeatNote').textContent = "can't reach the core";
        document.getElementById('moduleHeartbeat').textContent = '—';
    }

    // A new tick (or one that just finished) means new log rows and maybe a journal entry.
    const tickAt = heartbeatStatus ? heartbeatStatus.lastTickAt + '|' + heartbeatStatus.running : null;
    if (heartbeatStatus && tickAt !== lastSeenTickAt) {
        lastSeenTickAt = tickAt;
        await Promise.all([loadAwayLog(), loadJournal(true)]);
    }

    const nextRefresh = heartbeatStatus && heartbeatStatus.running ? heartbeatRunningRefreshMilliseconds : heartbeatRefreshMilliseconds;
    heartbeatRefreshTimer = setTimeout(refreshHeartbeat, nextRefresh);
}

// ---------- away log ----------

/**
 * Runs of quiet ticks fold into one line ("4 quiet ticks · 13:05 – 19:20"), so the log shows what she did, not how often she slept.
 */
function groupTicks(ticks) {
    const groups = [];
    for (const tick of ticks) {
        const previousGroup = groups[groups.length - 1];
        if (tick.outcome === 'nothing' && previousGroup && previousGroup.quiet) previousGroup.ticks.push(tick);
        else groups.push({ quiet: tick.outcome === 'nothing', ticks: [tick] });
    }
    return groups;
}

function addTrail(rowElement, tick) {
    if (!tick.steps || tick.steps.length === 0) return;
    const toggleButton = addElement(rowElement, 'button', 'away-trail-toggle mono', tick.steps.length + (tick.steps.length === 1 ? ' TOOL' : ' TOOLS') + ' ▸');
    toggleButton.type = 'button';
    const trailElement = addElement(rowElement, 'div', 'away-trail');
    trailElement.hidden = true;
    for (const step of tick.steps) {
        const stepElement = addElement(trailElement, 'div', 'away-step');
        const callLine = addElement(stepElement, 'div', 'away-step-call mono');
        addElement(callLine, 'span', 'away-step-name', step.toolName);
        addElement(callLine, 'span', 'away-step-arguments', step.arguments);
        addElement(stepElement, 'pre', 'away-step-result', step.result);
    }
    toggleButton.addEventListener('click', () => {
        trailElement.hidden = !trailElement.hidden;
        toggleButton.textContent = toggleButton.textContent.replace(trailElement.hidden ? '▾' : '▸', trailElement.hidden ? '▸' : '▾');
    });
}

function createTickRow(tick) {
    const rowElement = document.createElement('article');
    rowElement.className = 'away-row';
    rowElement.dataset.outcome = tick.outcome;
    const topLine = addElement(rowElement, 'div', 'memory-top');
    addElement(topLine, 'span', 'outcome-badge', outcomeLabels[tick.outcome] || tick.outcome.toUpperCase());
    addElement(topLine, 'span', null, formatWhen(tick.startedAt) + ' · ' + formatAge(tick.startedAt) + (tick.trigger === 'manual' ? ' · woken by you' : '') + (tick.seconds ? ' · ' + tick.seconds.toFixed(0) + ' s' : ''));
    addElement(rowElement, 'p', 'away-note', tick.note);
    addTrail(rowElement, tick);
    return rowElement;
}

function createQuietRow(quietTicks) {
    const rowElement = document.createElement('article');
    rowElement.className = 'away-row quiet';
    rowElement.dataset.outcome = 'nothing';
    const topLine = addElement(rowElement, 'div', 'memory-top');
    addElement(topLine, 'span', 'outcome-badge', 'QUIET');
    const newestTick = quietTicks[0];
    const oldestTick = quietTicks[quietTicks.length - 1];
    const whenText = quietTicks.length === 1 ? formatWhen(newestTick.startedAt) + ' · ' + formatAge(newestTick.startedAt) : formatWhen(oldestTick.startedAt) + ' – ' + formatWhen(newestTick.startedAt);
    addElement(topLine, 'span', null, whenText);
    addElement(rowElement, 'p', 'away-note', quietTicks.length === 1 ? newestTick.note : quietTicks.length + ' quiet ticks: nothing worth doing.');
    return rowElement;
}

async function loadAwayLog() {
    let ticks;
    try {
        const response = await fetch('/heartbeat/ticks?limit=' + awayTickCount);
        if (!response.ok) throw new Error(await readErrorText(response));
        ticks = await response.json();
    } catch (loadError) {
        awayListElement.replaceChildren();
        addElement(awayListElement, 'span', 'readout-empty', "couldn't load the log: " + loadError.message);
        return;
    }

    const logFragment = document.createDocumentFragment();
    for (const tickGroup of groupTicks(ticks)) {
        logFragment.appendChild(tickGroup.quiet ? createQuietRow(tickGroup.ticks) : createTickRow(tickGroup.ticks[0]));
    }
    if (ticks.length === 0) addElement(logFragment, 'span', 'readout-empty', "nothing yet · she wakes on her own once you've been away a while, or press WAKE HER NOW");
    awayListElement.replaceChildren(logFragment);
}

async function wakeHerNow() {
    wakeInProgress = true;
    wakeButton.disabled = true;
    wakeButton.textContent = 'AWAKE…';
    const wakeMeta = document.getElementById('wakeMeta');
    wakeMeta.textContent = 'she has a look around · usually under a minute';
    setTimeout(refreshHeartbeat, 300);
    try {
        const response = await fetch('/heartbeat/run', { method: 'POST' });
        if (!response.ok) throw new Error(await readErrorText(response));
        const tick = await response.json();
        wakeMeta.textContent = 'done in ' + tick.seconds.toFixed(0) + ' s';
    } catch (wakeError) {
        wakeMeta.textContent = '';
        await showNotice("COULDN'T WAKE HER", wakeError.message);
    } finally {
        wakeInProgress = false;
        wakeButton.textContent = 'WAKE HER NOW';
        lastSeenTickAt = undefined;
        refreshHeartbeat();
    }
}

// ---------- journal ----------

function createJournalRow(journalEntry) {
    const rowElement = document.createElement('article');
    rowElement.className = 'journal-row';
    const topLine = addElement(rowElement, 'div', 'memory-top');
    addElement(topLine, 'span', 'journal-date', new Date(journalEntry.writtenAt).toLocaleString([], { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }));
    addElement(topLine, 'span', null, formatAge(journalEntry.writtenAt));
    const deleteButton = addElement(topLine, 'button', 'turn-action danger journal-delete', 'DELETE');
    deleteButton.type = 'button';
    deleteButton.addEventListener('click', () => deleteJournalEntry(journalEntry, rowElement));
    addElement(rowElement, 'p', 'journal-text', journalEntry.text);
    return rowElement;
}

async function loadJournal(reset) {
    const offset = reset ? 0 : loadedJournalCount;
    let journalPage;
    try {
        const response = await fetch('/journal?limit=' + journalPageSize + '&offset=' + offset);
        if (!response.ok) throw new Error(await readErrorText(response));
        journalPage = await response.json();
    } catch (loadError) {
        journalListElement.replaceChildren();
        addElement(journalListElement, 'span', 'readout-empty', "couldn't load the journal: " + loadError.message);
        return;
    }

    const entriesFragment = document.createDocumentFragment();
    for (const journalEntry of journalPage.entries) entriesFragment.appendChild(createJournalRow(journalEntry));
    if (journalPage.total === 0) addElement(entriesFragment, 'span', 'readout-empty', 'empty so far · she writes here only when something is worth it');
    if (reset) journalListElement.replaceChildren(entriesFragment);
    else journalListElement.appendChild(entriesFragment);

    loadedJournalCount = offset + journalPage.entries.length;
    document.getElementById('journalNote').textContent = journalPage.total === 0 ? '' : journalPage.total + (journalPage.total === 1 ? ' entry' : ' entries');
    const remainingCount = journalPage.total - loadedJournalCount;
    journalMoreButton.hidden = remainingCount <= 0;
    journalMoreButton.textContent = 'OLDER ENTRIES · ' + remainingCount + ' LEFT';
}

async function deleteJournalEntry(journalEntry, rowElement) {
    const confirmed = await showDialog({
        title: 'DELETE JOURNAL ENTRY',
        message: "It's gone for good, and she won't see it the next time she wakes.",
        detail: journalEntry.text.length > 400 ? journalEntry.text.slice(0, 400) + '…' : journalEntry.text,
        confirmText: 'DELETE',
        cancelText: 'CANCEL',
        danger: true,
    });
    if (!confirmed) return;

    try {
        const response = await fetch('/journal/' + encodeURIComponent(journalEntry.id), { method: 'DELETE' });
        if (!response.ok && response.status !== 404) throw new Error(await readErrorText(response));
    } catch (deleteError) {
        await showNotice("COULDN'T DELETE", deleteError.message);
        return;
    }
    rowElement.remove();
    loadJournal(true);
}

// ---------- wiring ----------

wakeButton.addEventListener('click', wakeHerNow);
journalMoreButton.addEventListener('click', () => loadJournal(false));
refreshHeartbeat();
