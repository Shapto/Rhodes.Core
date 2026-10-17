// Chat page: the question card Closure sends with ask_shapto, and the chip for journal entries she brought into a chat.
// Load before rhodes.js: rhodes.js calls addQuestionCard while drawing a chat. Uses addElement from rhodes-common.js,
// and sendMessage / sendButton from rhodes.js when an answer is sent.
'use strict';

/**
 * Draws the card inside her message. An answered card (a later message exists) shows the options greyed out.
 */
function addQuestionCard(closureMessage, questions, isAnswered) {
    const cardElement = addElement(closureMessage.bodyElement, 'div', 'question-card cut');
    const headerLine = addElement(cardElement, 'div', 'question-card-header label');
    addElement(headerLine, 'span', null, questions.length === 1 ? 'A QUESTION FOR YOU' : questions.length + ' QUESTIONS FOR YOU');
    const stateLabel = addElement(headerLine, 'span', 'question-card-state mono', isAnswered ? 'ANSWERED' : 'pick or type, then send');

    const questionStates = questions.map((question, questionIndex) => {
        const questionElement = addElement(cardElement, 'div', 'question');
        const promptLine = addElement(questionElement, 'div', 'question-prompt');
        addElement(promptLine, 'span', 'question-number mono', String(questionIndex + 1));
        addElement(promptLine, 'span', null, question.question);
        if (question.multiSelect) addElement(promptLine, 'span', 'question-hint mono', 'pick any');

        const optionsLine = addElement(questionElement, 'div', 'question-options');
        const questionState = { question, selectedOptions: new Set(), optionButtons: [], freeTextInput: null };
        for (const optionText of question.options) {
            const optionButton = addElement(optionsLine, 'button', 'question-option', optionText);
            optionButton.type = 'button';
            optionButton.setAttribute('aria-pressed', 'false');
            optionButton.disabled = isAnswered;
            optionButton.addEventListener('click', () => toggleOption(questionState, optionText, optionButton));
            questionState.optionButtons.push(optionButton);
        }

        const freeTextInput = addElement(questionElement, 'input', 'question-free-text');
        freeTextInput.type = 'text';
        freeTextInput.placeholder = question.multiSelect ? 'or add your own…' : 'or type your own answer…';
        freeTextInput.setAttribute('aria-label', 'Your own answer to question ' + (questionIndex + 1));
        freeTextInput.disabled = isAnswered;
        freeTextInput.addEventListener('keydown', keyEvent => {
            if (keyEvent.key === 'Enter') {
                keyEvent.preventDefault();
                sendAnswers();
            }
        });
        // Typing an answer to a single-choice question replaces a picked option.
        freeTextInput.addEventListener('input', () => {
            if (!question.multiSelect && freeTextInput.value.trim()) clearSelection(questionState);
        });
        questionState.freeTextInput = freeTextInput;
        return questionState;
    });

    const footerLine = addElement(cardElement, 'div', 'question-card-footer');
    const sendAnswersButton = addElement(footerLine, 'button', 'send cut-small label', 'SEND ANSWERS');
    sendAnswersButton.type = 'button';
    sendAnswersButton.hidden = isAnswered;
    sendAnswersButton.addEventListener('click', sendAnswers);

    function sendAnswers() {
        if (cardElement.classList.contains('answered') || sendButton.disabled) return;
        const answerLines = questionStates.map((questionState, questionIndex) => {
            const pickedText = [...questionState.selectedOptions].join(', ');
            const typedText = questionState.freeTextInput.value.trim();
            const answerText = pickedText && typedText ? pickedText + '; ' + typedText : pickedText || typedText || '(skipped)';
            return (questionIndex + 1) + '. ' + questionState.question.question + ' → ' + answerText;
        });
        if (answerLines.every(answerLine => answerLine.endsWith('→ (skipped)'))) {
            stateLabel.textContent = 'answer at least one first';
            return;
        }
        markAnswered();
        sendMessage(answerLines.join('\n'), [], null);
    }

    function markAnswered() {
        cardElement.classList.add('answered');
        stateLabel.textContent = 'ANSWERED';
        sendAnswersButton.hidden = true;
        for (const questionState of questionStates) {
            questionState.optionButtons.forEach(optionButton => { optionButton.disabled = true; });
            questionState.freeTextInput.disabled = true;
        }
    }

    cardElement.markAnswered = markAnswered;
    if (isAnswered) cardElement.classList.add('answered');
    return cardElement;
}

function toggleOption(questionState, optionText, optionButton) {
    const wasSelected = questionState.selectedOptions.has(optionText);
    if (!questionState.question.multiSelect) {
        clearSelection(questionState);
        questionState.freeTextInput.value = '';
    }
    if (wasSelected) {
        questionState.selectedOptions.delete(optionText);
        optionButton.setAttribute('aria-pressed', 'false');
    } else {
        questionState.selectedOptions.add(optionText);
        optionButton.setAttribute('aria-pressed', 'true');
    }
}

function clearSelection(questionState) {
    questionState.selectedOptions.clear();
    questionState.optionButtons.forEach(optionButton => optionButton.setAttribute('aria-pressed', 'false'));
}

/**
 * Typing a normal message instead of using the card also answers it, so open cards close when any message is sent.
 */
function closeOpenQuestionCards() {
    document.querySelectorAll('.question-card:not(.answered)').forEach(cardElement => cardElement.markAnswered());
}

function addJournalChip(closureMessage, entryText) {
    const chipElement = createElementBefore(closureMessage.workedDetails, 'span', 'memory-chip journal-chip');
    addElement(chipElement, 'span', 'dot', '● ');
    chipElement.appendChild(document.createTextNode('FROM HER JOURNAL · ' + (entryText.length > 90 ? entryText.slice(0, 90) + '…' : entryText)));
    chipElement.title = entryText;
}
