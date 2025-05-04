export function getScreenHeight() {
    return Math.max(document.documentElement.clientHeight, window.innerHeight || 0);
}

export function getScreenWidth() {
    return Math.max(document.documentElement.clientWidth, window.innerWidth || 0);
}

export function initialize(dotNetHelper) {
    window.addEventListener("resize", () => {
        dotNetHelper.invokeMethodAsync("UpdateScreenSize", getScreenHeight(), getScreenWidth());
    });
}
