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
        tabs.appendChild(tabCharts);
        tabs.appendChild(tabMap);
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

        // ---- The map: NavVFR's route, the airspaces around and the aircraft, drawn on a canvas -----
        // StormATC relays NavVFR's local feed (/api/navvfr/state, /api/navvfr/map). Nothing is fetched
        // from the internet here: the map is vector only, so it works in the simulator and in VR.
        var MAP_POLL_MS = 1000;
        var MAP_FETCH_MIN_MS = 2500;
        var MAP_TRAIL_MAX = 150;
        var MAP_ZOOM_STEP = 1.5;
        var MAP_MIN_RANGE_NM = 1.5;
        var MAP_MAX_RANGE_NM = 400;
        var MAP_ZONE_STYLE = {
            restricted: '#ff5c5c', ctr: '#4aa3ff', tma: '#9b8cff', radio: '#35d39a', leisure: '#ffb347', sector: '#7f93a8',
            other: '#7f93a8'
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

        function mapTexts() {
            var t = lastState && lastState.labels && lastState.labels.map;
            return t || {
                tab: 'Map', notRunning: 'NavVFR is not running.', noRoute: 'No route in NavVFR.',
                noAircraft: 'Aircraft not tracked.', noZones: 'Airspaces are not loaded in NavVFR.',
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

            mapWrap = el('div', 'sa-mapwrap');
            mapCanvas = document.createElement('canvas');
            mapCanvas.className = 'sa-mapcanvas';
            mapInfo = el('div', 'sa-mapinfo');
            mapNote = el('div', 'sa-mapnote');
            mapWrap.appendChild(mapCanvas);
            mapWrap.appendChild(mapInfo);
            mapWrap.appendChild(mapNote);
            mapPane.appendChild(mapBar);
            mapPane.appendChild(mapWrap);
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
            mapCenter = { lat: mapCenter.lat + my / g.ppd, lon: mapCenter.lon - mx / (g.ppd * g.cosLat) };
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
            getJson('/api/navvfr/state', function (data) {
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
            getJson('/api/navvfr/map?minLat=' + box.minLat.toFixed(4) + '&minLon=' + box.minLon.toFixed(4) +
                    '&maxLat=' + box.maxLat.toFixed(4) + '&maxLon=' + box.maxLon.toFixed(4), function (data) {
                mapFetching = false;
                if (data) { mapData = data; mapBox = box; drawMap(); }
            });
        }

        function toRad(d) { return d * Math.PI / 180; }

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

        function drawMap() {
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
            var rot = mapHeadingUp && a && live ? toRad(a.headingTrue) : 0;
            mapGeometry = { w: w, h: h, ppd: ppd, cosLat: cosLat, rotation: rot };

            function project(lat, lon) {
                var dx = (lon - mapCenter.lon) * cosLat * ppd;
                var dy = -(lat - mapCenter.lat) * ppd;
                return [w / 2 + dx * Math.cos(rot) + dy * Math.sin(rot), h / 2 - dx * Math.sin(rot) + dy * Math.cos(rot)];
            }

            var px = Math.max(10, Math.round(10 * scale));
            ctx.fillStyle = '#0d1620';
            ctx.fillRect(0, 0, w, h);
            ctx.lineJoin = 'round';
            ctx.textBaseline = 'middle';

            var altitude = a ? a.altitudeFt : (mapState ? mapState.cruiseAltFt : 0);
            var inside = [];
            var labels = [];

            // Airspaces: those at the aircraft's altitude strongly, the others faintly.
            var zones = (mapData && mapData.airspaces) || [];
            for (var z = 0; z < zones.length; z++) {
                var zone = zones[z];
                var colour = MAP_ZONE_STYLE[zone.kind] || MAP_ZONE_STYLE.other;
                var here = zone.lowerFt <= altitude && zone.upperFt >= altitude;
                var above = zone.lowerFt > altitude;
                var isInside = false;
                var labelAt = null;
                for (var r = 0; r < zone.rings.length; r++) {
                    var ring = zone.rings[r];
                    if (!ring.length) { continue; }
                    ctx.beginPath();
                    var minX = 1e9, maxX = -1e9, minY = 1e9, maxY = -1e9;
                    for (var k = 0; k < ring.length; k++) {
                        var p = project(ring[k][1], ring[k][0]);
                        if (k === 0) { ctx.moveTo(p[0], p[1]); } else { ctx.lineTo(p[0], p[1]); }
                        minX = Math.min(minX, p[0]); maxX = Math.max(maxX, p[0]);
                        minY = Math.min(minY, p[1]); maxY = Math.max(maxY, p[1]);
                    }
                    if (maxX < 0 || minX > w || maxY < 0 || minY > h) { continue; }
                    ctx.closePath();
                    ctx.globalAlpha = here ? 0.16 : 0.05;
                    ctx.fillStyle = colour;
                    ctx.fill();
                    ctx.globalAlpha = here ? 0.95 : 0.35;
                    ctx.strokeStyle = colour;
                    ctx.lineWidth = here ? 2 : 1;
                    if (ctx.setLineDash) { ctx.setLineDash(above ? [6, 5] : []); }
                    ctx.stroke();
                    if (maxX - minX > px * 7 && !labelAt) { labelAt = [(minX + maxX) / 2, (minY + maxY) / 2, maxX - minX]; }
                }
                if (ctx.setLineDash) { ctx.setLineDash([]); }
                ctx.globalAlpha = 1;
                if (a && here && zoneContains(zone, a.lon, a.lat)) { isInside = true; inside.push(zone); }
                if (labelAt && here) { labels.push({ x: labelAt[0], y: labelAt[1], zone: zone, colour: colour, wide: labelAt[2], inside: isInside }); }
            }

            // Range rings around the aircraft.
            if (a) {
                var centre = project(a.lat, a.lon);
                var step = mapRangeNm > 60 ? 50 : (mapRangeNm > 25 ? 20 : (mapRangeNm > 10 ? 10 : 5));
                ctx.strokeStyle = 'rgba(159, 179, 200, 0.25)';
                ctx.lineWidth = 1;
                ctx.fillStyle = 'rgba(159, 179, 200, 0.6)';
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
            if (mapRangeNm <= 60) {
                ctx.font = (px - 1) + 'px sans-serif';
                for (var q = 0; q < airports.length; q++) {
                    var ap = project(airports[q].lat, airports[q].lon);
                    if (ap[0] < 0 || ap[0] > w || ap[1] < 0 || ap[1] > h) { continue; }
                    ctx.fillStyle = '#c9d6e3';
                    ctx.beginPath();
                    ctx.arc(ap[0], ap[1], 3, 0, Math.PI * 2);
                    ctx.fill();
                    if (mapRangeNm <= 25) { ctx.fillText(airports[q].ident, ap[0] + 6, ap[1]); }
                }
            }

            // Route.
            var route = (mapState && mapState.route) || [];
            var nextIndex = nextWaypoint(route, a);
            if (route.length > 1) {
                ctx.beginPath();
                for (var s = 0; s < route.length; s++) {
                    var rp = project(route[s].lat, route[s].lon);
                    if (s === 0) { ctx.moveTo(rp[0], rp[1]); } else { ctx.lineTo(rp[0], rp[1]); }
                }
                ctx.strokeStyle = '#ff4fd8';
                ctx.lineWidth = 3;
                ctx.stroke();
            }
            ctx.font = 'bold ' + px + 'px sans-serif';
            for (var n = 0; n < route.length; n++) {
                var wp = project(route[n].lat, route[n].lon);
                ctx.fillStyle = n === nextIndex ? '#ffd166' : (route[n].icao ? '#4aa3ff' : '#ff4fd8');
                ctx.strokeStyle = '#0d1620';
                ctx.lineWidth = 2;
                ctx.beginPath();
                ctx.arc(wp[0], wp[1], n === nextIndex ? 7 : 5, 0, Math.PI * 2);
                ctx.fill();
                ctx.stroke();
                ctx.fillStyle = '#ffffff';
                ctx.strokeStyle = 'rgba(13, 22, 32, 0.9)';
                ctx.lineWidth = 3;
                ctx.strokeText(route[n].name, wp[0] + 9, wp[1] - 9);
                ctx.fillText(route[n].name, wp[0] + 9, wp[1] - 9);
            }

            // Zone labels, without overlapping.
            ctx.font = px + 'px sans-serif';
            var placed = [];
            for (var l = 0; l < labels.length; l++) {
                var lab = labels[l];
                var text = lab.zone.name + '  ' + formatLimit(lab.zone.lower) + '–' + formatLimit(lab.zone.upper);
                var width = ctx.measureText(text).width;
                var rect = { x: lab.x - width / 2, y: lab.y - px, w: width, h: px * 2 };
                var clash = false;
                for (var c = 0; c < placed.length; c++) {
                    if (rect.x < placed[c].x + placed[c].w && rect.x + rect.w > placed[c].x &&
                        rect.y < placed[c].y + placed[c].h && rect.y + rect.h > placed[c].y) { clash = true; break; }
                }
                if (clash || width > lab.wide * 1.6) { continue; }
                placed.push(rect);
                ctx.strokeStyle = 'rgba(13, 22, 32, 0.9)';
                ctx.lineWidth = 3;
                ctx.strokeText(text, rect.x, lab.y);
                ctx.fillStyle = lab.inside ? '#ffffff' : lab.colour;
                ctx.fillText(text, rect.x, lab.y);
            }

            // The track flown, then the aircraft.
            if (mapTrail.length > 1) {
                ctx.beginPath();
                for (var m = 0; m < mapTrail.length; m++) {
                    var tp = project(mapTrail[m][0], mapTrail[m][1]);
                    if (m === 0) { ctx.moveTo(tp[0], tp[1]); } else { ctx.lineTo(tp[0], tp[1]); }
                }
                ctx.strokeStyle = 'rgba(255, 255, 255, 0.55)';
                ctx.lineWidth = 2;
                ctx.stroke();
            }
            if (a) {
                var ap2 = project(a.lat, a.lon);
                ctx.save();
                ctx.translate(ap2[0], ap2[1]);
                ctx.rotate(toRad(a.headingTrue) - rot);
                ctx.fillStyle = live ? '#ffffff' : '#7f93a8';
                ctx.strokeStyle = '#0d1620';
                ctx.lineWidth = 2;
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
            ctx.fillStyle = '#9fb3c8';
            ctx.fillText(north, w - px * 4, px * 1.2);
            var barNm = mapRangeNm > 60 ? 20 : (mapRangeNm > 25 ? 10 : (mapRangeNm > 10 ? 5 : (mapRangeNm > 4 ? 2 : 1)));
            var barPx = barNm / 60 * ppd;
            ctx.strokeStyle = '#ffffff';
            ctx.lineWidth = 3;
            ctx.beginPath();
            ctx.moveTo(10, h - 14); ctx.lineTo(10 + barPx, h - 14);
            ctx.stroke();
            ctx.fillStyle = '#ffffff';
            ctx.fillText(barNm + ' NM', 10, h - 26);

            // Overlay texts.
            mapFollowButton.className = 'sa-btn sa-chartnav' + (mapFollow ? ' sa-on' : '');
            mapRotateButton.textContent = mapHeadingUp ? t.headingUp : t.northUp;
            renderMapInfo(t, a, live, inside, route, nextIndex);
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
                if (tabMap.textContent !== mapTitle) {
                    tabMap.textContent = mapTitle;
                    if (mapCanvas) { buildMap(); drawMap(); }
                }
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
        tabMap.textContent = 'Carte';
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
