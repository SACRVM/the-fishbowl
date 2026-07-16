/**
 * <fb-hub-view>  (mounted at #/)
 *
 * Landing view. Gradient title + subtitle + 2-tile grid.
 * Tile content is static here for v1 (Notes, Todos). Feature work adds tiles
 * as features become real — never show a tile for something that doesn't work.
 *
 * No <fb-nav> slide-out from the hub — the tiles are the navigation.
 */
class FbHubView extends HTMLElement {
    connectedCallback() {
        this.render();
        // Swap hrefs on context switch so clicking a tile keeps you in the
        // active workspace instead of dropping back to personal.
        this._onContext = () => this.render();
        window.addEventListener("fb:context-changed", this._onContext);
    }

    disconnectedCallback() {
        if (this._onContext) window.removeEventListener("fb:context-changed", this._onContext);
    }

    render() {
        const hrefFor = (personalPath) =>
            window.fb?.context?.hashFor ? fb.context.hashFor(personalPath) : personalPath;

        this.innerHTML = `
            <div class="orb"></div>
            <div class="hub-container">
                <header>
                    <h1>THE FISHBOWL</h1>
                    <p class="intro-text">Your memory lives here. You don't.</p>
                </header>
                <main class="grid">
                    <a class="tile" href="${hrefFor("#/notes")}">
                        <fb-icon name="note"></fb-icon>
                        <div>
                            <h2>Notes</h2>
                            <p>Write freely. Find anything.</p>
                        </div>
                    </a>
                    <a class="tile" href="${hrefFor("#/todos")}">
                        <fb-icon name="check"></fb-icon>
                        <div>
                            <h2>Todos</h2>
                            <p>Fast to-dos, always at hand.</p>
                        </div>
                    </a>
                    <a class="tile" href="${hrefFor("#/calendar")}">
                        <fb-icon name="calendar"></fb-icon>
                        <div>
                            <h2>Calendar</h2>
                            <p>Events and reminders, yours.</p>
                        </div>
                    </a>
                </main>
            </div>
            <fb-footer></fb-footer>
        `;
    }
}

customElements.define("fb-hub-view", FbHubView);
fb.router.register("#/", "fb-hub-view", { label: "Home", icon: "home" });
