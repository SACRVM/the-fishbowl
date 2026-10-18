/**
 * fb.fileViewer — a file of a workspace's Files in a window over the desktop:
 * a PDF in the browser's own viewer (a frame on the inline content URL —
 * same origin, unsandboxed, so Chrome's viewer runs with print and
 * download), pictures, media and text in the kit's <sac-quick-look>,
 * anything else as its info card with Download. A window, not a tab:
 * installed, a new tab is an app window in window-controls-overlay mode
 * (manifest.webmanifest), which the PDF viewer can't be moved by and whose
 * window buttons cover its toolbar. A browser without an inline PDF viewer
 * (navigator.pdfViewerEnabled false — a phone) gets the PDF in a tab.
 *
 *   fb.fileViewer.open({ files, path, entry? })
 *     files — fb.api.files.in(<workspace>); entry — its stat, when the
 *     caller has it. The same file again brings its window to the front.
 */
(function () {
    const fb = window.fb;
    const t = (key, fallback, vars) => fb.t(key, fallback, vars);
    const isPdf = (entry) => /pdf/i.test(entry?.mime || entry?.type || "") || /\.pdf$/i.test(entry?.name || "");
    const open = new Map();   // "<workspace>|<path>" → its window

    async function show({ files, path, entry }) {
        entry = entry || await files.stat(path);
        path = entry.path || path;
        const key = `${files.workspace}|${path}`;
        const existing = open.get(key);
        if (existing?.isConnected) { existing.bringToFront?.(); return existing; }

        const pdf = isPdf(entry);
        if (pdf && navigator.pdfViewerEnabled === false) {
            window.open(files.contentUrl(path, { inline: true }), "_blank", "noopener");
            return null;
        }

        const win = document.createElement("sac-window");
        win.className = "fb-file-viewer";
        win.classList.toggle("pdf", pdf);
        win.setAttribute("title", entry.name);
        win.setAttribute("escape-closes", "");
        win.setAttribute("controls", "max close");
        win.setAttribute("width", "min(960px, 94vw)");
        win.setAttribute("height", "calc(100vh - 76px)");
        win.setAttribute("top", "60px");
        win.setAttribute("left", "max(3vw, calc(50vw - 480px))");
        if (pdf) {
            const frame = document.createElement("iframe");
            frame.src = files.contentUrl(path, { inline: true });
            frame.title = entry.name;
            win.appendChild(frame);
        } else {
            const look = document.createElement("sac-quick-look");
            const dl = document.createElement("a");
            dl.className = "btn";
            dl.slot = "actions";
            dl.href = files.contentUrl(path);
            dl.download = entry.name;
            dl.innerHTML = `<sac-icon name="download"></sac-icon> ${t("fb.files.download", "Download")}`;
            look.appendChild(dl);
            look.addEventListener("sac:dismiss", () => win.close());
            win.appendChild(look);
            look.source = { url: files.contentUrl(path), type: entry.mime || entry.type || "", name: entry.name, size: entry.size };
        }
        win.addEventListener("sac:close", () => { win.remove(); open.delete(key); }, { once: true });
        document.body.appendChild(win);
        open.set(key, win);
        requestAnimationFrame(() => win.open());
        return win;
    }

    fb.fileViewer = { open: show, isPdf };
})();
