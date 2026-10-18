// Rhodes home page: Closure's cleanup proposals for her memory, in the memory panel. Nothing changes until Shapto accepts one.
// Needs rhodes-common.js and home.js loaded first (uses addElement, readErrorText, showNotice, formatAge, loadStatistics, loadMemories).
'use strict';

const proposalKindLabels = { merge: 'MERGE', archive: 'ARCHIVE', retype: 'RETYPE', delete: 'DELETE' };
const reviewPollMilliseconds = 5000;

const reviewButton = document.getElementById('reviewButton');
const reviewStatusElement = document.getElementById('reviewStatus');
const proposalListElement = document.getElementById('proposalList');
let reviewPollTimer = null;

function describeProposalChange(proposal) {
    if (proposal.kind === 'merge') return 'into one ' + proposal.newType + ' memory:';
    if (proposal.kind === 'retype') return 'to ' + proposal.newType;
    if (proposal.kind === 'archive') return 'out of her way (still searchable)';
    return 'for good';
}

function createProposalCard(proposalView) {
    const proposal = proposalView.proposal;
    const cardElement = document.createElement('article');
    cardElement.className = 'proposal-card';
    cardElement.dataset.kind = proposal.kind;
    const topLine = addElement(cardElement, 'div', 'memory-top');
    addElement(topLine, 'span', 'proposal-kind', proposalKindLabels[proposal.kind] || proposal.kind.toUpperCase());
    addElement(topLine, 'span', null, describeProposalChange(proposal));
    if (proposal.reason) addElement(cardElement, 'p', 'proposal-reason', proposal.reason);

    for (const memory of proposalView.memories) {
        const memoryLine = addElement(cardElement, 'div', 'proposal-memory');
        memoryLine.dataset.type = memory.type;
        addElement(memoryLine, 'span', 'type-badge', memory.type.toUpperCase());
        addElement(memoryLine, 'span', 'proposal-memory-text', memory.content);
    }
    if (proposal.kind === 'merge') {
        const resultLine = addElement(cardElement, 'div', 'proposal-memory proposal-result');
        resultLine.dataset.type = proposal.newType;
        addElement(resultLine, 'span', 'type-badge', '→ ' + proposal.newType.toUpperCase());
        addElement(resultLine, 'span', 'proposal-memory-text', proposal.newContent);
    }

    const actionsLine = addElement(cardElement, 'div', 'memory-actions');
    const acceptButton = addElement(actionsLine, 'button', 'turn-action' + (proposal.kind === 'delete' ? ' danger' : ''), proposal.kind === 'delete' ? 'ACCEPT · DELETE' : 'ACCEPT');
    acceptButton.type = 'button';
    acceptButton.addEventListener('click', () => decideProposal(proposal, 'accept', cardElement));
    const rejectButton = addElement(actionsLine, 'button', 'turn-action', 'REJECT');
    rejectButton.type = 'button';
    rejectButton.addEventListener('click', () => decideProposal(proposal, 'reject', cardElement));
    return cardElement;
}

async function decideProposal(proposal, decision, cardElement) {
    cardElement.querySelectorAll('button').forEach(button => { button.disabled = true; });
    let outcome;
    try {
        const response = await fetch('/memory/proposals/' + encodeURIComponent(proposal.id), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ decision }),
        });
        if (!response.ok) throw new Error(await readErrorText(response));
        outcome = (await response.json()).outcome;
    } catch (decideError) {
        cardElement.querySelectorAll('button').forEach(button => { button.disabled = false; });
        await showNotice("COULDN'T APPLY IT", decideError.message);
        return;
    }
    if (outcome === 'stale') await showNotice('ALREADY CHANGED', 'One of these memories was changed or removed since she proposed this, so nothing was applied.');
    cardElement.remove();
    loadProposals();
    if (outcome === 'accepted') {
        loadStatistics();
        loadMemories(true);
    }
}

async function loadProposals() {
    clearTimeout(reviewPollTimer);
    let reviewData;
    try {
        const response = await fetch('/memory/proposals');
        if (!response.ok) throw new Error(await readErrorText(response));
        reviewData = await response.json();
    } catch {
        reviewStatusElement.textContent = "can't reach the core";
        return;
    }

    const reviewStatus = reviewData.status;
    reviewButton.disabled = reviewStatus.running;
    reviewButton.textContent = reviewStatus.running ? 'REVIEWING…' : 'ASK CLOSURE TO REVIEW';
    const statusParts = [];
    if (reviewStatus.running) statusParts.push('reviewing · ' + reviewStatus.progress);
    else if (reviewStatus.lastError) statusParts.push('last review failed: ' + reviewStatus.lastError);
    else if (reviewStatus.lastRunAt) statusParts.push('last review ' + formatAge(reviewStatus.lastRunAt));
    if (reviewData.proposals.length > 0) statusParts.push(reviewData.proposals.length + (reviewData.proposals.length === 1 ? ' proposal waits for you' : ' proposals wait for you'));
    reviewStatusElement.textContent = statusParts.join(' · ');

    const cardsFragment = document.createDocumentFragment();
    for (const proposalView of reviewData.proposals) cardsFragment.appendChild(createProposalCard(proposalView));
    proposalListElement.replaceChildren(cardsFragment);

    if (reviewStatus.running) reviewPollTimer = setTimeout(loadProposals, reviewPollMilliseconds);
}

reviewButton.addEventListener('click', async () => {
    reviewButton.disabled = true;
    try {
        const response = await fetch('/memory/proposals/run', { method: 'POST' });
        if (!response.ok && response.status !== 409) throw new Error(await readErrorText(response));
    } catch (startError) {
        reviewButton.disabled = false;
        await showNotice("COULDN'T START THE REVIEW", startError.message);
        return;
    }
    loadProposals();
});

loadProposals();
