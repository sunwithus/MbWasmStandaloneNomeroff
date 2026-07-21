window.settingsGet = function (key) {
    try {
        var v = localStorage.getItem(key);
        console.debug("[settings] get", key, "=", v);
        return v;
    } catch (e) {
        console.warn("[settings] get error", key, e);
        return null;
    }
};
window.settingsSet = function (key, value) {
    try {
        localStorage.setItem(key, String(value));
        console.debug("[settings] set", key, "=", value);
        return true;
    } catch (e) {
        console.warn("[settings] set error", key, value, e);
        return false;
    }
};
window.settingsRemove = function (key) {
    try {
        localStorage.removeItem(key);
        console.debug("[settings] remove", key);
        return true;
    } catch (e) {
        console.warn("[settings] remove error", key, e);
        return false;
    }
};

window.settings = {
    get: window.settingsGet,
    set: function (key, value) { window.settingsSet(key, value); },
    remove: window.settingsRemove
};

window.downloadLogsFile = function (content, filename) {
    try {
        var blob = new Blob([content], { type: "text/plain;charset=utf-8" });
        var url = URL.createObjectURL(blob);
        var a = document.createElement("a");
        a.href = url;
        a.download = filename || "nomeroff-logs-" + new Date().toISOString().slice(0, 10) + ".txt";
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
        return true;
    } catch (e) {
        console.error("downloadLogsFile error", e);
        return false;
    }
};
