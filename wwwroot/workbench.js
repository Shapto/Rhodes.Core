// Rhodes home page, workbench panel: the core's look-inside endpoints, so none of them need a typed URL.
// Needs rhodes-common.js and home.js loaded first (uses addElement, readErrorText, showDialog, openChat, …).
'use strict';

const benchViewStorageKey = 'rhodesWorkbenchView';
const injectCount = 3;
const injectMinimumMatch = 0.5;

const benchTabButtons = document.querySelectorAll('#benchTabs .type-tab');
const benchViews = document.querySelectorAll('.bench-view');
const loadedBenchViews = new Set();

let coreTools = [];
let selectedTool = null;
let knownDocuments = [];

// ---------- tabs ----------

function showBenchView(viewName) {
    for (const tabButton of benchTabButtons) tabButton.setAttribute('aria-selected', String(tabButton.dataset.view === viewName));
    for (const benchView of benchViews) benchView.hidden = benchView.dataset.view !== viewName;
    writeStorage(benchViewStorageKey, viewName);

    // Each view loads the first time it's opened, so the home page doesn't ask the core for everything up front.
    if (loadedBenchViews.has(viewName)) return Promise.resolve();
    loadedBenchViews.add(viewName);
    const viewLoaders = { tools: loadTools, documents: loadDocuments, chats: loadInternalsChats, legacy: loadLegacyBreakdown, permissions: loadPermissions, behavior: loadBehaviorTests };
    return viewLoaders[viewName] ? viewLoaders[viewName]() : Promise.resolve();
}

async function fetchJson(url, options) {
    const response = await fetch(url, options);
    if (!response.ok) throw new Error(await readErrorText(response));
    return response.json();
}

function showEmpty(containerElement, text) {
    containerElement.replaceChildren();
    addElement(containerElement, 'span', 'readout-empty', text);
}

// ---------- memory probe ----------

function addScoreBar(parentElement, label, value) {
    const scoreElement = addElement(parentElement, 'div', 'score');
    addElement(scoreElement, 'span', 'score-label', label);
    const trackElement = addElement(scoreElement, 'div', 'score-track');
    addElement(trackElement, 'div', 'score-fill').style.width = (Math.max(0, Math.min(1, value)) * 100).toFixed(0) + '%';
    addElement(scoreElement, 'span', 'score-value', value.toFixed(2));
}

function describeSkipReason(rankedMemory) {
    return rankedMemory.match < injectMinimumMatch ? 'SKIPPED · MATCH UNDER ' + injectMinimumMatch.toFixed(2) : 'SKIPPED · TOO FEW SHARED WORDS';
}

