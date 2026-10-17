/*
 * The MSFS toolbar window around the StormATC panel. All the drawing is in panel-core.js,
 * which is shared with the standalone page; this only hooks it to the simulator's panel frame.
 */
class StormAtcPanelElement extends TemplateElement {
    constructor() {
        super();
        this.panel = null;
    }

    connectedCallback() {
        if (typeof super.connectedCallback === "function") {
            super.connectedCallback();
        }

        var root = this.querySelector("#StormAtcRoot");
        var frame = this.querySelector("#StormAtcFrame");
        if (root && !this.panel) {
            // The window's title is the registration of the aircraft being flown.
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

        if (this.panel) {
            this.panel.stop();
            this.panel = null;
        }
    }
}

window.customElements.define("ingamepanel-stormatc", StormAtcPanelElement);
checkAutoload();
