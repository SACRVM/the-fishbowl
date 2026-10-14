// A link the Fishbowl app opens in a new tab or window (target=_blank,
// window.open) to another site goes to the system's browser: this closes the
// tab Chromium made and hands the address to the native host (open-link,
// xdg-open). The app's own pages that open that way — a file preview, an
// export — stay here: only this profile has the session. Links within the
// window (the Google sign-in) are left alone.
const HOST = "fishbowl.open_link";
const HANDED_OFF = new Set(["http:", "https:", "mailto:"]);

chrome.webNavigation.onCreatedNavigationTarget.addListener(async (target) => {
    let url;
    try {
        url = new URL(target.url);
    } catch {
        return;
    }
    if (!HANDED_OFF.has(url.protocol)) return;

    const source = await chrome.tabs.get(target.sourceTabId).catch(() => null);
    try {
        if (source?.url && new URL(source.url).origin === url.origin) return;
    } catch {
        // A source without a parsable address is not the app's page.
    }

    chrome.runtime.sendNativeMessage(HOST, { url: url.href }, (reply) => {
        // Not handed off (no host, or it refused): the tab stays open.
        if (chrome.runtime.lastError || !reply?.ok) return;
        chrome.tabs.remove(target.tabId).catch(() => {});
    });
});