async function runProbe(probeText) {
    const rankingElement = document.getElementById('probeRanking');
    const keywordElement = document.getElementById('probeKeyword');
    showEmpty(rankingElement, 'probing…');
    showEmpty(keywordElement, 'probing…');

    const encodedText = encodeURIComponent(probeText);
    let rankedMemories, injectedMemories, keywordHits;
    try {
        [rankedMemories, injectedMemories, keywordHits] = await Promise.all([
            fetchJson('/memory/rank?query=' + encodedText),
            fetchJson('/memory/inject-preview?message=' + encodedText),
            fetchJson('/memory/search?query=' + encodedText),
        ]);
    } catch (probeError) {
        showEmpty(rankingElement, "couldn't probe: " + probeError.message);
        showEmpty(keywordElement, '—');
        return;
    }

    // inject-preview returns "type: content" lines; matching them back to the ranking shows which rows made it.
    const injectedLines = new Set(injectedMemories);
    const rankingFragment = document.createDocumentFragment();
    rankedMemories.forEach((rankedMemory, rankIndex) => {
        const rowElement = addElement(rankingFragment, 'article', 'probe-row');
        rowElement.dataset.type = rankedMemory.type;
        const topLine = addElement(rowElement, 'div', 'memory-top');
        addElement(topLine, 'span', 'type-badge', rankedMemory.type.toUpperCase());
        const wasInjected = injectedLines.has(rankedMemory.type + ': ' + rankedMemory.content);
        if (wasInjected) addElement(topLine, 'span', 'inject-badge', 'INJECTED');
        else if (rankIndex < injectCount) addElement(topLine, 'span', 'skip-badge', describeSkipReason(rankedMemory));
        addElement(topLine, 'span', null, '#' + (rankIndex + 1));
        addElement(rowElement, 'p', 'memory-content clamped-two', rankedMemory.content);
        const scoresElement = addElement(rowElement, 'div', 'scores');
        addScoreBar(scoresElement, 'score', rankedMemory.score);
        addScoreBar(scoresElement, 'match', rankedMemory.match);
        addScoreBar(scoresElement, 'recency', rankedMemory.recency);
        addScoreBar(scoresElement, 'use', rankedMemory.use);
    });
    if (rankedMemories.length === 0) addElement(rankingFragment, 'span', 'readout-empty', 'nothing ranks for this, so nothing would be injected');
    rankingElement.replaceChildren(rankingFragment);

    const keywordFragment = document.createDocumentFragment();
    for (const keywordHit of keywordHits) {
        const rowElement = addElement(keywordFragment, 'article', 'probe-row');
        rowElement.dataset.type = keywordHit.memory.type;
        const topLine = addElement(rowElement, 'div', 'memory-top');
        addElement(topLine, 'span', 'type-badge', keywordHit.memory.type.toUpperCase());
        // bm25 scores are negative; closer to zero is a weaker hit.
        addElement(topLine, 'span', null, 'bm25 ' + keywordHit.keywordScore.toFixed(2));
        addElement(rowElement, 'p', 'memory-content clamped-two', keywordHit.memory.content);
    }
    if (keywordHits.length === 0) addElement(keywordFragment, 'span', 'readout-empty', 'no memory contains these words');
    keywordElement.replaceChildren(keywordFragment);
}

// ---------- try a tool ----------

async function loadTools() {
    const pickerElement = document.getElementById('toolPicker');
    try {
        coreTools = await fetchJson('/tools');
    } catch (loadError) {
        showEmpty(pickerElement, "couldn't load tools: " + loadError.message);
        return;
    }
    // The document fields offer the known documents as suggestions.
    if (knownDocuments.length === 0) await loadDocumentList();

    const pickerFragment = document.createDocumentFragment();
    for (const coreTool of coreTools) {
        const toolButton = addElement(pickerFragment, 'button', 'type-tab', coreTool.name);
        toolButton.type = 'button';
        toolButton.dataset.tool = coreTool.name;
        toolButton.addEventListener('click', () => selectTool(coreTool.name));
    }
    pickerElement.replaceChildren(pickerFragment);
    if (coreTools.length > 0) selectTool(coreTools[0].name);
}

function createToolField(propertyName, propertySchema, isRequired) {
    const fieldElement = document.createElement('label');
    fieldElement.className = 'tool-field';
    const nameLine = addElement(fieldElement, 'span', 'tool-field-name mono', propertyName);
    if (isRequired) addElement(nameLine, 'span', 'tool-field-required', ' required');

    let inputElement;
    if (propertySchema.enum) {
        inputElement = addElement(fieldElement, 'select', 'type-select tool-input');
        if (!isRequired) addElement(inputElement, 'option', null, '(any)').value = '';
        for (const enumValue of propertySchema.enum) addElement(inputElement, 'option', null, enumValue).value = enumValue;
    } else if (propertyName === 'content') {
        inputElement = addElement(fieldElement, 'textarea', 'memory-search tool-input');
        inputElement.rows = 3;
    } else {
        inputElement = addElement(fieldElement, 'input', 'memory-search tool-input');
        inputElement.type = propertySchema.type === 'integer' ? 'number' : 'text';
        if (propertySchema.type === 'array') inputElement.placeholder = 'comma-separated';
        if (propertyName === 'document_id') inputElement.setAttribute('list', 'documentIdOptions');
    }
    inputElement.name = propertyName;
    inputElement.dataset.valueType = propertySchema.type;
    if (propertySchema.description) addElement(fieldElement, 'span', 'tool-field-hint', propertySchema.description);
    return fieldElement;
}

