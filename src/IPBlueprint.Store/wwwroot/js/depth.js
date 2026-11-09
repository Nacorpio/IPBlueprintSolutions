// Small touches of depth for the glass look in site.css ("Depth layer").
// - Cards and glass panels follow a mouse pointer: this sets --px / --py (0 to 1)
//   and the is-pointer class on the element under it.
// - The hero's layer stack leans toward the pointer.
// - Buttons ripple from where they are pressed (mouse, pen or finger).
// - <html> gets is-scrolled once the page has scrolled, for the header shadow.
// Without JavaScript the site looks the same, only stiller.
(function () {
    "use strict";
    var root = document.documentElement;
    var finePointer = matchMedia("(hover: hover) and (pointer: fine)");
    var reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
    var tracked = ".product-card, .gallery-grid figure, .panel, .custom-cta, .stat, .delivery-option";

    // Header: one class flip, at most once per frame.
    var scrollQueued = false;
    function onScroll() {
        if (scrollQueued) return;
        scrollQueued = true;
        requestAnimationFrame(function () {
            scrollQueued = false;
            root.classList.toggle("is-scrolled", window.scrollY > 4);
        });
        leave();
    }
    addEventListener("scroll", onScroll, { passive: true });
    onScroll();

    // Ripple from the press point. The animation restarts on every press.
    document.addEventListener("pointerdown", function (e) {
        var button = e.target.closest && e.target.closest(".button");
        if (!button || button.disabled || reducedMotion.matches) return;
        var r = button.getBoundingClientRect();
        button.style.setProperty("--rx", (e.clientX - r.left) + "px");
        button.style.setProperty("--ry", (e.clientY - r.top) + "px");
        button.classList.remove("rippling");
        void button.offsetWidth;
        button.classList.add("rippling");
    }, { passive: true });
    document.addEventListener("animationend", function (e) {
        if (e.animationName === "ripple") e.target.classList.remove("rippling");
    });

    // Pointer light and tilt. The element's box is measured once when the pointer
    // enters it, so the tilt itself doesn't feed back into the measurement.
    var active = null, box = null, last = null, frame = 0;
    var stack = document.querySelector(".hero-stack");

    function leave() {
        if (active) active.classList.remove("is-pointer");
        active = null;
        box = null;
    }

    function update() {
        frame = 0;
        var e = last;
        if (stack && !reducedMotion.matches) {
            stack.style.setProperty("--gx", (e.clientX / innerWidth - .5).toFixed(3));
            stack.style.setProperty("--gy", (e.clientY / innerHeight - .5).toFixed(3));
        }
        var el = e.target.closest ? e.target.closest(tracked) : null;
        if (el !== active) {
            leave();
            if (!el) return;
            active = el;
            box = el.getBoundingClientRect();
            el.classList.add("is-pointer");
        }
        if (!el) return;
        var x = Math.min(1, Math.max(0, (e.clientX - box.left) / box.width));
        var y = Math.min(1, Math.max(0, (e.clientY - box.top) / box.height));
        el.style.setProperty("--px", x.toFixed(3));
        el.style.setProperty("--py", y.toFixed(3));
    }

    document.addEventListener("pointermove", function (e) {
        if (e.pointerType !== "mouse" || !finePointer.matches) return;
        last = e;
        if (!frame) frame = requestAnimationFrame(update);
    }, { passive: true });
    document.addEventListener("pointerout", function (e) {
        if (!e.relatedTarget) leave();
    }, { passive: true });
})();
