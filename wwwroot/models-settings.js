// Rhodes home page: the MODEL panel (switch Closure's model through llama-server's router mode) and the SETTINGS panel.
// Needs rhodes-common.js loaded first.
'use strict';

const modelPollInterval = 5000;

const modelListElement = document.getElementById('modelList');
const modelNoteElement = document.getElementById('modelNote');
const modelHintElement = document.getElementById('modelHint');
const settingsListElement = document.getElementById('settingsList');
const settingsMetaElement = document.getElementById('settingsMeta');
const restartButton = document.getElementById('restartButton');

let modelActionRunning = false;

// ---------- model ----------

async function postJson(url, body) {
    const response = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
    });
    if (!response.ok) throw new Error(await readErrorText(response));
    const responseText = await response.text();
    return responseText ? JSON.parse(responseText) : null;
}

function describeModelMeta(routerModel) {
    const metaParts = [];
    metaParts.push(routerModel.contextTokens ? formatTokens(routerModel.contextTokens) + ' ctx' : 'ctx not set');
    if (routerModel.hasVision) metaParts.push('vision');
    if (routerModel.filePath) metaParts.push(routerModel.filePath.split(/[\\/]/).pop());
    return metaParts.join(' · ');
}

function renderModels(modelState) {
    modelListElement.textContent = '';
    modelHintElement.hidden = true;

    if (!modelState.reachable) {
        modelNoteElement.textContent = 'llama-server offline';
        addElement(modelListElement, 'span', 'readout-empty', "can't reach llama-server");
        return;
    }
    if (!modelState.isRouter) {
        modelNoteElement.textContent = 'single model';
        addElement(modelListElement, 'span', 'readout-empty', 'llama-server runs one fixed model');
        modelHintElement.textContent = 'To switch models here, start llama-server in router mode with a models.ini (see Start-Rhodes.sh).';
        modelHintElement.hidden = false;
        return;
    }

    modelNoteElement.textContent = formatTokens(modelState.contextTokens) + ' ctx · vision ' + (modelState.visionEnabled ? 'on' : 'off');
    for (const routerModel of modelState.models) {
        const isCurrent = routerModel.id === modelState.currentModel;
        const rowElement = addElement(modelListElement, 'div', 'model-row' + (isCurrent ? ' current' : ''));
        const topElement = addElement(rowElement, 'div', 'model-top');
        addElement(topElement, 'span', 'model-name mono', routerModel.id);
        const statusElement = addElement(topElement, 'span', 'model-status mono', routerModel.status.toUpperCase());
        statusElement.dataset.status = routerModel.status;
        addElement(rowElement, 'span', 'model-meta mono', describeModelMeta(routerModel));

        const actionsElement = addElement(rowElement, 'div', 'model-actions');
        const isLoaded = routerModel.status === 'loaded' || routerModel.status === 'sleeping';
        const isBusy = routerModel.status === 'loading' || routerModel.status === 'downloading';
        if (!isCurrent || !isLoaded) {
            const useButton = addElement(actionsElement, 'button', 'turn-action', isCurrent ? 'LOAD' : 'USE');
            useButton.type = 'button';
            useButton.disabled = isBusy || modelActionRunning;
            useButton.title = isCurrent ? 'Load this model again' : 'Make this Closure\'s model. The current one is unloaded first; her chats stay as they are.';
            useButton.addEventListener('click', () => selectModel(routerModel.id, isCurrent));
        }
        if (isLoaded || isBusy) {
            const unloadButton = addElement(actionsElement, 'button', 'turn-action', 'UNLOAD');
            unloadButton.type = 'button';
            unloadButton.disabled = modelActionRunning;
            unloadButton.title = 'Free the GPU. Closure can\'t answer until you press LOAD again.';
            unloadButton.addEventListener('click', () => unloadModel(routerModel.id));
        }
        if (isCurrent) addElement(actionsElement, 'span', 'model-current-mark label', 'CLOSURE');
    }

    if (modelState.lastLoadError) {
        modelHintElement.textContent = 'Last load failed: ' + modelState.lastLoadError;
        modelHintElement.hidden = false;
    } else if (modelState.autoLoadPaused) {
        modelHintElement.textContent = 'Closure\'s model is unloaded. Press LOAD when you want her back.';
        modelHintElement.hidden = false;
    }
}

async function loadModels() {
    try {
        const response = await fetch('/models', { cache: 'no-store' });
        if (!response.ok) throw new Error(await readErrorText(response));
        renderModels(await response.json());
    } catch {
        modelNoteElement.textContent = '';
        modelListElement.textContent = '';
        addElement(modelListElement, 'span', 'readout-empty', "can't reach the core");
    }
}

async function selectModel(modelId, isCurrent) {
    if (!isCurrent) {
        const confirmed = await showDialog({
            title: 'SWITCH MODEL',
            message: 'Switch Closure to ' + modelId + '?',
            detail: 'The current model is unloaded and this one loads, which takes a minute or two. A reply that is running right now fails, so wait until she is done.',
            confirmText: 'SWITCH',
            cancelText: 'CANCEL',
        });
        if (!confirmed) return;
    }
    await runModelAction(() => postJson('/models/select', { model: modelId }));
}

