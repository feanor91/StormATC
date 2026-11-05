/*
 * StormATC panel: the ATC dialogue, the phraseology guide, the charts and the map.
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
        var tab = savedTab === 'phraseology' || savedTab === 'charts' || savedTab === 'map' || savedTab === 'military' ? savedTab : 'dialogue';
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
        var tabMap = el('button', 'sa-tab');
        var tabMilitary = el('button', 'sa-tab');
        tabMilitary.style.display = 'none';
        tabMilitary.setAttribute('type', 'button');
        tabDialogue.setAttribute('type', 'button');
        tabPhraseology.setAttribute('type', 'button');
        tabCharts.setAttribute('type', 'button');
        tabMap.setAttribute('type', 'button');
        tabs.appendChild(tabDialogue);
        tabs.appendChild(tabPhraseology);
        tabs.appendChild(tabMap);
        tabs.appendChild(tabCharts);
        tabs.appendChild(tabMilitary);

        var body = el('div', 'sa-body');
        var dialoguePane = el('div', 'sa-pane sa-dialogue');
        var phraseologyPane = el('div', 'sa-pane sa-phraseology');
        var chartsPane = el('div', 'sa-pane sa-charts');
        var mapPane = el('div', 'sa-pane sa-map');
        var militaryPane = el('div', 'sa-pane sa-military');
        body.appendChild(dialoguePane);
        body.appendChild(phraseologyPane);
        body.appendChild(chartsPane);
        body.appendChild(mapPane);
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

        // What to say next, under the conversation, when the option is on.
        var suggestBar = el('div', 'sa-suggest');
        suggestBar.style.display = 'none';

        root.appendChild(header);
        root.appendChild(tabs);
        root.appendChild(body);
        root.appendChild(suggestBar);
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

        function renderSuggestion(state) {
            var s = state && state.suggestion;
            clear(suggestBar);
            if (!s || tab !== 'dialogue') { suggestBar.style.display = 'none'; return; }
            suggestBar.style.display = 'block';
            suggestBar.appendChild(el('div', 'sa-suggest-head', s.heading + ' : ' + s.title));
            if (s.example) {
                suggestBar.appendChild(el('div', 'sa-suggest-example', '« ' + s.example + ' »'));
            } else if (s.detail) {
                suggestBar.appendChild(el('div', 'sa-suggest-detail', s.detail));
            }
        }

        function showTab(name) {
            tab = name;
            storageSet('stormatc.tab', name);
            dialoguePane.style.display = name === 'dialogue' ? 'block' : 'none';
            phraseologyPane.style.display = name === 'phraseology' ? 'block' : 'none';
            chartsPane.style.display = name === 'charts' ? 'flex' : 'none';
            mapPane.style.display = name === 'map' ? 'flex' : 'none';
            militaryPane.style.display = name === 'military' ? 'block' : 'none';
            tabMilitary.className = 'sa-tab' + (name === 'military' ? ' sa-active' : '');
            tabDialogue.className = 'sa-tab' + (name === 'dialogue' ? ' sa-active' : '');
            tabPhraseology.className = 'sa-tab' + (name === 'phraseology' ? ' sa-active' : '');
            tabCharts.className = 'sa-tab' + (name === 'charts' ? ' sa-active' : '');
            tabMap.className = 'sa-tab' + (name === 'map' ? ' sa-active' : '');
            updateVoiceBar();
            renderSuggestion(lastState);
            if (name === 'dialogue') { scrollDialogue(true); }
            if (name === 'charts') { loadCharts(); }
            if (name === 'map') {
                if (!mapCanvas) { buildMap(); }
                drawMap();
                pollMap();
            }
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

        // ---- The map: the flight plan, the airspaces around and the aircraft, drawn on a canvas -----
        // StormATC itself serves the map (/api/map/state, /api/map/data, /api/map/tile): the flight plan of the session, the
        // aircraft the simulator reports, the airspaces and the base map tiles. Nothing is fetched from the internet here.
        var MAP_POLL_MS = 1000;
        var MAP_FETCH_MIN_MS = 2500;
        var MAP_TRAIL_MAX = 150;
        var MAP_MAX_TILES = 90;
        var MAP_ZOOM_STEP = 1.5;
        var MAP_MIN_RANGE_NM = 0.25;
        var MAP_MAX_RANGE_NM = 400;
        // Colour, line width and dashes by kind of airspace.
        var MAP_ZONE_STYLE = {
            restricted: { colour: 'rgb(192, 40, 40)', width: 2, dashed: false },
            ctr: { colour: 'rgb(31, 90, 200)', width: 2, dashed: true },
            tma: { colour: 'rgb(50, 60, 170)', width: 1.5, dashed: false },
            radio: { colour: 'rgb(142, 68, 173)', width: 1.5, dashed: true },
            leisure: { colour: 'rgb(215, 125, 15)', width: 1.5, dashed: false },
            other: { colour: 'rgb(120, 120, 120)', width: 1, dashed: false }
        };
        var mapBar = null, mapWrap = null, mapCanvas = null, mapInfo = null, mapNote = null;
        var mapFollowButton = null, mapRotateButton = null;
        var mapState = null;
        var mapOffline = false;
        var mapData = null;
        var mapBox = null;
        var mapCenter = { lat: 46.6, lon: 2.4 };
        var mapRangeNm = 20;
        var mapFollow = storageGet('stormatc.mapFollow') !== '0';
        var mapHeadingUp = storageGet('stormatc.mapHeadingUp') === '1';
        var mapFitted = false;
        var mapFetching = false;
        var mapFetchedAt = 0;
        var mapTrail = [];
        var mapTrailAt = 0;
        var mapTimer = null;
        var mapGeometry = null;
        var mapTiles = {};
        var mapStyleButton = null, mapLayersButton = null, mapLayersRow = null;
        // What the map shows, chosen by the pilot and kept.
        var MAP_LAYER_KEYS = ['zones', 'airports', 'beacons', 'route', 'rings', 'trail'];
        var mapLayers = (function () {
            var chosen = {};
            var saved = null;
            try { saved = JSON.parse(storageGet('stormatc.mapLayers') || 'null'); } catch (e) { saved = null; }
            for (var i = 0; i < MAP_LAYER_KEYS.length; i++) {
                chosen[MAP_LAYER_KEYS[i]] = !(saved && saved[MAP_LAYER_KEYS[i]] === false);
            }
            return chosen;
        })();
        var mapLayersOpen = false;
        var mapStyleSignature = '';
        var mapLabelsReady = false;
        var mapTileKeys = [];

        function mapTexts() {
            var t = lastState && lastState.labels && lastState.labels.map;
            return t || {
                tab: 'Navigation', notRunning: 'The map is not available.', noRoute: 'No flight plan in StormATC.',
                noAircraft: 'Aircraft not tracked.', noZones: 'Airspaces are not loaded (OpenAIP key).',
                follow: 'Follow', northUp: 'North up', headingUp: 'Track up', fitRoute: 'Route', zoomIn: '+',
                zoomOut: '−', inside: 'You are in', next: 'Next point', altitude: 'Alt', groundSpeed: 'GS',
                heading: 'Hdg', truncated: 'Crowded map: zoom in.'
            };
        }

        function mapButton(text, onclick) {
            var b = el('button', 'sa-btn sa-chartnav', text);
            b.setAttribute('type', 'button');
            b.onclick = onclick;
            return b;
        }

        function buildMap() {
            clear(mapPane);
            mapLabelsReady = !!(lastState && lastState.labels && lastState.labels.map);
            var t = mapTexts();
            mapBar = el('div', 'sa-chartbar');
            mapBar.appendChild(mapButton(t.zoomOut, function () { zoomMap(1 / MAP_ZOOM_STEP); }));
            mapBar.appendChild(mapButton(t.zoomIn, function () { zoomMap(MAP_ZOOM_STEP); }));
            mapFollowButton = mapButton(t.follow, function () {
                mapFollow = !mapFollow;
                storageSet('stormatc.mapFollow', mapFollow ? '1' : '0');
                if (mapFollow) { centerOnAircraft(); }
                drawMap();
            });
            mapBar.appendChild(mapFollowButton);
            mapRotateButton = mapButton(mapHeadingUp ? t.headingUp : t.northUp, function () {
                mapHeadingUp = !mapHeadingUp;
                storageSet('stormatc.mapHeadingUp', mapHeadingUp ? '1' : '0');
                drawMap();
            });
            mapBar.appendChild(mapRotateButton);
            mapBar.appendChild(mapButton(t.fitRoute, function () { mapFollow = false; fitMapRoute(); drawMap(); }));
            // A button that goes to the next base map: a drop-down list cannot be used in VR.
            mapStyleButton = mapButton('', function () { nextMapStyle(); });
            mapBar.appendChild(mapStyleButton);
            mapLayersButton = mapButton(t.layers || 'Display', function () {
                mapLayersOpen = !mapLayersOpen;
                updateLayersRow();
                drawMap();
            });
            mapBar.appendChild(mapLayersButton);
            mapStyleSignature = '';

            mapWrap = el('div', 'sa-mapwrap');
            mapCanvas = document.createElement('canvas');
            mapCanvas.className = 'sa-mapcanvas';
            mapInfo = el('div', 'sa-mapinfo');
            mapNote = el('div', 'sa-mapnote');
            mapWrap.appendChild(mapCanvas);
            mapWrap.appendChild(mapInfo);
            mapWrap.appendChild(mapNote);
            mapLayersRow = el('div', 'sa-chartbar');
            mapPane.appendChild(mapBar);
            mapPane.appendChild(mapLayersRow);
            mapPane.appendChild(mapWrap);
            updateLayersRow();
            attachMapGestures();
        }

        function attachMapGestures() {
            var drag = null;
            function down(x, y) { drag = { x: x, y: y }; }
            function move(x, y) {
                if (!drag || !mapGeometry) { return; }
                panMap(x - drag.x, y - drag.y);
                drag.x = x;
                drag.y = y;
            }
            function up() { drag = null; }
            mapCanvas.onmousedown = function (e) { down(e.clientX, e.clientY); if (e.preventDefault) { e.preventDefault(); } };
            mapCanvas.onmousemove = function (e) { move(e.clientX, e.clientY); };
            mapCanvas.onmouseup = up;
            mapCanvas.onmouseleave = up;
            mapCanvas.ontouchstart = function (e) {
                if (e.touches && e.touches.length === 1) { down(e.touches[0].clientX, e.touches[0].clientY); }
                if (e.preventDefault) { e.preventDefault(); }
            };
            mapCanvas.ontouchmove = function (e) {
                if (e.touches && e.touches.length === 1) { move(e.touches[0].clientX, e.touches[0].clientY); }
                if (e.preventDefault) { e.preventDefault(); }
            };
            mapCanvas.ontouchend = up;
            mapCanvas.onwheel = function (e) {
                zoomMap(e.deltaY < 0 ? MAP_ZOOM_STEP : 1 / MAP_ZOOM_STEP);
                if (e.preventDefault) { e.preventDefault(); }
            };
        }

        // Moving the map by hand lets go of the aircraft.
        function panMap(dx, dy) {
            var g = mapGeometry;
            var rot = g.rotation;
            // Screen offset back to map offset: the inverse of the rotation used to draw.
            var mx = dx * Math.cos(rot) - dy * Math.sin(rot);
            var my = dx * Math.sin(rot) + dy * Math.cos(rot);
            mapCenter = { lat: fromMercator(mercator(mapCenter.lat) + my / g.scale), lon: mapCenter.lon - mx / g.scale * 180 / Math.PI };
            if (mapFollow) {
                mapFollow = false;
                storageSet('stormatc.mapFollow', '0');
            }
            drawMap();
            fetchMap();
        }

        function zoomMap(factor) {
            mapRangeNm = Math.max(MAP_MIN_RANGE_NM, Math.min(MAP_MAX_RANGE_NM, mapRangeNm / factor));
            drawMap();
            fetchMap();
        }

        function aircraftOf(state) {
            return state && state.aircraft && state.aircraftAgeMs >= 0 ? state.aircraft : null;
        }

        function aircraftIsLive(state) {
            return !!aircraftOf(state) && state.aircraftAgeMs < 6000;
        }

        function centerOnAircraft() {
            var a = aircraftOf(mapState);
            if (a) { mapCenter = { lat: a.lat, lon: a.lon }; }
        }

        function fitMapRoute() {
            var points = [];
            var route = (mapState && mapState.route) || [];
            for (var i = 0; i < route.length; i++) { points.push(route[i]); }
            var a = aircraftOf(mapState);
            if (a) { points.push(a); }
            if (!points.length) { return; }
            var minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
            for (var j = 0; j < points.length; j++) {
                minLat = Math.min(minLat, points[j].lat); maxLat = Math.max(maxLat, points[j].lat);
                minLon = Math.min(minLon, points[j].lon); maxLon = Math.max(maxLon, points[j].lon);
            }
            mapCenter = { lat: (minLat + maxLat) / 2, lon: (minLon + maxLon) / 2 };
            var heightNm = (maxLat - minLat) * 60;
            var widthNm = (maxLon - minLon) * 60 * Math.cos(mapCenter.lat * Math.PI / 180);
            var w = mapCanvas ? mapCanvas.clientWidth || 1 : 1;
            var h = mapCanvas ? mapCanvas.clientHeight || 1 : 1;
            // The range is the half-height in nautical miles: fit both dimensions with a margin.
            var needed = Math.max(heightNm / 2, widthNm / 2 * (h / w)) * 1.3;
            mapRangeNm = Math.max(MAP_MIN_RANGE_NM, Math.min(MAP_MAX_RANGE_NM, needed || 10));
            mapFitted = true;
        }

        function pollMap() {
            if (tab !== 'map') { return; }
            getJson('/api/map/state', function (data) {
                mapOffline = !data;
                mapState = data;
                if (data) {
                    var a = aircraftOf(data);
                    if (!mapFitted) {
                        if (a) { mapFitted = true; mapCenter = { lat: a.lat, lon: a.lon }; mapRangeNm = 15; }
                        else if (data.route && data.route.length) { fitMapRoute(); }
                    }
                    if (a && aircraftIsLive(data)) {
                        var now = new Date().getTime();
                        var last = mapTrail.length ? mapTrail[mapTrail.length - 1] : null;
                        if (!last || (now - mapTrailAt > 2000 && (Math.abs(last[0] - a.lat) + Math.abs(last[1] - a.lon)) > 0.0004)) {
                            mapTrail.push([a.lat, a.lon]);
                            mapTrailAt = now;
                            if (mapTrail.length > MAP_TRAIL_MAX) { mapTrail.shift(); }
                        }
                        if (mapFollow) { centerOnAircraft(); }
                    }
                }
                updateStyleMenu();
                drawMap();
                if (data) { fetchMap(); }
            });
        }

        // The rectangle the screen shows, and the one fetched around it (a margin, so a small move needs nothing).
        function mapViewBox() {
            var g = mapGeometry;
            if (!g) { return null; }
            var radius = Math.sqrt(g.w * g.w + g.h * g.h) / 2;
            var dLat = radius / g.ppd;
            var dLon = dLat / g.cosLat;
            return { minLat: mapCenter.lat - dLat, maxLat: mapCenter.lat + dLat, minLon: mapCenter.lon - dLon, maxLon: mapCenter.lon + dLon };
        }

        function fetchMap() {
            var view = mapViewBox();
            if (!view || mapFetching || tab !== 'map') { return; }
            var now = new Date().getTime();
            var covered = mapBox && mapBox.minLat <= view.minLat && mapBox.maxLat >= view.maxLat &&
                mapBox.minLon <= view.minLon && mapBox.maxLon >= view.maxLon;
            var tooBig = mapBox && (mapBox.maxLat - mapBox.minLat) > (view.maxLat - view.minLat) * 4;
            if (covered && !tooBig) { return; }
            if (now - mapFetchedAt < MAP_FETCH_MIN_MS) { return; }
            var dLat = (view.maxLat - view.minLat) * 0.8;
            var dLon = (view.maxLon - view.minLon) * 0.8;
            var box = { minLat: view.minLat - dLat / 2, maxLat: view.maxLat + dLat / 2,
                        minLon: view.minLon - dLon / 2, maxLon: view.maxLon + dLon / 2 };
            mapFetching = true;
            mapFetchedAt = now;
            getJson('/api/map/data?minLat=' + box.minLat.toFixed(4) + '&minLon=' + box.minLon.toFixed(4) +
                    '&maxLat=' + box.maxLat.toFixed(4) + '&maxLon=' + box.maxLon.toFixed(4), function (data) {
                mapFetching = false;
                if (data) {
                    // The projection of a latitude is worked out once here, not at every redraw for every point of every airspace.
                    var zs = data.airspaces || [];
                    for (var zi = 0; zi < zs.length; zi++) {
                        for (var ri = 0; ri < zs[zi].rings.length; ri++) {
                            var rg = zs[zi].rings[ri];
                            for (var pi2 = 0; pi2 < rg.length; pi2++) { rg[pi2][2] = mercator(rg[pi2][1]); }
                        }
                    }
                    mapData = data; mapBox = box; drawMap();
                }
            });
        }

        function toRad(d) { return d * Math.PI / 180; }

        function mercator(lat) { return Math.log(Math.tan(Math.PI / 4 + toRad(lat) / 2)); }

        function fromMercator(y) { return (2 * Math.atan(Math.exp(y)) - Math.PI / 2) * 180 / Math.PI; }

        // The base map in use: the pilot's choice when it can be drawn, else the one of the settings.
        function mapCurrentStyle() {
            var styles = (mapState && mapState.styles) || [];
            var wanted = storageGet('stormatc.mapStyle');
            var fallback = null;
            for (var i = 0; i < styles.length; i++) {
                if (styles[i].available && styles[i].key === wanted) { return styles[i]; }
                if (mapState.baseMap && styles[i].key === mapState.baseMap.key) { fallback = styles[i]; }
            }
            return fallback || (mapState && mapState.baseMap ? { key: mapState.baseMap.key, layers: mapState.baseMap.layers, maxZoom: mapState.baseMap.maxZoom, available: true } : null);
        }

        function mapStyleName(key, t) {
            var names = { 'clair': t.styleClair, 'carto-light': t.styleCartoLight, 'carto-voyager': t.styleCartoVoyager, 'relief': t.styleRelief, 'rues': t.styleRues };
            return names[key] || key;
        }

        // The base map button: shows the map in use; a press goes to the next one that can be drawn.
        function updateStyleMenu() {
            if (!mapStyleButton || !mapState || !mapState.styles) { return; }
            var t = mapTexts();
            var current = mapCurrentStyle();
            var label = (t.styleLabel || 'Map') + ' : ' + (current ? mapStyleName(current.key, t) : '');
            if (mapStyleButton.textContent !== label) { mapStyleButton.textContent = label; }
        }

        function nextMapStyle() {
            var styles = (mapState && mapState.styles) || [];
            var current = mapCurrentStyle();
            var index = -1;
            for (var i = 0; i < styles.length; i++) { if (current && styles[i].key === current.key) { index = i; } }
            for (var step = 1; step <= styles.length; step++) {
                var candidate = styles[(index + step) % styles.length];
                if (candidate && candidate.available) {
                    storageSet('stormatc.mapStyle', candidate.key);
                    break;
                }
            }
            updateStyleMenu();
            drawMap();
        }

        function layerName(key, t) {
            var names = { zones: t.layerZones, airports: t.layerAirports, beacons: t.layerBeacons, route: t.layerRoute, rings: t.layerRings, trail: t.layerTrail };
            return names[key] || key;
        }

        // The row of switches under the bar: one per kind of thing the map can show.
        function updateLayersRow() {
            if (!mapLayersRow) { return; }
            var t = mapTexts();
            clear(mapLayersRow);
            mapLayersRow.style.display = mapLayersOpen ? '' : 'none';
            if (mapLayersButton) { mapLayersButton.className = 'sa-btn sa-chartnav' + (mapLayersOpen ? ' sa-on' : ''); }
            if (!mapLayersOpen) { return; }
            MAP_LAYER_KEYS.forEach(function (key) {
                var b = mapButton(layerName(key, t), function () {
                    mapLayers[key] = !mapLayers[key];
                    storageSet('stormatc.mapLayers', JSON.stringify(mapLayers));
                    updateLayersRow();
                    drawMap();
                });
                if (mapLayers[key]) { b.className = 'sa-btn sa-chartnav sa-on'; }
                mapLayersRow.appendChild(b);
            });
        }

        // Tiles arrive one by one: one redraw for the ones that arrive together, not one each.
        var mapDrawTimer = null;
        function scheduleMapDraw() {
            if (mapDrawTimer) { return; }
            mapDrawTimer = window.setTimeout(function () { mapDrawTimer = null; drawMap(); }, 40);
        }

        // A tile that is already there (no request).
        function mapTileIfReady(base, layer, z, x, y) {
            var tile = mapTiles[base + '/' + layer + '/' + z + '/' + x + '/' + y];
            return tile && tile.ready ? tile.image : null;
        }

        // A tile of the base map, asked from StormATC once and kept; null while it is coming.
        function mapTile(base, layer, z, x, y) {
            var key = base + '/' + layer + '/' + z + '/' + x + '/' + y;
            var tile = mapTiles[key];
            if (tile) { return tile.ready ? tile.image : null; }
            if (mapTileKeys.length > 2500) {
                var old = mapTileKeys.splice(0, 800);
                for (var i = 0; i < old.length; i++) { delete mapTiles[old[i]]; }
            }
            var image = new Image();
            tile = { image: image, ready: false };
            image.onload = function () { tile.ready = true; scheduleMapDraw(); };
            image.onerror = function () {
                tile.ready = false;
                window.setTimeout(function () { if (mapTiles[key] === tile) { delete mapTiles[key]; scheduleMapDraw(); } }, 4000);
            };
            image.src = baseUrl() + '/api/map/tile/' + base + '/' + layer + '/' + z + '/' + x + '/' + y;
            mapTiles[key] = tile;
            mapTileKeys.push(key);
            return null;
        }

        function distanceNm(lat1, lon1, lat2, lon2) {
            var dLat = toRad(lat2 - lat1);
            var dLon = toRad(lon2 - lon1);
            var a = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
                Math.cos(toRad(lat1)) * Math.cos(toRad(lat2)) * Math.sin(dLon / 2) * Math.sin(dLon / 2);
            return 2 * 3440.065 * Math.asin(Math.min(1, Math.sqrt(a)));
        }

        function ringContains(ring, lon, lat) {
            var inside = false;
            for (var i = 0, j = ring.length - 1; i < ring.length; j = i++) {
                var xi = ring[i][0], yi = ring[i][1], xj = ring[j][0], yj = ring[j][1];
                if ((yi > lat) !== (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) { inside = !inside; }
            }
            return inside;
        }

        function zoneContains(zone, lon, lat) {
            for (var r = 0; r < zone.rings.length; r++) {
                if (ringContains(zone.rings[r], lon, lat)) { return true; }
            }
            return false;
        }

        function formatLimit(text) { return text || '?'; }

        // The leg the aircraft is closest to; its end is the next point.
        function nextWaypoint(route, a) {
            if (!route || route.length < 2 || !a) { return route && route.length === 1 ? 0 : -1; }
            var best = -1, bestDistance = 1e9;
            var cos = Math.cos(toRad(a.lat));
            for (var i = 0; i < route.length - 1; i++) {
                var ax = route[i].lon * cos, ay = route[i].lat, bx = route[i + 1].lon * cos, by = route[i + 1].lat;
                var px = a.lon * cos, py = a.lat;
                var dx = bx - ax, dy = by - ay;
                var len2 = dx * dx + dy * dy;
                var t = len2 > 0 ? Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / len2)) : 0;
                var d = Math.sqrt(Math.pow(px - (ax + t * dx), 2) + Math.pow(py - (ay + t * dy), 2));
                if (d < bestDistance) { bestDistance = d; best = i + 1; }
            }
            return best;
        }

        // The diagonal hatching inside restricted areas.
        function mapHatch(ctx, colour) {
            try {
                var tile = document.createElement('canvas');
                tile.width = 8;
                tile.height = 8;
                var c = tile.getContext('2d');
                c.strokeStyle = colour;
                c.globalAlpha = 150 / 255;
                c.lineWidth = 1.6;
                c.beginPath();
                c.moveTo(-1, 9);
                c.lineTo(9, -1);
                c.stroke();
                return ctx.createPattern(tile, 'repeat');
            } catch (e) { return null; }
        }

        // Liang-Barsky: the part of a segment that lies inside a rectangle, or null.
        function clipSegment(a, b, left, top, right, bottom) {
            var t0 = 0, t1 = 1, dx = b[0] - a[0], dy = b[1] - a[1];
            var p = [-dx, dx, -dy, dy];
            var q = [a[0] - left, right - a[0], a[1] - top, bottom - a[1]];
            for (var i = 0; i < 4; i++) {
                if (p[i] === 0) { if (q[i] < 0) { return null; } continue; }
                var t = q[i] / p[i];
                if (p[i] < 0) { if (t > t1) { return null; } if (t > t0) { t0 = t; } }
                else { if (t < t0) { return null; } if (t < t1) { t1 = t; } }
            }
            return [a[0] + t0 * dx, a[1] + t0 * dy, a[0] + t1 * dx, a[1] + t1 * dy];
        }

        // The simulator's browser does not always repaint a canvas that was drawn on: only the places that something else
        // (the info lines) invalidated showed the new map. A change of style after each drawing forces the whole canvas.
        var mapFlip = false;
        function drawMap() {
            drawMapNow();
            if (mapCanvas) {
                mapFlip = !mapFlip;
                mapCanvas.style.opacity = mapFlip ? '0.999' : '1';
            }
        }

        function drawMapNow() {
            if (tab !== 'map' || !mapCanvas) { return; }
            var t = mapTexts();
            var ratio = window.devicePixelRatio || 1;
            var w = mapCanvas.clientWidth || mapWrap.clientWidth || 300;
            var h = mapCanvas.clientHeight || mapWrap.clientHeight || 300;
            if (mapCanvas.width !== Math.round(w * ratio) || mapCanvas.height !== Math.round(h * ratio)) {
                mapCanvas.width = Math.round(w * ratio);
                mapCanvas.height = Math.round(h * ratio);
            }
            var ctx = mapCanvas.getContext('2d');
            ctx.setTransform(ratio, 0, 0, ratio, 0, 0);

            var a = aircraftOf(mapState);
            var live = aircraftIsLive(mapState);
            var ppd = (h / 2) / (mapRangeNm / 60);
            var cosLat = Math.cos(toRad(mapCenter.lat));
            // Web Mercator, like the tiles: scale is the number of pixels per radian of longitude.
            var scaleX = ppd * cosLat * 180 / Math.PI;
            var rot = mapHeadingUp && a && live ? toRad(a.headingTrue) : 0;
            mapGeometry = { w: w, h: h, ppd: ppd, cosLat: cosLat, rotation: rot, scale: scaleX };
            var centreY = mercator(mapCenter.lat);
            var cosRot = Math.cos(rot), sinRot = Math.sin(rot);

            function project(lat, lon) {
                var dx = toRad(lon - mapCenter.lon) * scaleX;
                var dy = -(mercator(lat) - centreY) * scaleX;
                return [w / 2 + dx * cosRot + dy * sinRot, h / 2 - dx * sinRot + dy * cosRot];
            }

            // The same for a point whose projected latitude is already known (the airspaces).
            function projectMerc(lon, merc) {
                var dx = toRad(lon - mapCenter.lon) * scaleX;
                var dy = -(merc - centreY) * scaleX;
                return [w / 2 + dx * cosRot + dy * sinRot, h / 2 - dx * sinRot + dy * cosRot];
            }

            var px = Math.max(10, Math.round(10 * scale));
            ctx.fillStyle = '#e8ebee';
            ctx.fillRect(0, 0, w, h);
            drawMapTiles(ctx, w, h, scaleX, centreY, rot);
            ctx.lineJoin = 'round';
            ctx.textBaseline = 'middle';

            var altitude = a ? a.altitudeFt : (mapState ? mapState.cruiseAltFt : 0);
            var inside = [];
            var labels = [];

            // Airspaces: a coloured outline, a band inside it (hatched for
            // restricted areas) and the name and limits written along the longest visible side.
            var zones = (mapData && mapData.airspaces) || [];
            var hatch = null;
            for (var z = 0; z < zones.length; z++) {
                var zone = zones[z];
                var look = MAP_ZONE_STYLE[zone.kind] || MAP_ZONE_STYLE.other;
                if (a && zone.lowerFt <= altitude && zone.upperFt >= altitude && zoneContains(zone, a.lon, a.lat)) { inside.push(zone); }
                if (!mapLayers.zones) { continue; }
                for (var r = 0; r < zone.rings.length; r++) {
                    var ring = zone.rings[r];
                    if (ring.length < 4) { continue; }
                    var pts = [];
                    var minX = 1e9, maxX = -1e9, minY = 1e9, maxY = -1e9;
                    for (var k = 0; k < ring.length; k++) {
                        var p = ring[k][2] !== undefined ? projectMerc(ring[k][0], ring[k][2]) : project(ring[k][1], ring[k][0]);
                        pts.push(p);
                        minX = Math.min(minX, p[0]); maxX = Math.max(maxX, p[0]);
                        minY = Math.min(minY, p[1]); maxY = Math.max(maxY, p[1]);
                    }
                    if (maxX < 0 || minX > w || maxY < 0 || minY > h || (maxX - minX < 3 && maxY - minY < 3)) { continue; }
                    ctx.beginPath();
                    for (var q0 = 0; q0 < pts.length; q0++) {
                        if (q0 === 0) { ctx.moveTo(pts[q0][0], pts[q0][1]); } else { ctx.lineTo(pts[q0][0], pts[q0][1]); }
                    }
                    ctx.closePath();
                    ctx.save();
                    ctx.clip();
                    ctx.lineWidth = 14;
                    if (zone.kind === 'restricted') {
                        if (!hatch) { hatch = mapHatch(ctx, look.colour); }
                        ctx.strokeStyle = hatch || look.colour;
                    } else {
                        ctx.globalAlpha = 40 / 255;
                        ctx.strokeStyle = look.colour;
                    }
                    ctx.stroke();
                    ctx.restore();
                    ctx.strokeStyle = look.colour;
                    ctx.lineWidth = look.width;
                    if (ctx.setLineDash) { ctx.setLineDash(look.dashed ? [9, 5] : []); }
                    ctx.stroke();
                    if (ctx.setLineDash) { ctx.setLineDash([]); }
                    labels.push({ pts: pts, zone: zone, colour: look.colour, minX: minX, maxX: maxX, minY: minY, maxY: maxY });
                }
            }

            // Range rings around the aircraft.
            if (a && mapLayers.rings) {
                var centre = project(a.lat, a.lon);
                var step = mapRangeNm > 60 ? 50 : (mapRangeNm > 25 ? 20 : (mapRangeNm > 10 ? 10 : 5));
                ctx.strokeStyle = 'rgba(60, 80, 100, 0.35)';
                ctx.lineWidth = 1;
                ctx.fillStyle = 'rgba(40, 60, 80, 0.8)';
                ctx.font = (px - 1) + 'px sans-serif';
                for (var nm = step; nm <= mapRangeNm * 2.5; nm += step) {
                    var radius = nm / 60 * ppd;
                    ctx.beginPath();
                    ctx.arc(centre[0], centre[1], radius, 0, Math.PI * 2);
                    ctx.stroke();
                    ctx.fillText(nm + ' NM', centre[0] + 4, centre[1] - radius + px * 0.7);
                }
            }

            // Aerodromes.
            var airports = (mapData && mapData.airports) || [];
            if (mapLayers.airports && mapRangeNm <= 60) {
                ctx.font = (px - 1) + 'px sans-serif';
                for (var q = 0; q < airports.length; q++) {
                    var ap = project(airports[q].lat, airports[q].lon);
                    if (ap[0] < 0 || ap[0] > w || ap[1] < 0 || ap[1] > h) { continue; }
                    ctx.fillStyle = 'rgb(31, 111, 224)';
                    ctx.strokeStyle = '#ffffff';
                    ctx.lineWidth = 1.5;
                    ctx.beginPath();
                    ctx.arc(ap[0], ap[1], 4.5, 0, Math.PI * 2);
                    ctx.fill();
                    ctx.stroke();
                    if (mapRangeNm <= 25) {
                        var tw2 = ctx.measureText(airports[q].ident).width;
                        ctx.fillStyle = 'rgba(255, 255, 255, 0.63)';
                        ctx.fillRect(ap[0] + 7, ap[1] - px * 0.6, tw2 + 4, px * 1.2);
                        ctx.fillStyle = 'rgb(11, 42, 85)';
                        ctx.fillText(airports[q].ident, ap[0] + 9, ap[1]);
                    }
                }
            }

            // Beacons (VOR, NDB).
            var beacons = (mapData && mapData.navaids) || [];
            if (mapLayers.beacons && mapRangeNm <= 40) {
                ctx.font = (px - 1) + 'px sans-serif';
                for (var bi = 0; bi < beacons.length; bi++) {
                    var bp = project(beacons[bi].lat, beacons[bi].lon);
                    if (bp[0] < 0 || bp[0] > w || bp[1] < 0 || bp[1] > h) { continue; }
                    var vor = beacons[bi].kind === 'vor';
                    ctx.strokeStyle = vor ? 'rgb(142, 68, 173)' : 'rgb(215, 125, 15)';
                    ctx.fillStyle = vor ? 'rgba(142, 68, 173, 0.25)' : 'rgba(215, 125, 15, 0.25)';
                    ctx.lineWidth = 2;
                    ctx.beginPath();
                    if (vor) {
                        for (var hx = 0; hx < 6; hx++) {
                            var ang = Math.PI / 3 * hx + Math.PI / 6;
                            var hxp = bp[0] + 7 * Math.cos(ang), hyp = bp[1] + 7 * Math.sin(ang);
                            if (hx === 0) { ctx.moveTo(hxp, hyp); } else { ctx.lineTo(hxp, hyp); }
                        }
                        ctx.closePath();
                    } else {
                        ctx.arc(bp[0], bp[1], 5, 0, Math.PI * 2);
                    }
                    ctx.fill();
                    ctx.stroke();
                    if (mapRangeNm <= 20) {
                        var bt = beacons[bi].ident + ' ' + (vor ? (beacons[bi].frequencyKhz / 1000).toFixed(3) : Math.round(beacons[bi].frequencyKhz));
                        ctx.strokeStyle = 'rgba(255, 255, 255, 0.92)';
                        ctx.lineWidth = 3;
                        ctx.fillStyle = vor ? 'rgb(100, 40, 130)' : 'rgb(150, 85, 10)';
                        ctx.strokeText(bt, bp[0] + 10, bp[1]);
                        ctx.fillText(bt, bp[0] + 10, bp[1]);
                    }
                }
            }

            // Route: a white casing under a red line, numbered round markers (blue for aerodromes).
            var route = (mapLayers.route && mapState && mapState.route) || [];
            var nextIndex = nextWaypoint(route, a);
            if (route.length > 1) {
                ctx.beginPath();
                for (var s = 0; s < route.length; s++) {
                    var rp = project(route[s].lat, route[s].lon);
                    if (s === 0) { ctx.moveTo(rp[0], rp[1]); } else { ctx.lineTo(rp[0], rp[1]); }
                }
                ctx.strokeStyle = '#ffffff';
                ctx.lineWidth = 8;
                ctx.stroke();
                ctx.strokeStyle = 'rgb(228, 40, 30)';
                ctx.lineWidth = 4;
                ctx.stroke();
            }
            ctx.font = 'bold ' + (px - 1) + 'px sans-serif';
            ctx.textAlign = 'center';
            for (var n = 0; n < route.length; n++) {
                var wp = project(route[n].lat, route[n].lon);
                ctx.fillStyle = route[n].icao ? 'rgb(31, 111, 224)' : 'rgb(228, 40, 30)';
                ctx.strokeStyle = n === nextIndex ? '#ffd166' : '#ffffff';
                ctx.lineWidth = 2.5;
                ctx.beginPath();
                ctx.arc(wp[0], wp[1], 8, 0, Math.PI * 2);
                ctx.fill();
                ctx.stroke();
                ctx.fillStyle = '#ffffff';
                ctx.fillText(String(n + 1), wp[0], wp[1]);
            }
            ctx.textAlign = 'start';
            ctx.font = 'bold ' + px + 'px sans-serif';
            for (var nn = 0; nn < route.length; nn++) {
                var wq = project(route[nn].lat, route[nn].lon);
                ctx.strokeStyle = 'rgba(255, 255, 255, 0.92)';
                ctx.lineWidth = 3;
                ctx.fillStyle = 'rgb(11, 42, 85)';
                ctx.strokeText(route[nn].name, wq[0] + 11, wq[1] - 11);
                ctx.fillText(route[nn].name, wq[0] + 11, wq[1] - 11);
            }

            // Zone labels, written along the longest visible side, without overlapping.
            ctx.font = px + 'px sans-serif';
            var placed = [];
            for (var l = 0; l < labels.length; l++) {
                var lab = labels[l];
                if (mapRangeNm > 400) { break; }
                var best = null;
                var bestLength = 0;
                for (var e = 0; e < lab.pts.length - 1; e++) {
                    var clipped = clipSegment(lab.pts[e], lab.pts[e + 1], 6, 6, w - 6, h - 6);
                    if (!clipped) { continue; }
                    var len = Math.sqrt(Math.pow(clipped[2] - clipped[0], 2) + Math.pow(clipped[3] - clipped[1], 2));
                    if (len > bestLength) { bestLength = len; best = clipped; }
                }
                if (bestLength < 28) { continue; }
                var full = lab.zone.name + '   ' + formatLimit(lab.zone.lower) + ' - ' + formatLimit(lab.zone.upper);
                var text = null;
                var candidates = [full, lab.zone.name];
                for (var cI = 0; cI < candidates.length; cI++) {
                    if (ctx.measureText(candidates[cI]).width <= bestLength - 6) { text = candidates[cI]; break; }
                }
                if (!text) { continue; }
                var tw = ctx.measureText(text).width;
                var x1 = best[0], y1 = best[1], x2 = best[2], y2 = best[3];
                if (x2 < x1) { var tx = x1; x1 = x2; x2 = tx; var ty = y1; y1 = y2; y2 = ty; }
                var angle = Math.atan2(y2 - y1, x2 - x1);
                var mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
                var box = { x: mx - tw / 2 - px * 0.7, y: my - tw / 2 * Math.abs(Math.sin(angle)) - px, w: tw + px * 1.4, h: tw * Math.abs(Math.sin(angle)) + px * 2 };
                var clash = false;
                for (var c = 0; c < placed.length; c++) {
                    if (box.x < placed[c].x + placed[c].w && box.x + box.w > placed[c].x &&
                        box.y < placed[c].y + placed[c].h && box.y + box.h > placed[c].y) { clash = true; break; }
                }
                if (clash) { continue; }
                placed.push(box);
                // On the inside of the zone: the side of the segment where the middle of the zone lies.
                var cx = (lab.minX + lab.maxX) / 2, cy = (lab.minY + lab.maxY) / 2;
                var side = ((x2 - x1) * (cy - y1) - (y2 - y1) * (cx - x1)) > 0 ? px + 1 : -3;
                ctx.save();
                ctx.translate(mx, my);
                ctx.rotate(angle);
                ctx.strokeStyle = 'rgba(255, 255, 255, 0.95)';
                ctx.lineWidth = 3.5;
                ctx.lineJoin = 'round';
                ctx.strokeText(text, -tw / 2, side);
                ctx.fillStyle = lab.colour;
                ctx.fillText(text, -tw / 2, side);
                ctx.restore();
            }

            // The track flown, then the aircraft.
            if (mapLayers.trail && mapTrail.length > 1) {
                ctx.beginPath();
                for (var m = 0; m < mapTrail.length; m++) {
                    var tp = project(mapTrail[m][0], mapTrail[m][1]);
                    if (m === 0) { ctx.moveTo(tp[0], tp[1]); } else { ctx.lineTo(tp[0], tp[1]); }
                }
                ctx.strokeStyle = 'rgba(30, 60, 90, 0.7)';
                ctx.lineWidth = 2;
                ctx.stroke();
            }
            if (a) {
                var ap2 = project(a.lat, a.lon);
                ctx.save();
                ctx.translate(ap2[0], ap2[1]);
                ctx.rotate(toRad(a.headingTrue) - rot);
                var big = 2 * Math.max(1, scale / 1.2);
                ctx.scale(big, big);
                ctx.fillStyle = live ? '#ffffff' : '#9aa7b4';
                ctx.strokeStyle = '#0d1620';
                ctx.lineWidth = 1.5;
                ctx.beginPath();
                ctx.moveTo(0, -14); ctx.lineTo(3, -4); ctx.lineTo(13, 3); ctx.lineTo(13, 6); ctx.lineTo(3, 3);
                ctx.lineTo(2, 10); ctx.lineTo(6, 13); ctx.lineTo(6, 15); ctx.lineTo(0, 13);
                ctx.lineTo(-6, 15); ctx.lineTo(-6, 13); ctx.lineTo(-2, 10); ctx.lineTo(-3, 3);
                ctx.lineTo(-13, 6); ctx.lineTo(-13, 3); ctx.lineTo(-3, -4);
                ctx.closePath();
                ctx.fill();
                ctx.stroke();
                ctx.restore();
            }

            // North marker and scale bar.
            var north = rot ? '↑ N ' + Math.round(-rot * 180 / Math.PI + 360) % 360 + '°' : 'N';
            ctx.font = 'bold ' + px + 'px sans-serif';
            ctx.fillStyle = '#34495e';
            ctx.fillText(north, w - px * 4, px * 1.2);
            var barNm = mapRangeNm > 60 ? 20 : (mapRangeNm > 25 ? 10 : (mapRangeNm > 10 ? 5 : (mapRangeNm > 4 ? 2 : 1)));
            var barPx = barNm / 60 * ppd;
            ctx.strokeStyle = '#1b2a3a';
            ctx.lineWidth = 3;
            ctx.beginPath();
            ctx.moveTo(10, h - 14); ctx.lineTo(10 + barPx, h - 14);
            ctx.stroke();
            ctx.fillStyle = '#1b2a3a';
            ctx.fillText(barNm + ' NM', 10, h - 26);

            // Overlay texts.
            mapFollowButton.className = 'sa-btn sa-chartnav' + (mapFollow ? ' sa-on' : '');
            mapRotateButton.textContent = mapHeadingUp ? t.headingUp : t.northUp;
            renderMapInfo(t, a, live, inside, route, nextIndex);
        }

        // The base map, drawn under everything else.
        function drawMapTiles(ctx, w, h, scaleX, centreY, rot) {
            var base = mapCurrentStyle();
            if (!base || !base.layers) { return; }
            var z = Math.round(Math.log(scaleX * 2 * Math.PI / 256) / Math.LN2);
            z = Math.max(0, Math.min(base.maxZoom, z));
            // The tiles that the screen can show: its two half-sizes, or its half-diagonal when the map is turned. When there
            // would be too many (a wide panel), a coarser zoom is used rather than drawing nothing.
            var halfW = rot ? Math.sqrt(w * w + h * h) / 2 : w / 2;
            var halfH = rot ? Math.sqrt(w * w + h * h) / 2 : h / 2;
            var tiles, size, x0, x1, y0, y1;
            for (;;) {
                tiles = Math.pow(2, z);
                size = 256 * (scaleX * 2 * Math.PI / (256 * tiles));
                var fx = function (px) { return ((mapCenter.lon + px / scaleX * 180 / Math.PI) + 180) / 360 * tiles; };
                var fy = function (py) { return (1 - (centreY - py / scaleX) / Math.PI) / 2 * tiles; };
                x0 = Math.floor(fx(-halfW)); x1 = Math.floor(fx(halfW));
                y0 = Math.max(0, Math.floor(fy(-halfH))); y1 = Math.min(tiles - 1, Math.floor(fy(halfH)));
                if ((x1 - x0 + 1) * (y1 - y0 + 1) <= MAP_MAX_TILES || z <= 0) { break; }
                z -= 1;
            }
            var cx = (mapCenter.lon + 180) / 360 * tiles;
            var cy = (1 - centreY / Math.PI) / 2 * tiles;
            // The tiles of the screen, the ones near the middle asked first (the browser asks a few at a time).
            var wanted = [];
            for (var ty = y0; ty <= y1; ty++) {
                for (var tx = x0; tx <= x1; tx++) {
                    wanted.push({ tx: tx, ty: ty, d: Math.pow(tx + 0.5 - cx, 2) + Math.pow(ty + 0.5 - cy, 2) });
                }
            }
            wanted.sort(function (a, b) { return a.d - b.d; });
            ctx.save();
            ctx.translate(w / 2, h / 2);
            ctx.rotate(-rot);
            var missing = 0;
            for (var layer = 0; layer < base.layers; layer++) {
                for (var wi = 0; wi < wanted.length; wi++) {
                    var tx2 = wanted[wi].tx, ty2 = wanted[wi].ty;
                    var wrapped = ((tx2 % tiles) + tiles) % tiles;
                    var image = mapTile(base.key, layer, z, wrapped, ty2);
                    var dx = (tx2 - cx) * size - 0.5, dy = (ty2 - cy) * size - 0.5;
                    if (image) {
                        ctx.drawImage(image, dx, dy, size + 1, size + 1);
                        continue;
                    }
                    missing++;
                    // While it comes, the same place of a coarser tile that is already there, enlarged: never a hole.
                    for (var k = 1; k <= 4 && z - k >= 0; k++) {
                        var parent = mapTileIfReady(base.key, layer, z - k, wrapped >> k, ty2 >> k);
                        if (parent) {
                            var span = 256 / Math.pow(2, k);
                            ctx.drawImage(parent, (wrapped & (Math.pow(2, k) - 1)) * span, (ty2 & (Math.pow(2, k) - 1)) * span, span, span,
                                dx, dy, size + 1, size + 1);
                            break;
                        }
                    }
                }
            }
            ctx.restore();
            // Everything is there: ask for a ring of tiles around, so the next move finds them ready.
            if (missing === 0) {
                for (var pl = 0; pl < base.layers; pl++) {
                    for (var py = y0 - 1; py <= y1 + 1; py++) {
                        for (var px2 = x0 - 1; px2 <= x1 + 1; px2++) {
                            if (py < 0 || py >= tiles || (px2 >= x0 && px2 <= x1 && py >= y0 && py <= y1)) { continue; }
                            mapTile(base.key, pl, z, ((px2 % tiles) + tiles) % tiles, py);
                        }
                    }
                }
            }
        }

        function renderMapInfo(t, a, live, inside, route, nextIndex) {
            clear(mapInfo);
            mapNote.textContent = '';
            if (mapOffline) { mapNote.textContent = t.notRunning; return; }
            if (!mapState) { return; }
            var lines = [];
            if (a) {
                var parts = [t.altitude + ' ' + Math.round(a.altitudeFt) + ' ft', t.groundSpeed + ' ' + Math.round(a.groundSpeedKt) + ' kt'];
                var hdg = Math.round(a.headingTrue);
                parts.push(t.heading + ' ' + (hdg < 10 ? '00' : (hdg < 100 ? '0' : '')) + hdg + '°');
                lines.push(parts.join('   '));
                if (nextIndex >= 0 && route[nextIndex]) {
                    var wp = route[nextIndex];
                    var nm = distanceNm(a.lat, a.lon, wp.lat, wp.lon);
                    var text = t.next + ' : ' + wp.name + '  ' + nm.toFixed(1) + ' NM';
                    if (a.groundSpeedKt > 30) { text += '  ' + Math.round(nm / a.groundSpeedKt * 60) + ' min'; }
                    lines.push(text);
                }
                if (inside.length) {
                    var names = [];
                    for (var i = 0; i < inside.length && i < 4; i++) {
                        names.push(inside[i].name + ' (' + formatLimit(inside[i].lower) + '–' + formatLimit(inside[i].upper) + ')');
                    }
                    lines.push(t.inside + ' : ' + names.join(', '));
                }
            }
            for (var j = 0; j < lines.length; j++) { mapInfo.appendChild(el('div', 'sa-mapline', lines[j])); }
            var notes = [];
            if (!live) { notes.push(t.noAircraft); }
            if (!(mapState.route && mapState.route.length)) { notes.push(t.noRoute); }
            if (mapData && !mapData.airspacesLoaded) { notes.push(t.noZones); }
            if (mapData && mapData.truncated) { notes.push(t.truncated); }
            mapNote.textContent = notes.join('  •  ');
        }

        mapTimer = window.setInterval(pollMap, MAP_POLL_MS);

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

            militaryPane.appendChild(el('h3', 'sa-always', m.labels.marsa));
            militaryPane.appendChild(el('div', 'sa-milnote', m.marsa || m.labels.noMarsa));

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
            if (state.labels.map) {
                var mapTitle = state.labels.map.tab;
                tabMap.textContent = mapTitle;
                if (mapCanvas && !mapLabelsReady) { buildMap(); drawMap(); }
            }
            var wasAtBottom = dialoguePane.scrollHeight - dialoguePane.scrollTop - dialoguePane.clientHeight < 40;
            stickToBottom = stickToBottom || wasAtBottom;
            renderDialogue(state);
            renderSuggestion(state);
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
        tabMap.onclick = function () { showTab('map'); };

        voiceBar.style.display = 'none';
        tabDialogue.textContent = 'Dialogue ATC';
        tabPhraseology.textContent = 'Phraséologie';
        tabCharts.textContent = 'Cartes';
        tabMap.textContent = 'Navigation';
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
                if (mapTimer) { window.clearInterval(mapTimer); }
                if (timer) { window.clearTimeout(timer); }
            }
        };
    }

    return { mount: mount, postJson: postJson };
}());
