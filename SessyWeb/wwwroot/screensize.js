export function getScreenHeight() {
    return Math.max(
        document.documentElement.clientHeight, // Meest betrouwbare methode
        window.innerHeight || 0
    );
}

export function initialize(dotNetHelper) {
    window.addEventListener("resize", () => {
        dotNetHelper.invokeMethodAsync("UpdateScreenHeight", getScreenHeight());
    });
}
