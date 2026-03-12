window.watchlist = {
    clickFileInput(elementId) {
        var el = document.getElementById(elementId);
        if (el) el.click();
    },
    downloadAsFile(content, filename) {
        var blob = new Blob([content], { type: "text/plain;charset=utf-8" });
        var url = URL.createObjectURL(blob);
        var a = document.createElement("a");
        a.href = url;
        a.download = filename || "watchlist.txt";
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }
};