function selectTool(toolName) {
    selectedTool = coreTools.find(coreTool => coreTool.name === toolName) || null;
    for (const toolButton of document.querySelectorAll('#toolPicker .type-tab')) toolButton.setAttribute('aria-selected', String(toolButton.dataset.tool === toolName));
    document.getElementById('toolDescription').textContent = selectedTool ? selectedTool.description : '';
    document.getElementById('toolOutput').hidden = true;
    document.getElementById('toolRunMeta').textContent = '';

    const formElement = document.getElementById('toolForm');
    formElement.replaceChildren();
    if (!selectedTool) return;
    const properties = (selectedTool.parameters && selectedTool.parameters.properties) || {};
    const requiredNames = new Set((selectedTool.parameters && selectedTool.parameters.required) || []);
    for (const [propertyName, propertySchema] of Object.entries(properties)) {
        formElement.appendChild(createToolField(propertyName, propertySchema, requiredNames.has(propertyName)));
    }
    const firstInput = formElement.querySelector('.tool-input');
    if (firstInput) firstInput.focus();
}

function readToolArguments() {
    const toolArguments = {};
    const missingNames = [];
    const requiredNames = new Set((selectedTool.parameters && selectedTool.parameters.required) || []);
    for (const inputElement of document.querySelectorAll('#toolForm .tool-input')) {
        const rawValue = inputElement.value.trim();
        if (!rawValue) {
            if (requiredNames.has(inputElement.name)) missingNames.push(inputElement.name);
            continue;
        }
        const valueType = inputElement.dataset.valueType;
        toolArguments[inputElement.name] = valueType === 'integer' ? Number(rawValue) : valueType === 'array' ? rawValue.split(',').map(part => part.trim()).filter(Boolean) : rawValue;
    }
    return { toolArguments, missingNames };
}

async function runSelectedTool() {
    if (!selectedTool) return;
    const { toolArguments, missingNames } = readToolArguments();
    if (missingNames.length > 0) {
        await showNotice('MISSING ' + (missingNames.length === 1 ? 'A FIELD' : 'FIELDS'), 'Fill in: ' + missingNames.join(', '));
        return;
    }
    if (selectedTool.name === 'save_memory') {
        const confirmed = await showDialog({
            title: 'SAVE A REAL MEMORY',
            message: 'This writes to her memory exactly as if she had saved it herself. You can delete it from the memory panel afterwards.',
            detail: toolArguments.content,
            confirmText: 'SAVE',
            cancelText: 'CANCEL',
        });
        if (!confirmed) return;
    }

    const runButton = document.getElementById('toolRunButton');
    const outputElement = document.getElementById('toolOutput');
    const metaElement = document.getElementById('toolRunMeta');
    runButton.disabled = true;
    metaElement.textContent = 'running ' + selectedTool.name + '…';
    const startedAt = performance.now();
    try {
        const response = await fetch('/tools/' + encodeURIComponent(selectedTool.name), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(toolArguments),
        });
        const resultText = response.ok ? await response.text() : 'Error: ' + await readErrorText(response);
        const elapsedSeconds = (performance.now() - startedAt) / 1000;
        outputElement.textContent = resultText;
        outputElement.hidden = false;
        metaElement.textContent = elapsedSeconds.toFixed(1) + ' s · ' + resultText.length.toLocaleString() + ' characters · about ' + formatTokens(Math.round(resultText.length * 0.3)) + ' tokens of her context';
        if (selectedTool.name === 'save_memory') loadStatistics();
    } catch (runError) {
        outputElement.textContent = "Couldn't reach the core: " + runError.message;
        outputElement.hidden = false;
        metaElement.textContent = '';
    } finally {
        runButton.disabled = false;
    }
}

