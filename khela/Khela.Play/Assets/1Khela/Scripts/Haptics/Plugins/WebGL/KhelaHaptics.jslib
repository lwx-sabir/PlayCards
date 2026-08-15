// Khela haptics — WebGL backend over navigator.vibrate. Namespaced (_KhelaHaptic*) to avoid symbol clashes.
// The Web Vibration API has no intensity/sharpness — only durations — so only timings survive.
var KhelaHapticPlugin = {
    $KhelaHapticState: { patterns: {} },

    _KhelaHapticInit: function () {
        KhelaHapticState.patterns = {};
    },

    _KhelaHapticPlayMs: function (durationMs) {
        if (navigator.vibrate) {
            navigator.vibrate(durationMs);
        }
    },

    _KhelaHapticRegisterPattern: function (idPtr, ptr, length) {
        var id = UTF8ToString(idPtr);
        var pattern = [];
        for (var i = 0; i < length; i++) {
            pattern.push(HEAP32[(ptr >> 2) + i]);
        }
        KhelaHapticState.patterns[id] = pattern;
    },

    _KhelaHapticPlayPattern: function (idPtr) {
        var id = UTF8ToString(idPtr);
        var pattern = KhelaHapticState.patterns[id];
        if (pattern) {
            if (navigator.vibrate) {
                navigator.vibrate(pattern);
            }
        } else {
            console.error("[KhelaHaptic] pattern with ID '" + id + "' not found.");
        }
    }
};

autoAddDeps(KhelaHapticPlugin, '$KhelaHapticState');
mergeInto(LibraryManager.library, KhelaHapticPlugin);
