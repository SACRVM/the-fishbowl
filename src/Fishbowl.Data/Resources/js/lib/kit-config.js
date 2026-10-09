/**
 * Fishbowl — the app's settings for the vendored kit. Runs right after the
 * kit's own lib scripts, before any view registers or the shell mounts.
 *
 * Routing, workspace scope and icons are the kit's own (sac.router,
 * sac.scope, sac.icons) — Fishbowl adds no layer over them:
 *
 *   #/notes               → personal workspace  sac.scope.get() → { type: "root" }
 *   #/space/SLUG/notes    → space workspace     sac.scope.get() → { type: "scoped", slug }
 *
 * The kit's default hash prefix is "scope"; Fishbowl's URLs say "space".
 * Note the REST nesting is plural (/api/v1/spaces/SLUG/…), so fb.api builds
 * its paths itself instead of using sac.scope.endpoint().
 */
(function () {
    sac.scope.configure({ prefix: "space" });

    // An app, not a website: the browser's own context menu never shows
    // (text fields keep theirs — paste, spelling). Lists and tiles open
    // their own with sac.contextMenu — a right-click or a long press.
    sac.contextMenu.suppress();

    // The nav's brand mark; the kit ships no fish. Registered before any
    // component renders so the nav's first paint already finds it.
    if (!sac.icons.has("fish")) {
        sac.icons.register("fish",
            '<ellipse cx="10" cy="12" rx="7" ry="4"/><path d="M17 12l4-3v6z"/>' +
            '<circle cx="6" cy="11" r="1" fill="currentColor"/><path d="M7 14q1 0.6 2 0"/>');
    }
    // Notifications (the system inbox) ring a bell; the kit ships none.
    if (!sac.icons.has("fb-bell")) {
        sac.icons.register("fb-bell",
            '<path d="M10.268 21a2 2 0 0 0 3.464 0"/>' +
            '<path d="M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326"/>');
    }
    // A mail's direction (the Mail app): in comes down-left, out goes up-right.
    if (!sac.icons.has("fb-mail-in")) {
        sac.icons.register("fb-mail-in", '<path d="M17 7 7 17"/><path d="M17 17H7V7"/>');
    }
    if (!sac.icons.has("fb-mail-out")) {
        sac.icons.register("fb-mail-out", '<path d="M7 7h10v10"/><path d="M7 17 17 7"/>');
    }
    // A mail rule sorts what comes in: a funnel.
    if (!sac.icons.has("fb-rule")) {
        sac.icons.register("fb-rule", '<path d="M3 4h18l-7 8.5V19l-4 2v-8.5z"/>');
    }
    // Writing mail: reply, reply all, forward, send — the kit ships none.
    const mailIcons = {
        "fb-reply": '<path d="M9 17 4 12l5-5"/><path d="M20 18v-2a4 4 0 0 0-4-4H4"/>',
        "fb-reply-all": '<path d="m7 17-5-5 5-5"/><path d="m12 17-5-5 5-5"/><path d="M22 18v-2a4 4 0 0 0-4-4H7"/>',
        "fb-forward": '<path d="m15 17 5-5-5-5"/><path d="M4 18v-2a4 4 0 0 1 4-4h12"/>',
        "fb-send": '<path d="M14.536 21.686a.5.5 0 0 0 .937-.024l6.5-19a.496.496 0 0 0-.635-.635l-19 6.5a.5.5 0 0 0-.024.937l7.93 3.18a2 2 0 0 1 1.112 1.11z"/><path d="m21.854 2.147-10.94 10.939"/>',
    };
    // Spam (the Mail app's Spam view): a shield with a warning.
    mailIcons["fb-spam"] = '<path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/><path d="M12 8v4"/><path d="M12 16h.01"/>';
    for (const [name, path] of Object.entries(mailIcons)) if (!sac.icons.has(name)) sac.icons.register(name, path);
    // Back to the inbox (an archived conversation); the kit ships no inbox tray.
    if (!sac.icons.has("fb-inbox")) {
        sac.icons.register("fb-inbox",
            '<path d="M22 12h-6l-2 3h-4l-2-3H2"/>' +
            '<path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/>');
    }
    // An envelope is mail; the kit ships no mail icon yet.
    if (!sac.icons.has("mail")) {
        sac.icons.register("mail",
            '<rect x="2" y="4" width="20" height="16" rx="2"/><path d="m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7"/>');
    }
    // Birthdays in the calendar (from contacts); the kit ships no gift.
    if (!sac.icons.has("fb-gift")) {
        sac.icons.register("fb-gift",
            '<rect x="3" y="8" width="18" height="4" rx="1"/><path d="M12 8v13"/><path d="M19 12v7a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2v-7"/>' +
            '<path d="M7.5 8a2.5 2.5 0 0 1 0-5C9.5 3 11 5.5 12 8c1-2.5 2.5-5 4.5-5a2.5 2.5 0 0 1 0 5"/>');
    }
    // A series in the calendar (the repeat arrows calendars use); the kit's
    // "sync" is a refresh, not a repeat.
    if (!sac.icons.has("fb-repeat")) {
        sac.icons.register("fb-repeat",
            '<path d="m17 2 4 4-4 4"/><path d="M3 11v-1a4 4 0 0 1 4-4h14"/><path d="m7 22-4-4 4-4"/><path d="M21 13v1a4 4 0 0 1-4 4H3"/>');
    }
    // A phone number (the calendar's birthday card); the kit ships no phone.
    if (!sac.icons.has("fb-phone")) {
        sac.icons.register("fb-phone",
            '<path d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 ' +
            '12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"/>');
    }
    // The account menu's theme items (dark / light / like the system); the
    // kit's own switch draws these inline and registers none.
    sac.icons.register("fb-theme-dark", '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>');
    sac.icons.register("fb-theme-light",
        '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41' +
        'M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41"/>');
    sac.icons.register("fb-theme-auto",
        '<circle cx="12" cy="12" r="9"/><path d="M12 3a9 9 0 0 1 0 18z" fill="currentColor"/>');
})();
