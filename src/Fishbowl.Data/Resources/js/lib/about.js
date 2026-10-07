/*
 * fb.about — the About window of The Fishbowl, opened from the desktop's cover
 * tile (js/views/fb-hub-view.js).
 *
 *   fb.about.open()
 *
 * The kit's sac.about (kit ≥ 2.26), from data: the fish, name, version and
 * tagline on top; two tabs — Info (licence, rights holder, source) and Open
 * source (every bundled part as a compact "licence · holder" line, interface
 * and server as two groups). Re-opening brings the open window to the front.
 *
 * The licences were read from their sources: the project's own from LICENSE
 * and the README (AGPL-3.0, "Fishbowl contributors"); the vendored parts from
 * the headers and licence files of what is vendored (js/vendor/*, kit/js/vendor/*)
 * and the upstream repositories' LICENSE files; the NuGet packages from their
 * .nuspec licence expressions for the versions the csproj files reference
 * (the ONNX Runtime package carries a licence file: MIT); the fonts from the
 * upstream licence files (vendor.lock.json holds the file hashes); the
 * embedding model from its model card. Re-check a line when its part is
 * updated — the versions are deliberately not repeated here.
 */
(function () {
    const REPO = "https://github.com/SACRVM/the-fishbowl";
    // [name, licence, rights holder]
    const INTERFACE = [
        ["SACRVM APPKIT",       "MIT", "SACRVM APPKIT contributors"],
        ["sac-data-grid",       "MIT", "sac-data-grid contributors"],
        ["sac-md-editor",       "MIT", "sac-md-editor contributors"],
        ["marked",              "MIT", "Christopher Jeffrey"],
        ["DOMPurify",           "Apache-2.0 or MPL-2.0", "Cure53 and other contributors"],
        ["Inter",               "OFL-1.1", "The Inter Project Authors"],
        ["Outfit",              "OFL-1.1", "The Outfit Project Authors"],
        ["BIP-39 word list",    "MIT", "bitcoin/bips"],
    ];
    const SERVER = [
        ["Jint",                "BSD-2-Clause", "Sebastien Ros"],
        ["Dapper",              "Apache-2.0", "Stack Exchange, Inc."],
        ["Ical.Net",            "MIT", "ical-org"],
        ["Noda Time",           "Apache-2.0", "Jon Skeet"],
        ["Microsoft.Data.Sqlite", "MIT", "Microsoft"],
        ["sqlite-vec",          "MIT", "Alex Garcia"],
        ["ONNX Runtime",        "MIT", "Microsoft"],
        ["Microsoft.ML.Tokenizers", "MIT", "Microsoft"],
        ["all-MiniLM-L6-v2",    "Apache-2.0", "sentence-transformers"],
        ["Discord.Net",         "MIT", "Discord.Net Contributors"],
        ["Serilog",             "Apache-2.0", "Serilog Contributors"],
    ];

    function open() {
        const parts = (list, group) => list.map(([title, license, holder]) => ({ title, license, holder, group }));
        return sac.about.open({
            name: "The Fishbowl",
            icon: "fish",
            version: fb.version || undefined,
            description: fb.t("fb.desk.tagline", "Your memory lives here. You don't."),
            license: "AGPL-3.0",
            copyright: "Fishbowl contributors",
            source: REPO,
            notices: [
                ...parts(INTERFACE, fb.t("fb.about.interface", "Interface")),
                ...parts(SERVER, fb.t("fb.about.server", "Server")),
            ],
        });
    }

    fb.about = { open };
})();
