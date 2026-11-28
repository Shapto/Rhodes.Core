// Rhodes home page: the ENGINE panel (llama.cpp or RustingStrata, from Data/engines.ini), the MODEL panel (switch
// Closure's model through llama-server's router mode, or RustingStrata's models file, and calibrate RustingStrata)
// and the SETTINGS panel. Needs rhodes-common.js loaded first.
'use strict';

const modelPollInterval = 5000;
// While an engine starts, stops or calibrates, the panels follow it more closely.
const busyPollInterval = 2000;

const engineListElement = document.getElementById('engineList');
const engineNoteElement = document.getElementById('engineNote');
const engineProgressElement = document.getElementById('engineProgress');
const engineHintElement = document.getElementById('engineHint');

const modelListElement = document.getElementById('modelList');
const modelNoteElement = document.getElementById('modelNote');
const modelHintElement = document.getElementById('modelHint');
const settingsListElement = document.getElementById('settingsList');
const settingsMetaElement = document.getElementById('settingsMeta');
const restartButton = document.getElementById('restartButton');

let modelActionRunning = false;
let engineIsBusy = false;
let engineIsRunning = false;

const busyEngineStates = ['starting', 'stopping', 'calibrating'];

// ---------- engine ----------

function describeEngineMeta(engine) {
    const metaParts = [engine.kind, engine.baseUrl];
    if (engine.contextTokens) metaParts.push(formatTokens(engine.contextTokens) + ' ctx');
    return metaParts.join(' · ');
}

function formatElapsed(startedAt) {
    const elapsedMinutes = Math.floor((Date.now() - new Date(startedAt).getTime()) / 60000);
    return elapsedMinutes < 1 ? 'under a minute' : elapsedMinutes + ' min';
}

function renderEngines(engineState) {
    engineListElement.textContent = '';
    engineIsBusy = busyEngineStates.includes(engineState.state);
    engineIsRunning = engineState.state === 'running';
    engineNoteElement.textContent = engineState.current ? engineState.state.toUpperCase() + ' · ' + engineState.current : '';

    for (const engine of engineState.engines) {
        const rowElement = addElement(engineListElement, 'div', 'model-row' + (engine.isCurrent ? ' current' : ''));
        const topElement = addElement(rowElement, 'div', 'model-top');
        addElement(topElement, 'span', 'model-name mono', engine.name);
        if (engine.isCurrent) {
            const statusElement = addElement(topElement, 'span', 'model-status mono', engineState.state.toUpperCase());
            statusElement.dataset.status = engineState.state;
        }
        addElement(rowElement, 'span', 'model-meta mono', describeEngineMeta(engine));

        const actionsElement = addElement(rowElement, 'div', 'model-actions');
        if (!engine.isCurrent) {
            const useButton = addElement(actionsElement, 'button', 'turn-action', 'USE');
            useButton.type = 'button';
            useButton.disabled = engineIsBusy || modelActionRunning || !engine.canStart;
            useButton.title = engine.canStart
                ? 'Run Closure on ' + engine.name + '. The running engine\'s server is stopped first.'
                : 'Add a command for it in Data/engines.ini first.';
            useButton.addEventListener('click', () => switchEngine(engine.name));
        } else {
            if (engineState.state === 'stopped' || engineState.state === 'failed') {
                const startButton = addElement(actionsElement, 'button', 'turn-action', 'START');
                startButton.type = 'button';
                startButton.disabled = modelActionRunning || !engine.canStart;
                startButton.title = 'Start this engine\'s server.';
                startButton.addEventListener('click', () => runModelAction(() => postJson('/engines/start', {})));
            }
            if (engineState.state === 'running' || engineState.state === 'starting') {
                const stopButton = addElement(actionsElement, 'button', 'turn-action', 'STOP');
                stopButton.type = 'button';
                stopButton.disabled = modelActionRunning;
                stopButton.title = 'Stop this engine\'s server and free the GPU. Closure can\'t answer until it\'s started again.';
                stopButton.addEventListener('click', () => runModelAction(() => postJson('/engines/stop', {})));
            }
            addElement(actionsElement, 'span', 'model-current-mark label', 'ACTIVE');
        }
    }

    // Progress while busy: what the engine is doing and the newest line of its log.
    const progressParts = [];
    if (engineIsBusy) {
        progressParts.push(engineState.detail);
        if (engineState.state === 'calibrating' && engineState.calibrationStartedAt) {
            progressParts.push('running for ' + formatElapsed(engineState.calibrationStartedAt) + ' (usually up to ~30 min)');
        }
        if (engineState.lastLogLine) progressParts.push('log: ' + engineState.lastLogLine);
    }
    engineProgressElement.textContent = progressParts.join(' · ');
    engineProgressElement.hidden = progressParts.length === 0;

    const hintParts = [];
    if (engineState.state === 'failed' && engineState.lastError) hintParts.push('Failed: ' + engineState.lastError);
    if (engineState.catalogProblem) hintParts.push('engines.ini: ' + engineState.catalogProblem);
    if (engineState.lastCalibrationResult) hintParts.push('Last calibration ' + engineState.lastCalibrationResult + '.');
    if (engineState.engines.length === 0) hintParts.push('Add engines to ' + engineState.enginesFilePath + '.');
    engineHintElement.textContent = hintParts.join(' ');
    engineHintElement.hidden = hintParts.length === 0;
}