// ---------- documents ----------

async function loadDocumentList() {
    try {
        knownDocuments = await fetchJson('/documents');
    } catch {
        knownDocuments = [];
    }
    const optionsElement = document.getElementById('documentIdOptions') || addElement(document.body, 'datalist');
    optionsElement.id = 'documentIdOptions';
    optionsElement.replaceChildren();
    for (const knownDocument of knownDocuments) {
        const optionElement = addElement(optionsElement, 'option');
        optionElement.value = knownDocument.id;
        optionElement.label = knownDocument.name;
    }
}

async function loadDocuments() {
    await loadDocumentList();
    const rowsElement = document.getElementById('documentRows');
    const rowsFragment = document.createDocumentFragment();
    for (const knownDocument of knownDocuments) {
        const tableRow = addElement(rowsFragment, 'tr');
        const nameCell = addElement(tableRow, 'td', 'document-name');
        addElement(nameCell, 'span', null, knownDocument.name);
        addElement(nameCell, 'span', 'document-source mono', knownDocument.id + (knownDocument.source.startsWith('http') ? ' · ' + knownDocument.source : ''));
        addElement(tableRow, 'td', 'mono', knownDocument.lineCount.toLocaleString());
        addElement(tableRow, 'td', 'mono', formatTokens(knownDocument.tokens));
        addElement(tableRow, 'td', 'mono', formatAge(knownDocument.createdAt));
        const actionsCell = addElement(tableRow, 'td', 'table-actions');
        const mapButton = addElement(actionsCell, 'button', 'turn-action', 'MAP');
        mapButton.type = 'button';
        mapButton.addEventListener('click', () => showDocumentMap(knownDocument));
        const searchButton = addElement(actionsCell, 'button', 'turn-action', 'SEARCH IT');
        searchButton.type = 'button';
        searchButton.addEventListener('click', () => openToolWithDocument('search_document', knownDocument.id));
    }
    if (knownDocuments.length === 0) {
        const emptyRow = addElement(rowsFragment, 'tr');
        const emptyCell = addElement(emptyRow, 'td', 'readout-empty', 'no documents yet · they appear when you attach a big file or she fetches a page');
        emptyCell.colSpan = 5;
    }
    rowsElement.replaceChildren(rowsFragment);
}

async function showDocumentMap(knownDocument) {
    const mapElement = document.getElementById('documentMap');
    mapElement.hidden = false;
    mapElement.textContent = 'building the map…';
    try {
        const response = await fetch('/documents/' + encodeURIComponent(knownDocument.id) + '/map');
        mapElement.textContent = response.ok ? await response.text() : "Couldn't build the map: " + await readErrorText(response);
    } catch (mapError) {
        mapElement.textContent = "Couldn't reach the core: " + mapError.message;
    }
    mapElement.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}

async function openToolWithDocument(toolName, documentId) {
    await showBenchView('tools');
    selectTool(toolName);
    const documentField = document.querySelector('#toolForm .tool-input[name="document_id"]');
    if (documentField) documentField.value = documentId;
    const nextField = document.querySelector('#toolForm .tool-input:not([name="document_id"])');
    if (nextField) nextField.focus();
}

// ---------- chat internals ----------

