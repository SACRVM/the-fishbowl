/**
 * Fishbowl — dates and times in the user's chosen format (fb.format).
 *
 * Browsers never tell a page the OS regional format; `toLocale*()` and the
 * native date/time inputs follow the browser's UI *language* (English Chrome
 * → US dates, 12-hour clock). So the format is a user setting (profile →
 * "Date & time format", stored as users.date_format, mirrored in
 * Fishbowl.Core.Util.DateFormats), and every date on screen goes through
 * here — never through toLocale*(undefined).
 *
 *   iso  2026-09-25 20:00   (default)
 *   de   25.09.2026 20:00
 *   uk   25/09/2026 20:00
 *   us   09/25/2026 8:00 PM
 *
 * Month and weekday *names* follow the UI language (English), not the format.
 *
 * Inputs are the kit's <sac-date-field> + <sac-time-field>. The chosen
 * format drives them page-wide through sac.regional (fb.format.set maps it:
 * de → "dmy." h23, uk → "dmy/" h23, us → "mdy/" h12, iso → "iso" h23).
 * fb.format.attachInput(input, { time }) swaps a placeholder <input> for the
 * pair (the date field takes over its id); read it with
 * fb.format.readInput(field) → Date or null, write it with
 * fb.format.writeInput(field, date).
 */
(function () {
    const PATTERNS = {
        iso: { order: "ymd", sep: "-", h12: false, kit: "iso" },
        de:  { order: "dmy", sep: ".", h12: false, kit: "dmy." },
        uk:  { order: "dmy", sep: "/", h12: false, kit: "dmy/" },
        us:  { order: "mdy", sep: "/", h12: true,  kit: "mdy/" },
    };
    const NAMES = "en-GB";   // month/weekday names: the UI's language
    const KEY = "fb.dateFormat";

    let name = "iso";
    try { const v = localStorage.getItem(KEY); if (PATTERNS[v]) name = v; } catch { /* storage blocked */ }
    const p = () => PATTERNS[name];
    const pad = (n) => String(n).padStart(2, "0");
    const toDate = (d) => (d instanceof Date ? d : new Date(d));

    function date(d) {
        d = toDate(d);
        const y = d.getFullYear(), m = pad(d.getMonth() + 1), day = pad(d.getDate());
        const { order, sep } = p();
        return order === "ymd" ? [y, m, day].join(sep)
             : order === "dmy" ? [day, m, y].join(sep)
             : [m, day, y].join(sep);
    }

    function time(d) {
        d = toDate(d);
        if (!p().h12) return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
        const h = d.getHours() % 12 || 12;
        return `${h}:${pad(d.getMinutes())} ${d.getHours() < 12 ? "AM" : "PM"}`;
    }

    // Day and month without the year: 09-25 · 25.09. · 25/09 · 09/25
    function dayMonth(d) {
        d = toDate(d);
        const m = pad(d.getMonth() + 1), day = pad(d.getDate());
        const { order, sep } = p();
        return order === "ymd" ? `${m}${sep}${day}`
             : order === "dmy" ? `${day}${sep}${m}${sep === "." ? "." : ""}`
             : `${m}${sep}${day}`;
    }

    const api = {
        NAMES: Object.keys(PATTERNS),
        get name() { return name; },

        /** Switch the format (a DateFormats name; anything else → iso). */
        set(next) {
            name = PATTERNS[next] ? next : "iso";
            try {
                if (name === "iso") localStorage.removeItem(KEY);
                else localStorage.setItem(KEY, name);
            } catch { /* storage blocked */ }
            syncKit();
        },

        date,
        time,
        dayMonth,
        dateTime: (d) => `${date(d)} ${time(d)}`,
        weekday:  (d) => toDate(d).toLocaleDateString(NAMES, { weekday: "short" }),
        monthYear: (d) => toDate(d).toLocaleDateString(NAMES, { month: "long", year: "numeric" }),

        /** An example of `formatName` for a settings list ("25.09.2026 20:00"). */
        example(formatName, d = new Date()) {
            const keep = name;
            name = PATTERNS[formatName] ? formatName : "iso";
            try { return `${date(d)} ${time(d)}`; } finally { name = keep; }
        },

        attachInput,
        readInput,
        writeInput,
        setInputTime,
    };

    // --- Inputs ---------------------------------------------------------------
    // The kit's fields, driven page-wide by sac.regional (they follow a
    // change live). The views hold the date field: it carries the id of the
    // <input> it replaced, and a commit in either field fires `change` on it.

    function syncKit() {
        window.sac?.regional?.set({ date: p().kit, hourCycle: p().h12 ? "h12" : "h23" });
    }

    function attachInput(input, { time: withTime = false } = {}) {
        const dateEl = document.createElement("sac-date-field");
        if (input.id) dateEl.id = input.id;
        dateEl.className = "fb-date-field";
        // Full-size, like the plain kit inputs around them (compact is for
        // sidebars); set on both so the pair stays one family.
        dateEl.setAttribute("size", "regular");
        const timeEl = document.createElement("sac-time-field");
        timeEl.className = "fb-time-field";
        timeEl.setAttribute("size", "regular");
        timeEl.setAttribute("step", "5");
        timeEl.hidden = !withTime;
        dateEl._fbTime = timeEl;

        const wrap = document.createElement("span");
        wrap.className = "fb-date-wrap";
        wrap.append(dateEl, timeEl);
        input.replaceWith(wrap);

        const changed = () => dateEl.dispatchEvent(new Event("change", { bubbles: true }));
        dateEl.addEventListener("sac:change", changed);
        timeEl.addEventListener("sac:change", changed);
        return dateEl;
    }

    /** Date (+ time) from the fields: a Date, or null without a date. No
     *  time on a timed field saves as 09:00. The kit fields reject invalid
     *  entries themselves, so there is no invalid state to report. */
    function readInput(field) {
        const iso = field.value;
        if (!iso) return null;
        const [y, m, d] = iso.split("-").map(Number);
        const timeEl = field._fbTime;
        if (!timeEl || timeEl.hidden) return new Date(y, m - 1, d);
        const [h, min] = timeEl.value ? timeEl.value.split(":").map(Number) : [9, 0];
        return new Date(y, m - 1, d, h, min);
    }

    function writeInput(field, d) {
        const timeEl = field._fbTime;
        if (!d) { field.value = ""; if (timeEl) timeEl.value = ""; return; }
        d = toDate(d);
        field.value = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
        if (timeEl) timeEl.value = timeEl.hidden ? "" : `${pad(d.getHours())}:${pad(d.getMinutes())}`;
    }

    /** Show or hide the time field (all-day events), keeping the day. */
    function setInputTime(field, withTime) {
        const timeEl = field._fbTime;
        if (!timeEl) return;
        const cur = readInput(field);
        timeEl.hidden = !withTime;
        if (cur) writeInput(field, withTime && cur.getHours() === 0 && cur.getMinutes() === 0
            ? new Date(cur.getFullYear(), cur.getMonth(), cur.getDate(), 9, 0) : cur);
    }

    fb.format = api;
    syncKit();
})();