async function loadEngines() {
    try {
        const response = await fetch('/engines', { cache: 'no-store' });
        if (!response.ok) throw new Error(await readErrorText(response));
        renderEngines(await response.json());
    } catch {
        engineIsBusy = false;
        engineNoteElement.textContent = '';
        engineListElement.textContent = '';
        engineProgressElement.hidden = true;
        engineHintElement.hidden = true;
        addElement(engineListElement, 'span', 'readout-empty', "can't reach the core");
    }
}

async function switchEngine(engineName) {
    const confirmed = await showDialog({
        title: 'SWITCH ENGINE',
        message: 'Run Closure on ' + engineName + '?',
        detail: 'The running engine\'s server stops and this one starts with its current model, which takes a minute or two. A reply that is running right now fails, so wait until she is done.',
        confirmText: 'SWITCH',
        cancelText: 'CANCEL',
    });
    if (!confirmed) return;
    await runModelAction(() => postJson('/engines/select', { engine: engineName }));
}

async function calibrateModel(modelId) {
    const confirmed = await showDialog({
        title: 'CALIBRATE',
        message: 'Calibrate ' + modelId + ' on RustingStrata?',
        detail: 'RustingStrata measures the fastest settings for this model on this laptop and keeps them for every later run. It usually takes up to ~30 minutes, and Closure can\'t answer until it\'s done. Close other GPU programs first so the measurement is fair.',
        confirmText: 'CALIBRATE',
        cancelText: 'CANCEL',
    });
    if (!confirmed) return;
    await runModelAction(() => postJson('/engines/calibrate', {}));
}

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
    if (routerModel.calibrated === true) metaParts.push('calibrated');
    if (routerModel.calibrated === false) metaParts.push('not calibrated');
    if (routerModel.filePath) metaParts.push(routerModel.filePath.split(/[\\/]/).pop());
    return metaParts.join(' · ');
}

function renderModels(modelState) {
    modelListElement.textContent = '';
    modelHintElement.hidden = true;
    const isStrata = modelState.engineKind === 'rustingstrata';

    if (isStrata && modelState.models.length === 0) {
        modelNoteElement.textContent = 'RustingStrata';
        addElement(modelListElement, 'span', 'readout-empty', 'no models yet');
        modelHintElement.textContent = 'Add a [section] with model = file.gguf to Data/strata-models.ini (the engine\'s models file).';
        modelHintElement.hidden = false;
        return;
    }
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
        const isBusy = routerModel.status === 'loading' || routerModel.status === 'downloading' || routerModel.status === 'calibrating';
        // RustingStrata restarts its server for every switch, so nothing else may run while the engine is busy.
        const blockedByEngine = isStrata && engineIsBusy;
        if (!isCurrent || !isLoaded) {
            const useButton = addElement(actionsElement, 'button', 'turn-action', isCurrent ? 'LOAD' : 'USE');
            useButton.type = 'button';
            useButton.disabled = isBusy || modelActionRunning || blockedByEngine;
            useButton.title = isCurrent ? 'Load this model again' : 'Make this Closure\'s model. The current one is unloaded first; her chats stay as they are.';
            useButton.addEventListener('click', () => selectModel(routerModel.id, isCurrent));
        }
        if ((isLoaded || isBusy) && routerModel.status !== 'calibrating') {
            const unloadButton = addElement(actionsElement, 'button', 'turn-action', 'UNLOAD');
            unloadButton.type = 'button';
            unloadButton.disabled = modelActionRunning;
            unloadButton.title = isStrata
                ? 'Stop RustingStrata and free the GPU. Closure can\'t answer until you press LOAD again.'
                : 'Free the GPU. Closure can\'t answer until you press LOAD again.';
            unloadButton.addEventListener('click', () => unloadModel(routerModel.id));
        }
        if (isStrata && isCurrent) {
            const calibrateButton = addElement(actionsElement, 'button', 'turn-action', routerModel.calibrated ? 'RECALIBRATE' : 'CALIBRATE');
            calibrateButton.type = 'button';
            calibrateButton.disabled = modelActionRunning || blockedByEngine;
            calibrateButton.title = 'Measure the fastest RustingStrata settings for this model on this machine (once per model; again after a driver, GPU or quant change).';
            calibrateButton.addEventListener('click', () => calibrateModel(routerModel.id));
        }
        if (isCurrent) addElement(actionsElement, 'span', 'model-current-mark label', 'CLOSURE');
    }

    if (isStrata) {
        const currentModel = modelState.models.find(routerModel => routerModel.id === modelState.currentModel);
        if (currentModel && currentModel.calibrated === false && engineIsRunning) {
            modelHintElement.textContent = 'This model isn\'t calibrated on this machine yet, so RustingStrata uses default settings. CALIBRATE measures better ones.';
            modelHintElement.hidden = false;
        }
        return;
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
        await refreshEngineAndModels();
    }
}

async function refreshEngineAndModels() {
    // The engine first: the model panel reads whether it's busy.
    await loadEngines();
    await loadModels();
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
loadSettings();

async function pollEngineAndModels() {
    if (!modelActionRunning && !isDialogOpen()) await refreshEngineAndModels();
    setTimeout(pollEngineAndModels, engineIsBusy ? busyPollInterval : modelPollInterval);
}
pollEngineAndModels();
