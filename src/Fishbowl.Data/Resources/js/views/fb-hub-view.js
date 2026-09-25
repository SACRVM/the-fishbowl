/**
 * <fb-hub-view>  (mounted at #/)
 *
 * Landing view: gradient title + subtitle + tile grid, built from the kit's
 * hub recipe (.hub-container / .grid / .tile in kit/css/ui.css) — on a
 * narrow phone the tiles turn into rows on their own.
 * Feature work adds tiles as features become real — never show a tile for
 * something that doesn't work.
 *
 * The tiles are the navigation; the shell's burger lists the same routes.
 */
class FbHubView extends HTMLElement {
    connectedCallback() {
        this.render();
        // Swap hrefs on context switch so clicking a tile keeps you in the
        // active workspace instead of dropping back to personal.
        this._onContext = () => this.render();
        window.addEventListener("fb:context-changed", this._onContext);
        this._loadVersion();
    }

    disconnectedCallback() {
        if (this._onContext) window.removeEventListener("fb:context-changed", this._onContext);
    }

    async _loadVersion() {
        try {
            const v = await fb.api.version();
            fb.version = v?.version ?? null;
        } catch {
            fb.version = null;
        }
        const footer = this.querySelector("sac-footer");
        if (footer && fb.version) footer.setAttribute("version", fb.version);
    }

    render() {
        const hrefFor = (personalPath) => fb.context.hashFor(personalPath);

        this.innerHTML = `
            <div class="orb"></div>
            <div class="hub-container">
                <header>
                    <h1>THE FISHBOWL</h1>
                    <p class="intro-text">Your memory lives here. You don't.</p>
                </header>
                <main class="grid">
                    <a class="tile" href="${hrefFor("#/notes")}">
                        <sac-icon name="note"></sac-icon>
                        <div>
                            <h2>Notes</h2>
                            <p>Write freely. Find anything.</p>
                        </div>
                    </a>
                    <a class="tile" href="${hrefFor("#/todos")}">
                        <sac-icon name="check"></sac-icon>
                        <div>
                            <h2>Todos</h2>
                            <p>Fast to-dos, always at hand.</p>
                        </div>
                    </a>
                    <a class="tile" href="${hrefFor("#/calendar")}">
                        <sac-icon name="calendar"></sac-icon>
                        <div>
                            <h2>Calendar</h2>
                            <p>Events and reminders, yours.</p>
                        </div>
                    </a>
                </main>
            </div>
            <sac-footer brand="THE FISHBOWL"></sac-footer>
        `;
        if (fb.version) this.querySelector("sac-footer").setAttribute("version", fb.version);
    }
}

customElements.define("fb-hub-view", FbHubView);
fb.router.register("#/", "fb-hub-view", { label: "Home", icon: "home" });