async function loadInternalsChats() {
    const chatSelect = document.getElementById('internalsChatSelect');
    let chatSummaries;
    try {
        chatSummaries = await fetchJson('/chats');
    } catch (loadError) {
        document.getElementById('internalsSummary').textContent = "Couldn't load chats: " + loadError.message;
        return;
    }
    chatSelect.replaceChildren();
    for (const chatSummary of chatSummaries) {
        const optionElement = addElement(chatSelect, 'option', null, (chatSummary.compacted ? '◆ ' : '') + (chatSummary.title || 'untitled') + ' · ' + formatAge(chatSummary.updatedAt));
        optionElement.value = chatSummary.id;
    }
    if (chatSummaries.length > 0) showChatInternals(chatSummaries[0].id);
    else document.getElementById('internalsSummary').textContent = 'No chats yet.';
}

function addReadout(gridElement, label, value) {
    addElement(gridElement, 'dt', null, label);
    addElement(gridElement, 'dd', null, value);
}

async function showChatInternals(conversationId) {
    const gridElement = document.getElementById('internalsGrid');
    const summaryElement = document.getElementById('internalsSummary');
    let compaction;
    try {
        compaction = await fetchJson('/chats/' + encodeURIComponent(conversationId) + '/compaction');
    } catch (loadError) {
        gridElement.replaceChildren();
        summaryElement.textContent = "Couldn't load this chat: " + loadError.message;
        return;
    }

    // Both numbers are message indexes: everything before them is summarized, or has its tool results shortened.
    gridElement.replaceChildren();
    addReadout(gridElement, 'stored messages', compaction.messageCount.toLocaleString() + ' (yours, hers and tool results)');
    addReadout(gridElement, 'context last turn', compaction.lastContextTokens ? formatTokens(compaction.lastContextTokens) + ' tokens' : '—');
    addReadout(gridElement, 'summarized', compaction.summaryCoversThrough > 0 ? 'the first ' + compaction.summaryCoversThrough + ' messages' : 'nothing yet');
    addReadout(gridElement, 'tool results shortened', compaction.toolResultsCollapsedBefore > 0 ? 'before message ' + compaction.toolResultsCollapsedBefore : 'none');
    summaryElement.textContent = compaction.summary || 'This chat hasn\'t been compacted, so there is no summary. She still sees every message.';
}

// ---------- old categories ----------

async function loadLegacyBreakdown() {
    const rowsElement = document.getElementById('legacyRows');
    let breakdownRows;
    try {
        breakdownRows = await fetchJson('/memory/breakdown');
    } catch (loadError) {
        rowsElement.replaceChildren();
        addElement(addElement(rowsElement, 'tr'), 'td', 'readout-empty', "couldn't load: " + loadError.message).colSpan = 4;
        return;
    }

    const rowsFragment = document.createDocumentFragment();
    for (const breakdownRow of breakdownRows) {
        const tableRow = addElement(rowsFragment, 'tr', 'clickable');
        addElement(tableRow, 'td', null, breakdownRow.category || '(none)');
        addElement(tableRow, 'td', 'mono', breakdownRow.source);
        addElement(tableRow, 'td', 'mono', breakdownRow.needsReview ? 'yes' : '');
        addElement(tableRow, 'td', 'mono', breakdownRow.count.toLocaleString());
        tableRow.tabIndex = 0;
        tableRow.addEventListener('click', () => showLegacyCategory(breakdownRow.category, tableRow));
        tableRow.addEventListener('keydown', keyEvent => { if (keyEvent.key === 'Enter') showLegacyCategory(breakdownRow.category, tableRow); });
    }
    rowsElement.replaceChildren(rowsFragment);
}

async function showLegacyCategory(category, tableRow) {
    for (const selectedRow of document.querySelectorAll('#legacyRows tr.selected')) selectedRow.classList.remove('selected');
    tableRow.classList.add('selected');
    const memoriesElement = document.getElementById('legacyMemories');
    showEmpty(memoriesElement, 'loading…');
    let categoryMemories;
    try {
        categoryMemories = await fetchJson('/memory/category/' + encodeURIComponent(category || ''));
    } catch (loadError) {
        showEmpty(memoriesElement, "couldn't load: " + loadError.message);
        return;
    }
    const memoriesFragment = document.createDocumentFragment();
    addElement(memoriesFragment, 'div', 'bench-subheading mono', (category || '(none)').toUpperCase() + ' · ' + categoryMemories.length + (categoryMemories.length === 100 ? '+ (first 100)' : '') + ' memories');
    for (const categoryMemory of categoryMemories) {
        const rowElement = addElement(memoriesFragment, 'article', 'probe-row');
        addElement(rowElement, 'p', 'memory-content clamped-two', categoryMemory.content);
    }
    memoriesElement.replaceChildren(memoriesFragment);
}

