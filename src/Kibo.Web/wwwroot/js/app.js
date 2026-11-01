window.kibo = {
    toggleTheme: function () {
        var dark = document.documentElement.classList.toggle("dark");
        try { localStorage.setItem("kibo-theme", dark ? "dark" : "light"); } catch (e) { }
        return dark;
    },
    isDark: function () {
        return document.documentElement.classList.contains("dark");
    },
    copy: async function (text) {
        try { await navigator.clipboard.writeText(text); return true; } catch (e) { return false; }
    },
    download: function (fileName, text) {
        var blob = new Blob([text], { type: "text/plain" });
        var link = document.createElement("a");
        link.href = URL.createObjectURL(blob);
        link.download = fileName;
        link.click();
        URL.revokeObjectURL(link.href);
    }
};
