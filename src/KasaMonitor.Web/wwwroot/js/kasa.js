// KasaMonitor client helpers: report browser-side errors to the server log and a clipboard helper that works on http://.
(function () {
    'use strict';
    var sent = 0;
    var MAX_REPORTS = 20;
    var recent = [];

    // Keep the last console errors: Blazor logs the reason there before it shows #blazor-error-ui.
    var originalError = console.error;
    console.error = function () {
        try {
            var text = Array.prototype.map.call(arguments, function (a) {
                return a && a.stack ? a.stack : String(a);
            }).join(' ');
            recent.push(text.slice(0, 800));
            if (recent.length > 5) recent.shift();
        } catch (e) { /* never break logging */ }
        return originalError.apply(console, arguments);
    };

    function report(kind, message, stack) {
        if (sent >= MAX_REPORTS) return;
        sent++;
        try {
            fetch('api/client-error', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    kind: kind,
                    message: String(message || '').slice(0, 4000),
                    stack: String(stack || '').slice(0, 4000),
                    url: location.href,
                    recent: recent.slice()
                })
            }).catch(function () { });
        } catch (e) { /* offline or blocked: ignore */ }
    }

    window.addEventListener('error', function (e) {
        var target = e.target;
        if (target && target !== window && (target.src || target.href)) {
            report('resource', 'failed to load ' + (target.src || target.href), '');
            return;
        }
        report('error', e.message, e.error && e.error.stack);
    }, true);

    window.addEventListener('unhandledrejection', function (e) {
        var r = e.reason;
        report('unhandledrejection', r && r.message ? r.message : String(r), r && r.stack);
    });

    // Blazor shows #blazor-error-ui (style.display = block) on circuit/JS errors: report it and show the reason.
    function watchBanner() {
        var el = document.getElementById('blazor-error-ui');
        if (!el || el.dataset.watched) return;
        el.dataset.watched = '1';
        new MutationObserver(function () {
            if (el.style.display !== 'block' || el.dataset.reported) return;
            el.dataset.reported = '1';
            var msg = recent.length ? recent[recent.length - 1] : 'no console error was captured';
            var text = el.querySelector('.error-text');
            if (text) text.textContent = msg;
            report('blazor-error-ui', msg, recent.join('\n---\n'));
        }).observe(el, { attributes: true, attributeFilter: ['style'] });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', watchBanner);
    else watchBanner();

    // Theme (light / dark): data-theme + data-bs-theme on <html>, the explicit choice saved in localStorage; without a
    // saved choice the system preference is followed live. Re-applied after Blazor's enhanced navigation.
    var THEME_KEY = 'kasa-theme';
    var boot = window.kasaThemeBoot;   // defined by the inline script in App.razor (single owner of the rule)
    function savedTheme() { return boot ? boot.saved() : null; }
    function systemTheme() { return boot ? boot.system() : 'light'; }
    // The toggle button says which theme is on (aria-pressed = dark).
    function syncToggle(t) {
        var buttons = document.querySelectorAll('[data-theme-toggle]');
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].setAttribute('aria-pressed', t === 'dark' ? 'true' : 'false');
            buttons[i].setAttribute('title', t === 'dark' ? 'Temă întunecată (apasă pentru luminoasă)' : 'Temă luminoasă (apasă pentru întunecată)');
        }
    }
    function applyTheme(t) {
        if (boot) boot.apply(t);
        else { document.documentElement.setAttribute('data-theme', t); document.documentElement.setAttribute('data-bs-theme', t); }
        syncToggle(t);
    }
    window.kasaTheme = {
        current: function () { return document.documentElement.getAttribute('data-theme') || savedTheme() || systemTheme(); },
        toggle: function () {
            var next = window.kasaTheme.current() === 'dark' ? 'light' : 'dark';
            applyTheme(next);
            try { localStorage.setItem(THEME_KEY, next); } catch (e) { /* private mode: only this page */ }
            return next;
        },
        apply: function () { applyTheme(savedTheme() || systemTheme()); }
    };
    try {
        if (window.matchMedia) {
            matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () { if (!savedTheme()) window.kasaTheme.apply(); });
        }
    } catch (e) { /* old browsers */ }
    document.addEventListener('click', function (e) {
        var b = e.target && e.target.closest ? e.target.closest('[data-theme-toggle]') : null;
        if (b) { e.preventDefault(); window.kasaTheme.toggle(); }
    });
    function hookEnhancedLoad() {
        if (window.Blazor && window.Blazor.addEventListener) {
            try { window.Blazor.addEventListener('enhancedload', window.kasaTheme.apply); } catch (e) { /* not a Web App */ }
            return true;
        }
        return false;
    }
    if (!hookEnhancedLoad()) document.addEventListener('DOMContentLoaded', hookEnhancedLoad);
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', function () { syncToggle(window.kasaTheme.current()); });
    else syncToggle(window.kasaTheme.current());

    // navigator.clipboard exists only on https:// or localhost; fall back to execCommand and never throw.
    window.kasaCopy = function (text) {
        try {
            if (navigator.clipboard && window.isSecureContext) {
                navigator.clipboard.writeText(text).catch(function () { });
                return true;
            }
        } catch (e) { /* fall through */ }
        try {
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.setAttribute('readonly', '');
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            var ok = document.execCommand('copy');
            document.body.removeChild(ta);
            return !!ok;
        } catch (e) {
            return false;
        }
    };
})();