// ---------- permissions ----------

const permissionKindLabels = { web: 'web', write: 'file writes', command: 'commands' };

async function loadPermissions() {
    const rowsElement = document.getElementById('permissionRows');
    let rules;
    try {
        rules = await fetchJson('/approvals/rules');
    } catch (loadError) {
        rowsElement.replaceChildren();
        addElement(addElement(rowsElement, 'tr'), 'td', 'readout-empty', "couldn't load: " + loadError.message).colSpan = 4;
        return;
    }

    const rowsFragment = document.createDocumentFragment();
    for (const rule of rules) {
        const tableRow = addElement(rowsFragment, 'tr');
        addElement(tableRow, 'td', 'mono', permissionKindLabels[rule.category] || rule.category);
        addElement(tableRow, 'td', 'mono', rule.key);
        addElement(tableRow, 'td', 'mono', formatAge(rule.createdAt));
        const actionsCell = addElement(tableRow, 'td', 'table-actions');
        const removeButton = addElement(actionsCell, 'button', 'turn-action danger', 'REMOVE');
        removeButton.type = 'button';
        removeButton.addEventListener('click', async () => {
            removeButton.disabled = true;
            try {
                const response = await fetch('/approvals/rules/' + encodeURIComponent(rule.id), { method: 'DELETE' });
                if (!response.ok && response.status !== 404) throw new Error(await readErrorText(response));
            } catch (removeError) {
                removeButton.disabled = false;
                await showNotice("COULDN'T REMOVE", removeError.message);
                return;
            }
            loadPermissions();
        });
    }
    if (rules.length === 0) {
        const emptyCell = addElement(addElement(rowsFragment, 'tr'), 'td', 'readout-empty', 'nothing yet · she asks every time');
        emptyCell.colSpan = 4;
    }
    rowsElement.replaceChildren(rowsFragment);
}

// ---------- behavior tests ----------

let behaviorPollTimer = null;

function formatSeconds(seconds) {
    return seconds >= 60 ? Math.floor(seconds / 60) + 'm ' + Math.round(seconds % 60) + 's' : Math.round(seconds) + 's';
}

async function startBehaviorTests(testId) {
    const runUrl = testId ? '/tests/run?id=' + encodeURIComponent(testId) : '/tests/run';
    try {
        const response = await fetch(runUrl, { method: 'POST' });
        if (!response.ok && response.status !== 409) throw new Error(await readErrorText(response));
    } catch (runError) {
        await showNotice("COULDN'T START", runError.message);
        return;
    }
    loadBehaviorTests();
}

function addBehaviorDetail(rowsFragment, result) {
    const detailRow = addElement(rowsFragment, 'tr', 'behavior-detail');
    detailRow.hidden = true;
    const detailCell = addElement(detailRow, 'td');
    detailCell.colSpan = 5;
    for (const failure of result.failures) addElement(detailCell, 'div', 'behavior-failure mono', '✕ ' + failure);
    addElement(detailCell, 'pre', 'bench-output', result.reply || '(no reply)');
    return detailRow;
}

