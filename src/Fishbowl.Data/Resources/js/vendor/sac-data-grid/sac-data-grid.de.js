/**
 * sac-data-grid — its UI strings in German.
 *
 * English lives inline in the grid as the fallback; this table only adds
 * German, the same way the kit ships kit/js/i18n/de.js. Load it after the
 * kit's globals.js (it waits for sac:ready when the kit is injected by
 * all.js and arrives later).
 */
(function () {
    const table = {
        "data-grid.label": "Datentabelle",
        "data-grid.yes": "Ja",
        "data-grid.no": "Nein",
        "data-grid.any": "Alle",
        "data-grid.select-all": "Alles auswählen",
        "data-grid.row-number": "Zeile",
        "data-grid.filtered": "Gefiltert",
        "data-grid.loading": "Wird geladen …",
        "data-grid.retry": "Erneut versuchen",
        "data-grid.load-failed": "Zeilen konnten nicht geladen werden.",
        "data-grid.empty": "Keine Zeilen",

        "data-grid.agg-sum": "Summe",
        "data-grid.agg-avg": "Mittel",
        "data-grid.agg-count": "Anzahl",
        "data-grid.agg-min": "Min",
        "data-grid.agg-max": "Max",

        "data-grid.rows-one": "1 Zeile",
        "data-grid.rows": "{n} Zeilen",
        "data-grid.rows-more": "{n}+ Zeilen",
        "data-grid.count": "Anzahl: {n}",
        "data-grid.sum": "Summe: {n}",
        "data-grid.avg": "Mittelwert: {n}",
        "data-grid.partial": "(nur geladene Zeilen)",

        "data-grid.pages": "Seiten",
        "data-grid.page": "Seite {n}",
        "data-grid.page-of": "Seite {n} von {m}",
        "data-grid.first-page": "Erste Seite",
        "data-grid.prev-page": "Vorige Seite",
        "data-grid.next-page": "Nächste Seite",
        "data-grid.last-page": "Letzte Seite",

        "data-grid.sort-asc": "Aufsteigend sortieren",
        "data-grid.sort-desc": "Absteigend sortieren",
        "data-grid.sort-clear": "Sortierung aufheben",
        "data-grid.filter": "Filtern …",
        "data-grid.filter-title": "{label} filtern",
        "data-grid.filter-clear": "Filter entfernen",
        "data-grid.contains": "Enthält …",
        "data-grid.from": "Von",
        "data-grid.to": "Bis",
        "data-grid.done": "Fertig",
        "data-grid.select-column": "Spalte auswählen",
        "data-grid.fit-width": "Breite anpassen",
        "data-grid.hide-column": "Spalte ausblenden",
        "data-grid.show-column": "{label} einblenden",
        "data-grid.show-all": "Alle Spalten einblenden",

        "data-grid.new-row": "Neue Zeile",
        "data-grid.insert-row": "Neue Zeile",
        "data-grid.delete-row": "Zeile löschen",
        "data-grid.delete-rows": "{n} Zeilen löschen",
        "data-grid.deleted-one": "1 Zeile gelöscht",
        "data-grid.deleted": "{n} Zeilen gelöscht",
        "data-grid.undo": "Rückgängig",
        "data-grid.open-record": "Datensatz öffnen",
        "data-grid.required": "Pflichtfeld",
        "data-grid.invalid": "Kein gültiger Wert",
        "data-grid.fix-errors": "Bitte die markierten Zellen korrigieren: {n} Zeilen wurden nicht gespeichert.",
        "data-grid.save-error": "Speichern fehlgeschlagen",
        "data-grid.save-failed": "{n} Zeilen konnten nicht gespeichert werden.",
        "data-grid.saving": "Wird gespeichert …",
        "data-grid.errors-one": "1 Zeile mit Fehlern",
        "data-grid.errors": "{n} Zeilen mit Fehlern",
        "data-grid.unsaved-one": "1 ungespeicherte Zeile",
        "data-grid.unsaved": "{n} ungespeicherte Zeilen",
        "data-grid.revert": "Verwerfen",
        "data-grid.save": "Speichern",

        "data-grid.paste-invalid": "Kein gültiger Wert: {text}",
        "data-grid.paste-rejected": "{n} Zellen wurden nicht eingefügt.",

        "data-grid.record": "Datensatz {n}",
        "data-grid.record-of": "Datensatz {n} von {m}",
        "data-grid.new-record": "Neuer Datensatz",
        "data-grid.previous": "Vorheriger",
        "data-grid.next": "Nächster",
        "data-grid.new": "Neu",
        "data-grid.close": "Schließen",
        "data-grid.cancel": "Abbrechen",
        "data-grid.form-errors": "Bitte die markierten Felder korrigieren.",
        "data-grid.discard-title": "Änderungen verwerfen?",
        "data-grid.discard-message": "Dieser Datensatz hat Änderungen, die noch nicht gespeichert sind.",
        "data-grid.discard": "Verwerfen",
        "data-grid.keep-editing": "Weiter bearbeiten",
        "data-grid.mode": "Bearbeitungsmodus",
        "data-grid.mode-read": "Lesen",
        "data-grid.mode-sheet": "Tabelle",
        "data-grid.mode-form": "Formular",

        "data-grid.copy": "Kopieren",
        "data-grid.copy-wait": "Einige Zeilen werden noch geladen – bitte gleich noch einmal versuchen.",
        "data-grid.copy-failed": "Kopieren ist fehlgeschlagen.",
    };

    const add = () => {
        if (window.sac && sac.i18n && typeof sac.i18n.add === "function") { sac.i18n.add("de", table); return true; }
        return false;
    };
    if (!add()) document.addEventListener("sac:ready", add, { once: true });
})();
