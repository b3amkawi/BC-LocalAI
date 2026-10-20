/*
 * Chat UI wiring (FE-001, streaming since FE-002). Calls only POST /api/chat/stream on this site.
 * Every piece of text from the user, the model or the server is written with textContent / text nodes,
 * never as markup, so HTML or script in an answer is shown as plain text.
 * The conversation lives only in memory: nothing is stored in the browser.
 * Stream parsing and the answer states are in chat-stream.js (unit-tested); this file only touches the page.
 */
(function () {
    'use strict';

    var core = window.ChatCore;
    var stream = window.ChatStream;
    var form = document.getElementById('composer');
    var input = document.getElementById('message');
    var sendButton = document.getElementById('send');
    var stopButton = document.getElementById('stop');
    var list = document.getElementById('messages');
    var scroller = document.getElementById('conversation');
    var hint = document.getElementById('hint');
    var modelChip = document.getElementById('model-chip');
    var emptyState = document.getElementById('empty-state');

    if (!core || !stream || !form || !input || !sendButton || !stopButton || !list || !scroller || !hint || !modelChip) {
        return;
    }

    var STREAM_URL = '/api/chat/stream';
    // The server renders its own deadline plus a margin into the form, so its AI_TIMEOUT arrives first;
    // this only guards a request that never ends.
    var REQUEST_TIMEOUT_MS = core.requestTimeoutMs(form.getAttribute('data-timeout-seconds'));
    var MAX_INPUT_HEIGHT_PX = 200;
    // "At the bottom" tolerance: if the reader scrolled further up than this, new text does not move the page.
    var FOLLOW_THRESHOLD_PX = 48;

    var active = null; // the answer being generated: { controller, timedOut }

    function isNearBottom() {
        return scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight <= FOLLOW_THRESHOLD_PX;
    }

    function scrollToEnd() {
        scroller.scrollTop = scroller.scrollHeight;
    }

    function resizeInput() {
        // An empty field uses the height from the stylesheet (one line); measuring it can include the placeholder.
        if (input.value === '') {
            input.style.height = '';
            return;
        }

        // The field is border-box: add the border so the text fits without an inner scrollbar.
        input.style.height = 'auto';
        var borders = input.offsetHeight - input.clientHeight;
        input.style.height = Math.min(input.scrollHeight + borders, MAX_INPUT_HEIGHT_PX) + 'px';
    }

    function showHint(text) {
        hint.textContent = text;
    }

    function removeEmptyState() {
        if (emptyState && emptyState.parentNode) {
            emptyState.parentNode.removeChild(emptyState);
        }
    }

    function addMessage(kind, text, label) {
        removeEmptyState();

        var item = document.createElement('li');
        item.className = 'message message--' + kind;

        var meta = document.createElement('div');
        meta.className = 'message__meta';
        meta.textContent = label;

        var body = document.createElement('div');
        body.className = 'message__text';
        body.textContent = text;

        item.appendChild(meta);
        item.appendChild(body);
        list.appendChild(item);
        scrollToEnd();
        return item;
    }

    /**
     * One AI bubble per answer. Deltas are appended to a single text node, so the bubble is never rebuilt and
     * text that is already on screen never changes. The status line shows connecting / typing / stopped / incomplete.
     */
    function createAnswerView() {
        removeEmptyState();

        var item = document.createElement('li');
        item.className = 'message message--ai message--streaming';
        item.setAttribute('aria-busy', 'true');

        var meta = document.createElement('div');
        meta.className = 'message__meta';
        meta.textContent = 'AI';

        var body = document.createElement('div');
        body.className = 'message__text';
        var textNode = document.createTextNode('');
        body.appendChild(textNode);

        var status = document.createElement('div');
        status.className = 'message__status';
        status.setAttribute('role', 'status');

        var statusText = document.createElement('span');
        var dots = document.createElement('span');
        dots.className = 'dots';
        dots.setAttribute('aria-hidden', 'true');
        for (var i = 0; i < 3; i += 1) {
            dots.appendChild(document.createElement('i'));
        }

        status.appendChild(statusText);
        status.appendChild(dots);
        item.appendChild(meta);
        item.appendChild(body);
        item.appendChild(status);
        list.appendChild(item);
        scrollToEnd();

        var shown = 0;

        return {
            render: function (snapshot) {
                var follow = isNearBottom();
                if (snapshot.text.length > shown) {
                    textNode.appendData(snapshot.text.slice(shown));
                    shown = snapshot.text.length;
                }
                if (snapshot.model) {
                    meta.textContent = 'AI · ' + snapshot.model;
                }

                var state = snapshot.state;
                item.setAttribute('data-state', state);
                if (state === stream.STATES.CONNECTING || state === stream.STATES.STREAMING) {
                    statusText.textContent = stream.STATUS_TEXT[state];
                } else {
                    item.removeAttribute('aria-busy');
                    item.classList.remove('message--streaming');
                    if (dots.parentNode) {
                        dots.parentNode.removeChild(dots);
                    }
                    if (state === stream.STATES.COMPLETED) {
                        if (status.parentNode) {
                            status.parentNode.removeChild(status);
                        }
                    } else {
                        item.classList.add('message--incomplete');
                        statusText.textContent = state === stream.STATES.CANCELLED
                            ? stream.STATUS_TEXT.cancelled
                            : stream.STATUS_TEXT.incomplete;
                    }
                }
                if (follow) {
                    scrollToEnd();
                }
            },
            hasText: function () {
                return shown > 0;
            },
            remove: function () {
                if (item.parentNode) {
                    item.parentNode.removeChild(item);
                }
            }
        };
    }

    function setState(state) {
        var busy = state === stream.STATES.CONNECTING || state === stream.STATES.STREAMING;
        form.setAttribute('data-state', state);
        form.setAttribute('aria-busy', busy ? 'true' : 'false');
        sendButton.disabled = busy;
        sendButton.hidden = busy;
        stopButton.hidden = !busy;
        stopButton.disabled = !busy;
    }

    function stopGenerating() {
        if (active) {
            active.controller.abort();
        }
    }

    function giveQuestionBack(text) {
        // So it can be sent again, unless a new one is already being typed.
        if (input.value === '') {
            input.value = text;
            resizeInput();
        }
    }

    async function onSubmit(event) {
        event.preventDefault();

        // Guards the Enter key and double clicks as well as the hidden button: one answer at a time.
        if (active) {
            return;
        }

        var text = core.prepareMessage(input.value);
        if (!text) {
            input.value = '';
            resizeInput();
            showHint(core.messageFor('INVALID_MESSAGE'));
            input.focus();
            return;
        }

        showHint('');
        var request = { controller: new AbortController(), timedOut: false, timer: null };
        active = request;
        setState(stream.STATES.CONNECTING);
        try {
            await askAndShow(request, text);
        } finally {
            // The page must be usable again even if something above throws: Send comes back, Stop goes away.
            clearTimeout(request.timer);
            active = null;
            setState(stream.STATES.IDLE);
            input.focus();
        }
    }

    async function askAndShow(request, text) {
        addMessage('user', text, 'คุณ');
        input.value = '';
        resizeInput();
        var view = createAnswerView();
        view.render({ state: stream.STATES.CONNECTING, text: '', model: null });

        request.timer = setTimeout(function () {
            request.timedOut = true;
            request.controller.abort();
        }, REQUEST_TIMEOUT_MS);

        function update(snapshot) {
            // Only the answer this request created is ever updated; nothing from another request can reach it.
            if (active !== request) {
                return;
            }
            view.render(snapshot);
            if (snapshot.state === stream.STATES.STREAMING) {
                setState(stream.STATES.STREAMING);
                modelChip.textContent = 'โมเดล: ' + snapshot.model;
            }
        }

        var final;
        try {
            var response = await fetch(STREAM_URL, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' },
                body: JSON.stringify({ message: text }),
                signal: request.controller.signal
            });
            final = await stream.readAnswer(response, update, { isTimeout: function () { return request.timedOut; } });
        } catch (error) {
            // The request itself failed: no response at all (network) or aborted before the headers arrived.
            var session = stream.createStreamSession();
            final = error && error.name === 'AbortError'
                ? (request.timedOut ? session.fail('AI_TIMEOUT') : session.cancel())
                : session.fail('NETWORK_ERROR');
            update(final);
        }

        if (final.state === stream.STATES.ERROR) {
            if (!view.hasText()) {
                view.remove();
            }
            addMessage('error', final.message, 'ข้อผิดพลาด');
            giveQuestionBack(text);
        } else if (final.state === stream.STATES.CANCELLED && !view.hasText()) {
            view.remove();
            addMessage('note', stream.STATUS_TEXT.cancelled, 'ระบบ');
        }
    }

    form.addEventListener('submit', onSubmit);
    stopButton.addEventListener('click', stopGenerating);

    input.addEventListener('keydown', function (event) {
        if (core.isSendKey(event)) {
            event.preventDefault();
            form.requestSubmit();
        }
    });

    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape' && active) {
            event.preventDefault();
            stopGenerating();
        }
    });

    input.addEventListener('input', function () {
        resizeInput();
        if (hint.textContent !== '') {
            showHint('');
        }
    });

    // Leaving the page stops the answer, so the server stops generating too.
    window.addEventListener('pagehide', stopGenerating);

    // A different width wraps the text differently.
    window.addEventListener('resize', resizeInput);

    setState(stream.STATES.IDLE);
    resizeInput();
    input.focus();
}());
