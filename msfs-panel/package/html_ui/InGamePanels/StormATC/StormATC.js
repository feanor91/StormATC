/*
 * The MSFS toolbar window around the StormATC panel. All the drawing is in panel-core.js,
 * which is shared with the standalone page; this only hooks it to the simulator's panel frame.
 */
class StormAtcPanelElement extends TemplateElement {
    constructor() {
        super();
        this.panel = null;
        this.routeListener = null;
        this.routeTimer = null;
        this.routesSent = {};
        this.firstRoutePoll = true;
    }

    /**
     * Reads the flight plan the pilot makes in the EFB and posts it to StormATC.
     *
     * The simulator hands the route to JavaScript through the Planned Route listener: the EFB's
     * route (EfbRouteUpdated), the route synced to the avionics (AvionicsRouteSync) and the route
     * filed with ATC (GET_ATC_ROUTE), which is what "send to ATC" produces. StormATC applies the
     * last two when they change. The panel has to be open (it may be minimised) for this to run.
     */
    startFlightPlanBridge() {
        var self = this;
        if (typeof RegisterViewListener !== "function" || this.routeListener) {
            return;
        }

        try {
            this.routeListener = RegisterViewListener("JS_LISTENER_PLANNEDROUTE", function () {
                try {
                    self.routeListener.on("EfbRouteUpdated", function (route) { self.offerRoute("efb", route, false); });
                    self.routeListener.on("AvionicsRouteSync", function (route) { self.offerRoute("avionics", route, false); });
                } catch (e) {
                    console.log("[StormATC] cannot listen for route events: " + e);
                }

                self.pollRoutes();
                // The filed ATC route has no event of its own: look at it every few seconds.
                self.routeTimer = window.setInterval(function () { self.pollRoutes(); }, 3000);
            });
        } catch (e) {
            console.log("[StormATC] the flight plan listener is not available: " + e);
        }
    }

    pollRoutes() {
        var self = this;
        var initial = this.firstRoutePoll;
        this.firstRoutePoll = false;

        [["atc", "GET_ATC_ROUTE"], ["efb", "GET_EFB_ROUTE"]].forEach(function (pair) {
            try {
                var result = self.routeListener.call(pair[1]);
                if (result && typeof result.then === "function") {
                    result.then(function (route) { self.offerRoute(pair[0], route, initial); });
                }
            } catch (e) {
                console.log("[StormATC] cannot read the " + pair[0] + " route: " + e);
            }
        });
    }

    offerRoute(source, route, initial) {
        var departure = route && route.departureAirport;
        if (!departure || !(departure.ident || departure.airport)) {
            return;
        }

        var key = JSON.stringify(route);
        if (this.routesSent[source] === key) {
            return;
        }

        var self = this;
        // Remembered only once StormATC has it, so a StormATC that was not running yet gets it later.
        StormAtcPanel.postJson("/api/flightplan", { source: source, initial: initial, route: route }, function (ok) {
            if (ok) {
                self.routesSent[source] = key;
            }
        });
    }

    connectedCallback() {
        if (typeof super.connectedCallback === "function") {
            super.connectedCallback();
        }

        var root = this.querySelector("#StormAtcRoot");
        var frame = this.querySelector("#StormAtcFrame");
        if (root && !this.panel) {
            // The window's title is the registration of the aircraft being flown.
            this.startFlightPlanBridge();
            this.panel = StormAtcPanel.mount(root, {
                onState: function (state) {
                    var title = state.registration || state.callsign || "StormATC";
                    if (frame && frame.getAttribute("title") !== title) {
                        frame.setAttribute("title", title);
                    }
                }
            });
        }
    }

    disconnectedCallback() {
        if (typeof super.disconnectedCallback === "function") {
            super.disconnectedCallback();
        }

        if (this.routeTimer) {
            window.clearInterval(this.routeTimer);
            this.routeTimer = null;
        }

        if (this.panel) {
            this.panel.stop();
            this.panel = null;
        }
    }
}

window.customElements.define("ingamepanel-stormatc", StormAtcPanelElement);
checkAutoload();