async function unloadModel(modelId) {
    await runModelAction(() => postJson('/models/unload', { model: modelId }));
}

async function runModelAction(action) {
    modelActionRunning = true;
    try {
        await action();
    } catch (error) {
        await showNotice('MODEL', error.message);
    } finally {
        modelActionRunning = false;
        await loadModels();
    }
}

// ---------- settings ----------

function createSettingInput(setting) {
    if (setting.kind === 'choice' || setting.kind === 'boolean') {
        const selectElement = document.createElement('select');
        selectElement.className = 'memory-sort mono setting-input';
        const choices = setting.kind === 'boolean' ? ['true', 'false'] : setting.choices;
        for (const choice of choices) {
            const optionElement = addElement(selectElement, 'option', null, setting.kind === 'boolean' ? (choice === 'true' ? 'yes' : 'no') : choice);
            optionElement.value = choice;
        }
        selectElement.value = setting.value;
        return selectElement;
    }

    const inputElement = document.createElement('input');
    inputElement.className = 'memory-search setting-input';
    inputElement.type = setting.kind === 'number' ? 'number' : 'text';
    inputElement.value = setting.value;
    inputElement.spellcheck = false;
    return inputElement;
}

function renderSettings(settings) {
    settingsListElement.textContent = '';
    const anyPending = settings.some(setting => setting.restartPending);
    settingsMetaElement.textContent = anyPending ? 'some saved settings wait for a restart' : '';

    for (const setting of settings) {
        const rowElement = addElement(settingsListElement, 'div', 'setting-row');
        const labelColumn = addElement(rowElement, 'div', 'setting-text');
        const titleElement = addElement(labelColumn, 'div', 'setting-title');
        addElement(titleElement, 'span', 'setting-label', setting.label);
        addElement(titleElement, 'span', 'setting-badge mono' + (setting.live ? ' live' : ''), setting.live ? 'LIVE' : 'RESTART');
        if (setting.restartPending) addElement(titleElement, 'span', 'setting-badge mono pending', 'RESTART PENDING');
        addElement(labelColumn, 'span', 'setting-description', setting.description);
        addElement(labelColumn, 'span', 'setting-key mono', setting.key);

        const inputElement = createSettingInput(setting);
        rowElement.appendChild(inputElement);
        let savedValue = setting.value;

        async function saveSetting() {
            if (inputElement.value === savedValue) return;
            inputElement.disabled = true;
            try {
                renderSettings(await postJson('/settings', { key: setting.key, value: inputElement.value }));
            } catch (error) {
                inputElement.disabled = false;
                inputElement.value = savedValue;
                await showNotice('SETTINGS', error.message);
            }
        }

        // Selects save as soon as they change; text and numbers when you leave the box or press Enter.
        inputElement.addEventListener('change', saveSetting);
        inputElement.addEventListener('keydown', keyEvent => {
            if (keyEvent.key === 'Enter') {
                keyEvent.preventDefault();
                inputElement.blur();
            }
        });
    }
}

async function loadSettings() {
    try {
        const response = await fetch('/settings', { cache: 'no-store' });
        if (!response.ok) throw new Error(await readErrorText(response));
        renderSettings(await response.json());
    } catch {
        settingsListElement.textContent = '';
        addElement(settingsListElement, 'span', 'readout-empty', "can't load the settings");
    }
}

async function restartCore() {
    const confirmed = await showDialog({
        title: 'RESTART CORE',
        message: 'Restart the core so saved settings apply?',
        detail: 'Takes a few seconds; the model stays loaded. A reply that is running right now is stopped.',
        confirmText: 'RESTART',
        cancelText: 'CANCEL',
    });
    if (!confirmed) return;

    restartButton.disabled = true;
    try {
        await postJson('/core/restart', {});
    } catch (error) {
        restartButton.disabled = false;
        await showNotice('RESTART CORE', error.message);
        return;
    }

    settingsMetaElement.textContent = 'restarting…';
    // Wait for the old core to go away and the new one to answer, then reload the page.
    const startedAt = Date.now();
    await new Promise(resolve => setTimeout(resolve, 2000));
    while (Date.now() - startedAt < 60000) {
        try {
            const response = await fetch('/status', { cache: 'no-store', signal: AbortSignal.timeout(2000) });
            if (response.ok) {
                window.location.reload();
                return;
            }
        } catch { }
        await new Promise(resolve => setTimeout(resolve, 1000));
    }
    settingsMetaElement.textContent = "the core didn't come back within a minute · check Logs/core.log";
    restartButton.disabled = false;
}

restartButton.addEventListener('click', restartCore);
loadModels();
loadSettings();
setInterval(() => {
    if (!modelActionRunning && !isDialogOpen()) loadModels();
}, modelPollInterval);