async function loadBehaviorTests() {
    clearTimeout(behaviorPollTimer);
    const rowsElement = document.getElementById('behaviorRows');
    const statusElement = document.getElementById('behaviorStatus');
    const runButton = document.getElementById('behaviorRunButton');
    let testData;
    try {
        testData = await fetchJson('/tests');
    } catch (loadError) {
        rowsElement.replaceChildren();
        addElement(addElement(rowsElement, 'tr'), 'td', 'readout-empty', "couldn't load: " + loadError.message).colSpan = 5;
        return;
    }

    const runStatus = testData.status;
    runButton.disabled = runStatus.running;
    statusElement.textContent = runStatus.running
        ? runStatus.progress
        : runStatus.lastRunAt ? runStatus.passed + ' / ' + runStatus.total + ' passed · ' + formatAge(runStatus.lastRunAt) : 'not run yet';

    const resultsById = new Map(testData.results.map(result => [result.id, result]));
    const rowsFragment = document.createDocumentFragment();
    for (const behaviorTest of testData.tests) {
        const result = resultsById.get(behaviorTest.id);
        const tableRow = addElement(rowsFragment, 'tr');
        const titleCell = addElement(tableRow, 'td', 'behavior-title');
        addElement(titleCell, 'div', null, behaviorTest.title);
        addElement(titleCell, 'div', 'document-source', behaviorTest.prompt);
        const resultCell = addElement(tableRow, 'td', 'mono');
        if (result) {
            addElement(resultCell, 'span', result.passed ? 'behavior-pass' : 'behavior-fail', result.passed ? 'PASS' : 'FAIL');
        } else {
            resultCell.textContent = '—';
        }
        addElement(tableRow, 'td', 'mono', result ? result.toolsCalled.join(', ') || 'none' : '');
        addElement(tableRow, 'td', 'mono', result ? formatSeconds(result.seconds) : '');
        const actionsCell = addElement(tableRow, 'td', 'table-actions');
        if (result) {
            const detailRow = addBehaviorDetail(rowsFragment, result);
            const showButton = addElement(actionsCell, 'button', 'turn-action', 'REPLY');
            showButton.type = 'button';
            showButton.addEventListener('click', () => { detailRow.hidden = !detailRow.hidden; });
        }
        const runOneButton = addElement(actionsCell, 'button', 'turn-action', 'RUN');
        runOneButton.type = 'button';
        runOneButton.disabled = runStatus.running;
        runOneButton.addEventListener('click', () => startBehaviorTests(behaviorTest.id));
    }
    rowsElement.replaceChildren(rowsFragment);

    // While a run is going, check back every few seconds.
    if (runStatus.running) behaviorPollTimer = setTimeout(loadBehaviorTests, 4000);
}

// ---------- wiring ----------

for (const tabButton of benchTabButtons) tabButton.addEventListener('click', () => showBenchView(tabButton.dataset.view));

document.getElementById('probeForm').addEventListener('submit', submitEvent => {
    submitEvent.preventDefault();
    const probeText = document.getElementById('probeInput').value.trim();
    if (probeText) runProbe(probeText);
});

document.getElementById('toolRunButton').addEventListener('click', runSelectedTool);
document.getElementById('behaviorRunButton').addEventListener('click', () => startBehaviorTests(null));
document.getElementById('toolForm').addEventListener('submit', submitEvent => {
    submitEvent.preventDefault();
    runSelectedTool();
});
document.getElementById('toolForm').addEventListener('keydown', keyEvent => {
    if (keyEvent.key === 'Enter' && keyEvent.target.tagName === 'INPUT') {
        keyEvent.preventDefault();
        runSelectedTool();
    }
});

document.getElementById('internalsChatSelect').addEventListener('change', changeEvent => showChatInternals(changeEvent.target.value));
document.getElementById('internalsOpenButton').addEventListener('click', () => {
    const conversationId = document.getElementById('internalsChatSelect').value;
    if (conversationId) openChat(conversationId);
});

const savedBenchView = readStorage(benchViewStorageKey);
showBenchView([...benchTabButtons].some(tabButton => tabButton.dataset.view === savedBenchView) ? savedBenchView : 'probe');