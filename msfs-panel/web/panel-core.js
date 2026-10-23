/*
 * StormATC panel: the ATC dialogue, the phraseology guide and the charts.
 *
 * Shared by the MSFS toolbar window and by the standalone page (a browser, or a VR overlay).
 * It asks StormATC's local server for the state once a second and draws it. Written in plain
 * ES5 with no libraries because it runs inside the simulator's embedded browser.
 */
var StormAtcPanel = (function () {
    'use strict';

    var DEFAULT_PORT = 48765;
    var POLL_MS = 1000;
    var MIN_SCALE = 0.8;
    var MAX_SCALE = 2.4;
    var ZOOMS = [1, 1.5, 2, 3, 4];
    var CHARTS_REFRESH_MS = 5000;
    var CHARTS_FLAT_MAX = 15;
    var RECENT_CHARTS_KEY = 'stormatc.recentCharts';
    var RECENT_CHARTS_MAX = 5;

    function storageGet(key) {
        try { return window.localStorage.getItem(key); } catch (e) { return null; }
    }

    function storageSet(key, value) {
        try { window.localStorage.setItem(key, String(value)); } catch (e) { /* not available */ }
    }

    function port() {
        var fromQuery = /[?&]port=(\d+)/.exec(window.location.search || '');
        var stored = storageGet('stormatc.port');
        var value = parseInt(fromQuery ? fromQuery[1] : stored, 10);
        return value > 0 ? value : DEFAULT_PORT;
    }

    function baseUrl() {
        // Served by StormATC itself: the same origin. Inside MSFS the page comes from the
        // simulator's own files, so the address is built from the port.
        if (window.location && window.location.protocol === 'http:' &&
            /^(127\.0\.0\.1|localhost)$/.test(window.location.hostname)) {
            return window.location.protocol + '//' + window.location.host;
        }
        return 'http://127.0.0.1:' + port();
    }

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) { node.className = className; }
        if (text !== undefined && text !== null) { node.textContent = text; }
        return node;
    }

    function clear(node) {
        while (node.firstChild) { node.removeChild(node.firstChild); }
    }

    // A request that changes something in StormATC. The marker header keeps a web page of another
    // site from sending the same request to the pilot's PC.
    function postJson(path, body, done) {
        var xhr = new XMLHttpRequest();
        xhr.open('POST', baseUrl() + path, true);
        xhr.setRequestHeader('X-StormATC', '1');
        xhr.timeout = 5000;
        xhr.onload = function () { if (done) { done(xhr.status === 200); } };
        xhr.onerror = function () { if (done) { done(false); } };
        xhr.ontimeout = function () { if (done) { done(false); } };
        xhr.send(JSON.stringify(body));
    }

    function mount(root, options) {
        options = options || {};
        var scale = parseFloat(storageGet('stormatc.scale')) || 1.2;
        var savedTab = storageGet('stormatc.tab');
        var tab = savedTab === 'phraseology' || savedTab === 'charts' || savedTab === 'military' ? savedTab : 'dialogue';
        var timer = null;
        var stopped = false;
        var lastJson = '';
        var lastState = null;
        var offline = false;
        var stickToBottom = true;
        var chosenStage = storageGet('stormatc.stage') || 'auto';
        var rejectedVoice = '';
        var rejecting = false;
        var messageTimer = null;

        clear(root);
        root.className = 'sa-root';

        var header = el('div', 'sa-header');
        var status = el('div', 'sa-status');
        var tools = el('div', 'sa-tools');
        var smaller = el('button', 'sa-btn sa-btn-small', 'A−');
        var bigger = el('button', 'sa-btn sa-btn-small', 'A+');
        smaller.setAttribute('type', 'button');
        bigger.setAttribute('type', 'button');
        smaller.setAttribute('aria-label', 'Smaller text');
        bigger.setAttribute('aria-label', 'Larger text');
        tools.appendChild(smaller);
        tools.appendChild(bigger);
        header.appendChild(status);
        header.appendChild(tools);

        var tabs = el('div', 'sa-tabs');
        var tabDialogue = el('button', 'sa-tab');
        var tabPhraseology = el('button', 'sa-tab');
        var tabCharts = el('button', 'sa-tab');
        var tabMilitary = el('button', 'sa-tab');
        tabMilitary.style.display = 'none';
        tabMilitary.setAttribute('type', 'button');
        tabDialogue.setAttribute('type', 'button');
        tabPhraseology.setAttribute('type', 'button');
        tabCharts.setAttribute('type', 'button');
        tabs.appendChild(tabDialogue);
        tabs.appendChild(tabPhraseology);
        tabs.appendChild(tabCharts);
        tabs.appendChild(tabMilitary);

        var body = el('div', 'sa-body');
        var dialoguePane = el('div', 'sa-pane sa-dialogue');
        var phraseologyPane = el('div', 'sa-pane sa-phraseology');
        var chartsPane = el('div', 'sa-pane sa-charts');
        var militaryPane = el('div', 'sa-pane sa-military');
        body.appendChild(dialoguePane);
        body.appendChild(phraseologyPane);
        body.appendChild(chartsPane);
        body.appendChild(militaryPane);

        // Under the conversation: which voice spoke last, and the button to set it aside.
        var voiceBar = el('div', 'sa-voicebar');
        var voiceText = el('div', 'sa-voicetext');
        var rejectButton = el('button', 'sa-reject');
        var voiceMessage = el('div', 'sa-voicemsg');
        rejectButton.setAttribute('type', 'button');
        voiceBar.appendChild(voiceText);
        voiceBar.appendChild(rejectButton);
        voiceBar.appendChild(voiceMessage);

        // What the panel is doing about the flight plan, so the pilot can see in the cockpit
        // whether the plan reached StormATC.
        var bridgeLine = el('div', 'sa-bridge');

        root.appendChild(header);
        root.appendChild(tabs);
        root.appendChild(body);
        root.appendChild(voiceBar);
        root.appendChild(bridgeLine);

        // Inside the simulator's window the root gets no height from its parents, and the content
        // area only grows with its content. The window itself has a definite size: the root takes
        // what is left under the window's header, and follows the window when it is resized.
        function fitToParent() {
            var frame = root.parentNode;
            while (frame && !(frame.tagName && frame.tagName.toLowerCase() === 'ingame-ui')) {
                frame = frame.parentNode;
            }
            if (!frame || frame.clientHeight < 100) { return; }

            var header = frame.querySelector('ingame-ui-header');
            var headerHeight = header ? header.offsetHeight : 0;
            var height = frame.clientHeight - headerHeight - 12;
            if (height > 80) { root.style.height = height + 'px'; }
        }

        function applyScale() {
            root.style.fontSize = Math.round(scale * 100) / 100 + 'em';
        }

        function setScale(value) {
            scale = Math.max(MIN_SCALE, Math.min(MAX_SCALE, value));
            storageSet('stormatc.scale', scale);
            applyScale();
        }

        // The bar belongs to the conversation tab and appears once StormATC has answered.
        function updateVoiceBar() {
            var ready = lastState && lastState.labels && lastState.labels.rejectVoice;
            voiceBar.style.display = (tab === 'dialogue' && ready) ? 'block' : 'none';
        }

        function showTab(name) {
            tab = name;
            storageSet('stormatc.tab', name);
            dialoguePane.style.display = name === 'dialogue' ? 'block' : 'none';
            phraseologyPane.style.display = name === 'phraseology' ? 'block' : 'none';
            chartsPane.style.display = name === 'charts' ? 'flex' : 'none';
            militaryPane.style.display = name === 'military' ? 'block' : 'none';
            tabMilitary.className = 'sa-tab' + (name === 'military' ? ' sa-active' : '');
            tabDialogue.className = 'sa-tab' + (name === 'dialogue' ? ' sa-active' : '');
            tabPhraseology.className = 'sa-tab' + (name === 'phraseology' ? ' sa-active' : '');
            tabCharts.className = 'sa-tab' + (name === 'charts' ? ' sa-active' : '');
            updateVoiceBar();
            if (name === 'dialogue') { scrollDialogue(true); }
            if (name === 'charts') { loadCharts(); }
        }

        // ---- The charts: the PDFs in StormATC's charts folder, drawn by StormATC ------------------
        var chartLabels = null;
        var chartData = null;
        var chartError = false;
        var openedChart = null;
        var chartPage = 1;
        var chartPages = 0;
        var chartZoom = 1;
        var chartsTimer = null;
        var chartLetter = null;
        var renderedChartList = null;
        var chartView = null;
        // The destination whose chart was opened for the pilot, or that the pilot went back from: the
        // chart of the aerodrome the flight is heading to opens by itself once, not again and again.
        var chartHandledFor = '';

        function chartTexts() {
            return (lastState && lastState.labels && lastState.labels.charts) || chartLabels || null;
        }

        function getJson(path, done) {
            var xhr = new XMLHttpRequest();
            xhr.open('GET', baseUrl() + path, true);
            xhr.timeout = 15000;
            xhr.onload = function () {
                var data = null;
                if (xhr.status === 200) {
                    try { data = JSON.parse(xhr.responseText); } catch (e) { data = null; }
                }
                done(data);
            };
            xhr.onerror = function () { done(null); };
            xhr.ontimeout = function () { done(null); };
            xhr.send();
        }

        function loadCharts() {
            if (tab !== 'charts' || openedChart) { return; }
            getJson('/api/charts', function (data) {
                chartError = !data;
                chartData = data;
                renderChartList();
            });
        }

        // The charts of the flight's aerodromes come first, tagged.
        function chartRank(chart) {
            var s = lastState;
            if (s && chart.icao && s.departure && chart.icao === s.departure) { return 0; }
            if (s && chart.icao && s.destination && chart.icao === s.destination) { return 1; }
            return 2;
        }

        // What the list shows, as text. The list is refreshed every few seconds and with every state
        // update; rebuilding it when nothing changed would send the pilot back to the top.
        function chartListSignature(t) {
            var charts = (chartData && chartData.charts) || [];
            return JSON.stringify([
                t && t.tab, chartError, chartData && chartData.directory, chartData && chartData.source, chartLetter,
                lastState && lastState.departure, lastState && lastState.destination, recentIds(),
                charts.map(function (c) { return c.id + '|' + (c.aerodromeName || ''); })
            ]);
        }

        function renderChartList() {
            if (openedChart) { return; }
            var t = chartTexts();
            var signature = chartListSignature(t);
            if (signature === renderedChartList && chartsPane.firstChild) { return; }
            renderedChartList = signature;
            clear(chartsPane);
            var list = el('div', 'sa-chartlist');
            chartsPane.appendChild(list);
            if (chartData && chartData.source) { chartsPane.appendChild(el('p', 'sa-chartsource', chartData.source)); }
            if (!t) { return; }

            if (chartError || !chartData) {
                list.appendChild(el('p', 'sa-empty', t.unavailable));
                return;
            }
            var charts = (chartData.charts || []).slice();
            if (!charts.length) {
                list.appendChild(el('p', 'sa-empty', t.empty.replace('{0}', chartData.directory || '')));
                return;
            }
            if (openDestinationChart(charts)) { return; }
            charts.sort(function (a, b) {
                var d = chartRank(a) - chartRank(b);
                if (d !== 0) { return d; }
                var an = chartTitle(a), bn = chartTitle(b);
                return an < bn ? -1 : (an > bn ? 1 : 0);
            });

            function addChartButton(chart) {
                var button = el('button', 'sa-chartbtn');
                button.setAttribute('type', 'button');
                button.appendChild(el('span', 'sa-chartname', chartTitle(chart)));
                var rank = chartRank(chart);
                if (rank < 2) {
                    button.appendChild(el('span', 'sa-charttag', rank === 0 ? t.departure : t.destination));
                }
                button.onclick = function () { openChart(chart); };
                list.appendChild(button);
            }

            // Few charts: one flat list. Many: an index by letter, which is easy to use with a
            // controller or in VR, where there is no keyboard to search with.
            if (charts.length <= CHARTS_FLAT_MAX) {
                charts.forEach(addChartButton);
                return;
            }

            if (chartLetter !== null) {
                var up = el('button', 'sa-btn sa-chartnav', t.back);
                up.setAttribute('type', 'button');
                up.onclick = function () { chartLetter = null; renderChartList(); };
                list.appendChild(up);
                list.appendChild(el('p', 'sa-charthead', chartLetter));
                charts.filter(function (c) { return chartLetterOf(c) === chartLetter; }).forEach(addChartButton);
                return;
            }

            charts.filter(function (c) { return chartRank(c) < 2; }).forEach(addChartButton);

            var recent = recentCharts(charts);
            if (recent.length) {
                list.appendChild(el('p', 'sa-charthead', t.recent));
                recent.forEach(addChartButton);
            }

            var counts = {};
            charts.forEach(function (c) { var l = chartLetterOf(c); counts[l] = (counts[l] || 0) + 1; });
            var grid = el('div', 'sa-letters');
            Object.keys(counts).sort().forEach(function (letter) {
                var tile = el('button', 'sa-letter');
                tile.setAttribute('type', 'button');
                tile.appendChild(el('span', 'sa-letterchar', letter));
                tile.appendChild(el('span', 'sa-lettercount', String(counts[letter])));
                tile.onclick = function () { chartLetter = letter; renderChartList(); };
                grid.appendChild(tile);
            });
            list.appendChild(grid);
        }

        // The title of a chart in the list: its file name and the aerodrome's name.
        function chartTitle(chart) {
            return chart.aerodromeName ? chart.name + ' – ' + chart.aerodromeName : chart.name;
        }

        // The letter a chart is filed under: the first letter of the aerodrome's name, without accent.
        function chartLetterOf(chart) {
            var text = (chart.aerodromeName || chart.name || '').toString();
            var first = text.normalize ? text.normalize('NFD').charAt(0).toUpperCase() : text.charAt(0).toUpperCase();
            return first >= 'A' && first <= 'Z' ? first : '#';
        }

        function recentIds() {
            try {
                var saved = JSON.parse(window.localStorage.getItem(RECENT_CHARTS_KEY) || '[]');
                return Array.isArray(saved) ? saved : [];
            } catch (e) { return []; }
        }

        function rememberChart(chart) {
            try {
                var ids = recentIds().filter(function (id) { return id !== chart.id; });
                ids.unshift(chart.id);
                window.localStorage.setItem(RECENT_CHARTS_KEY, JSON.stringify(ids.slice(0, RECENT_CHARTS_MAX)));
            } catch (e) { /* the list of recent charts is a convenience: ignore a store that does not work */ }
        }

        function recentCharts(charts) {
            var found = [];
            recentIds().forEach(function (id) {
                for (var i = 0; i < charts.length; i++) {
                    if (charts[i].id === id && chartRank(charts[i]) >= 2) { found.push(charts[i]); break; }
                }
            });
            return found;
        }

        // With a flight plan, the chart of the destination aerodrome is what the pilot wants first.
        function openDestinationChart(charts) {
            var destination = lastState && lastState.destination;
            if (!destination || chartHandledFor === destination) { return false; }
            for (var i = 0; i < charts.length; i++) {
                if (charts[i].icao === destination) {
                    chartHandledFor = destination;
                    openChart(charts[i]);
                    return true;
                }
            }
            return false;
        }

        function openChart(chart) {
            rememberChart(chart);
            openedChart = chart;
            chartPage = 1;
            chartPages = 0;
            chartZoom = 1;
            buildViewer();
            getJson('/api/charts/' + encodeURIComponent(chart.id), function (info) {
                if (openedChart !== chart) { return; }
                if (!info) { chartView.message.textContent = chartTexts().failed; return; }
                chartPages = info.pages;
                showChartPage();
            });
        }

        function closeChart() {
            // Back to the list on purpose: do not open the destination's chart again.
            if (lastState && lastState.destination) { chartHandledFor = lastState.destination; }
            openedChart = null;
            chartView = null;
            loadCharts();
            renderChartList();
        }

        function chartButton(text, onclick) {
            var button = el('button', 'sa-btn sa-chartnav', text);
            button.setAttribute('type', 'button');
            button.onclick = onclick;
            return button;
        }

        function buildViewer() {
            renderedChartList = null;
            clear(chartsPane);
            var t = chartTexts();
            var bar = el('div', 'sa-chartbar');
            var back = chartButton(t.back, closeChart);
            var previous = chartButton(t.previous, function () { turnChart(-1); });
            var label = el('span', 'sa-chartpage');
            var next = chartButton(t.next, function () { turnChart(1); });
            var zoomOut = chartButton(t.zoomOut, function () { zoomChart(-1); });
            var fit = chartButton(t.fit, function () { chartZoom = 1; showChartPage(); });
            var zoomIn = chartButton(t.zoomIn, function () { zoomChart(1); });
            var items = [back, previous, label, next, zoomOut, fit, zoomIn];
            for (var i = 0; i < items.length; i++) { bar.appendChild(items[i]); }

            var scroller = el('div', 'sa-chartscroll');
            var message = el('div', 'sa-chartmsg', t.loading);
            var image = el('img', 'sa-chartimg');
            image.setAttribute('alt', openedChart.name);
            image.onload = function () { message.textContent = ''; };
            image.onerror = function () { message.textContent = chartTexts().failed; };
            scroller.appendChild(message);
            scroller.appendChild(image);

            // Dragging with a mouse or a VR pointer moves the page; touch screens scroll on their own.
            var drag = null;
            scroller.onmousedown = function (e) {
                drag = { x: e.clientX, y: e.clientY, left: scroller.scrollLeft, top: scroller.scrollTop };
                if (e.preventDefault) { e.preventDefault(); }
            };
            scroller.onmousemove = function (e) {
                if (!drag) { return; }
                scroller.scrollLeft = drag.left - (e.clientX - drag.x);
                scroller.scrollTop = drag.top - (e.clientY - drag.y);
            };
            scroller.onmouseup = function () { drag = null; };
            scroller.onmouseleave = function () { drag = null; };

            chartsPane.appendChild(bar);
            chartsPane.appendChild(scroller);
            chartView = { label: label, previous: previous, next: next, zoomOut: zoomOut, zoomIn: zoomIn,
                          scroller: scroller, image: image, message: message };
            updateChartBar();
        }

        function updateChartBar() {
            if (!chartView) { return; }
            var t = chartTexts();
            chartView.label.textContent = chartPages
                ? t.page + ' ' + chartPage + ' ' + t.of + ' ' + chartPages
                : '';
            chartView.previous.disabled = chartPage <= 1;
            chartView.next.disabled = !chartPages || chartPage >= chartPages;
            chartView.zoomOut.disabled = chartZoom <= ZOOMS[0];
            chartView.zoomIn.disabled = chartZoom >= ZOOMS[ZOOMS.length - 1];
        }

        function turnChart(step) {
            var target = chartPage + step;
            if (target < 1 || target > chartPages) { return; }
            chartPage = target;
            showChartPage();
        }

        function zoomChart(step) {
            var at = 0;
            for (var i = 0; i < ZOOMS.length; i++) { if (ZOOMS[i] === chartZoom) { at = i; } }
            var target = Math.max(0, Math.min(ZOOMS.length - 1, at + step));
            chartZoom = ZOOMS[target];
            showChartPage();
        }

        function showChartPage() {
            if (!chartView || !openedChart) { return; }
            updateChartBar();
            var view = chartView;
            var cssWidth = Math.max(300, view.scroller.clientWidth - 4) * chartZoom;
            var pixels = Math.min(4096, Math.round(cssWidth * (window.devicePixelRatio || 1)));
            view.message.textContent = chartTexts().loading;
            view.image.style.width = Math.round(cssWidth) + 'px';
            view.image.src = baseUrl() + '/api/charts/' + encodeURIComponent(openedChart.id) + '/' + chartPage +
                '.png?w=' + pixels;
            view.scroller.scrollTop = 0;
            view.scroller.scrollLeft = 0;
        }

        chartsTimer = window.setInterval(function () {
            if (tab === 'charts' && !openedChart) { loadCharts(); }
        }, CHARTS_REFRESH_MS);

        function scrollDialogue(force) {
            if (force || stickToBottom) {
                dialoguePane.scrollTop = dialoguePane.scrollHeight;
            }
        }

        function renderStatus(state) {
            clear(status);
            if (offline || !state) {
                status.appendChild(el('span', 'sa-dot sa-dot-off'));
                status.appendChild(el('span', 'sa-status-text',
                    'StormATC ne répond pas — lancez l’application / StormATC is not responding'));
                return;
            }
            status.appendChild(el('span', 'sa-dot sa-dot-on'));
            var parts = [];
            if (state.callsign) { parts.push(state.callsign); }
            if (state.controller) {
                parts.push(state.controller + (state.frequency ? ' ' + state.frequency : ''));
            }
            parts.push(state.rules);
            status.appendChild(el('span', 'sa-status-text', parts.join('  ·  ')));
        }

        function renderDialogue(state) {
            clear(dialoguePane);
            if (!state.turns.length) {
                dialoguePane.appendChild(el('p', 'sa-empty',
                    state.callsign ? state.labels.noConversation : state.labels.noFlight));
                return;
            }
            for (var i = 0; i < state.turns.length; i++) {
                var turn = state.turns[i];
                var line = el('div', 'sa-turn sa-' + turn.speaker);
                var meta = el('div', 'sa-meta');
                meta.appendChild(el('span', 'sa-who', turn.who));
                meta.appendChild(el('span', 'sa-time', turn.time));
                line.appendChild(meta);
                line.appendChild(el('div', 'sa-text', turn.text));
                dialoguePane.appendChild(line);
            }
        }

        function renderExample(example, labels) {
            var card = el('div', 'sa-card');
            card.appendChild(el('div', 'sa-situation', example.situation));
            if (example.pilot) {
                var pilot = el('div', 'sa-say sa-pilot');
                pilot.appendChild(el('span', 'sa-label', labels.say));
                pilot.appendChild(el('span', 'sa-quote', example.pilot));
                card.appendChild(pilot);
            }
            if (example.controller) {
                var atc = el('div', 'sa-say sa-atc');
                atc.appendChild(el('span', 'sa-label', labels.answer));
                atc.appendChild(el('span', 'sa-quote', example.controller));
                card.appendChild(atc);
            }
            return card;
        }

        function findStage(p, key) {
            var stages = p.stages || [];
            for (var i = 0; i < stages.length; i++) {
                if (stages[i].key === key) { return stages[i]; }
            }
            return null;
        }

        function chooseStage(key) {
            chosenStage = key;
            storageSet('stormatc.stage', key);
            if (lastState) {
                renderPhraseology(lastState);
                phraseologyPane.scrollTop = 0;
            }
        }

        function renderPhraseology(state) {
            clear(phraseologyPane);
            var p = state.phraseology;

            // The pilot may read any phase; "Auto" follows the flight.
            var shown = chosenStage === 'auto' ? null : findStage(p, chosenStage);
            if (!shown) { chosenStage = 'auto'; }
            var examples = shown ? shown.examples : p.examples;
            var title = shown ? shown.title : p.title;

            var chips = el('div', 'sa-chips');
            var auto = el('button', 'sa-chip' + (chosenStage === 'auto' ? ' sa-active' : ''), state.labels.auto);
            auto.setAttribute('type', 'button');
            auto.onclick = function () { chooseStage('auto'); };
            chips.appendChild(auto);
            var allStages = p.stages || [];
            for (var s = 0; s < allStages.length; s++) {
                (function (stage) {
                    var chip = el('button', 'sa-chip' + (chosenStage === stage.key ? ' sa-active' : ''), stage.title);
                    if (stage.key === p.stage) { chip.appendChild(el('span', 'sa-now')); }
                    chip.setAttribute('type', 'button');
                    chip.onclick = function () { chooseStage(stage.key); };
                    chips.appendChild(chip);
                }(allStages[s]));
            }
            phraseologyPane.appendChild(chips);

            var heading = el('div', 'sa-stage');
            heading.appendChild(el('span', 'sa-stage-label',
                shown ? state.labels.chooseStage : state.labels.currentStage));
            heading.appendChild(el('span', 'sa-stage-title', title));
            phraseologyPane.appendChild(heading);
            for (var i = 0; i < examples.length; i++) {
                phraseologyPane.appendChild(renderExample(examples[i], state.labels));
            }
            phraseologyPane.appendChild(el('h3', 'sa-always', state.labels.always));
            for (var j = 0; j < p.always.length; j++) {
                phraseologyPane.appendChild(renderExample(p.always[j], state.labels));
            }
        }

        // The military page: the regime, the base's VHF frequencies, the radar approach, and the calls by theme.
        function renderMilitary(state) {
            var m = state.military;
            if (!m) {
                tabMilitary.style.display = 'none';
                if (tab === 'military') { showTab('dialogue'); }
                return;
            }
            tabMilitary.style.display = '';
            tabMilitary.textContent = m.labels.tab;
            if (tab !== 'military') { return; }

            var readingAt = militaryPane.scrollTop;
            clear(militaryPane);
            var head = el('div', 'sa-stage');
            head.appendChild(el('span', 'sa-stage-label', m.labels.regime));
            head.appendChild(el('span', 'sa-stage-title', m.regime + (m.aerodrome ? ' \u2013 ' + m.aerodrome : '')));
            militaryPane.appendChild(head);

            militaryPane.appendChild(el('h3', 'sa-always', m.labels.radar));
            militaryPane.appendChild(el('div', 'sa-milnote', m.radar || m.labels.noRadar));

            militaryPane.appendChild(el('h3', 'sa-always', m.labels.frequencies));
            if (!m.frequencies.length) {
                militaryPane.appendChild(el('div', 'sa-milnote', m.labels.noFrequencies));
            }
            for (var f = 0; f < m.frequencies.length; f++) {
                var row = el('div', 'sa-freqrow');
                row.appendChild(el('span', 'sa-freqservice', m.frequencies[f].service));
                row.appendChild(el('span', 'sa-freqvalue', m.frequencies[f].frequency));
                if (m.frequencies[f].remark) { row.appendChild(el('span', 'sa-freqremark', m.frequencies[f].remark)); }
                militaryPane.appendChild(row);
            }
            militaryPane.appendChild(el('div', 'sa-milnote', m.labels.vhfOnly));

            for (var s = 0; s < m.sections.length; s++) {
                militaryPane.appendChild(el('h3', 'sa-always', m.sections[s].title));
                for (var e = 0; e < m.sections[s].examples.length; e++) {
                    militaryPane.appendChild(renderExample(m.sections[s].examples[e], state.labels));
                }
            }
            militaryPane.scrollTop = readingAt;
        }

        function showVoiceMessage(text) {
            voiceMessage.textContent = text;
            if (messageTimer) { window.clearTimeout(messageTimer); }
            messageTimer = window.setTimeout(function () { voiceMessage.textContent = ''; }, 7000);
        }

        function renderVoice(state) {
            var labels = state.labels;
            updateVoiceBar();
            if (!labels.rejectVoice) { return; }

            var voice = state.voice;
            voiceText.textContent = voice ? labels.voice + ' : ' + voice.description : labels.noVoiceYet;
            rejectButton.textContent = labels.rejectVoice;

            // A voice that was just set aside waits for the next one before it can be judged again.
            var already = voice && voice.description === rejectedVoice;
            rejectButton.disabled = !voice || !voice.canReject || already || rejecting;
            if (voice && !voice.canReject && !voiceMessage.textContent) {
                voiceMessage.textContent = labels.voiceInSettings;
            }
        }

        function rejectVoice() {
            if (!lastState || !lastState.voice || rejecting) { return; }
            var labels = lastState.labels;
            var description = lastState.voice.description;
            rejecting = true;
            rejectButton.disabled = true;

            var xhr = new XMLHttpRequest();
            xhr.open('POST', baseUrl() + '/api/voice/reject', true);
            xhr.setRequestHeader('X-StormATC', '1');
            xhr.timeout = 5000;
            function done(text, accepted) {
                rejecting = false;
                if (accepted) { rejectedVoice = description; }
                showVoiceMessage(text);
                if (lastState) { renderVoice(lastState); }
            }
            xhr.onload = function () {
                var outcome = 'failed';
                try { outcome = JSON.parse(xhr.responseText).outcome; } catch (e) { /* keep failed */ }
                if (xhr.status !== 200) { outcome = 'failed'; }
                if (outcome === 'setAside') { done(labels.voiceSetAside, true); }
                else if (outcome === 'chosenInSettings') { done(labels.voiceInSettings, false); }
                else if (outcome === 'noVoiceYet') { done(labels.noVoiceYet, false); }
                else { done(labels.rejectFailed, false); }
            };
            xhr.onerror = function () { done(labels.rejectFailed, false); };
            xhr.ontimeout = function () { done(labels.rejectFailed, false); };
            xhr.send('');
        }

        function render(state) {
            lastState = state;
            renderStatus(state);
            if (!state) { return; }
            if (options.onState) { options.onState(state); }
            tabDialogue.textContent = state.labels.tabDialogue;
            tabPhraseology.textContent = state.labels.tabPhraseology;
            if (state.labels.charts) {
                chartLabels = state.labels.charts;
                tabCharts.textContent = state.labels.charts.tab;
                if (tab === 'charts' && !openedChart) { renderChartList(); }
            }
            var wasAtBottom = dialoguePane.scrollHeight - dialoguePane.scrollTop - dialoguePane.clientHeight < 40;
            stickToBottom = stickToBottom || wasAtBottom;
            renderDialogue(state);
            renderVoice(state);
            // A new message must not throw the pilot back to the top of the list being read.
            var readingAt = phraseologyPane.scrollTop;
            renderPhraseology(state);
            phraseologyPane.scrollTop = readingAt;
            renderMilitary(state);
            scrollDialogue(false);
        }

        function request() {
            if (stopped) { return; }
            var xhr = new XMLHttpRequest();
            xhr.open('GET', baseUrl() + '/api/state?t=' + new Date().getTime(), true);
            xhr.timeout = 3000;
            xhr.onload = function () {
                if (xhr.status === 200) {
                    offline = false;
                    if (xhr.responseText !== lastJson) {
                        lastJson = xhr.responseText;
                        try { render(JSON.parse(xhr.responseText)); } catch (e) { /* keep the last view */ }
                    }
                    // The simulator resets the window's title when it opens it: apply it again.
                    if (lastState && options.onState) { options.onState(lastState); }
                    fitToParent();
                } else {
                    markOffline();
                }
                next();
            };
            xhr.onerror = function () { markOffline(); next(); };
            xhr.ontimeout = function () { markOffline(); next(); };
            xhr.send();
        }

        function markOffline() {
            if (!offline) {
                offline = true;
                renderStatus(lastState);
            }
        }

        function next() {
            if (!stopped) { timer = window.setTimeout(request, POLL_MS); }
        }

        dialoguePane.onscroll = function () {
            stickToBottom = dialoguePane.scrollHeight - dialoguePane.scrollTop - dialoguePane.clientHeight < 40;
        };
        smaller.onclick = function () { setScale(scale - 0.15); };
        bigger.onclick = function () { setScale(scale + 0.15); };
        rejectButton.onclick = rejectVoice;
        tabDialogue.onclick = function () { showTab('dialogue'); };
        tabPhraseology.onclick = function () { showTab('phraseology'); };
        tabCharts.onclick = function () { showTab('charts'); };

        voiceBar.style.display = 'none';
        tabDialogue.textContent = 'Dialogue ATC';
        tabPhraseology.textContent = 'Phraséologie';
        tabCharts.textContent = 'Cartes';
        applyScale();
        fitToParent();
        var fitTimer = window.setInterval(fitToParent, 300);
        showTab(tab);
        renderStatus(null);
        request();

        return {
            setBridgeStatus: function (text) { bridgeLine.textContent = text || ''; },
            stop: function () {
                stopped = true;
                window.clearInterval(fitTimer);
                if (chartsTimer) { window.clearInterval(chartsTimer); }
                if (timer) { window.clearTimeout(timer); }
            }
        };
    }

    return { mount: mount, postJson: postJson };
}());
