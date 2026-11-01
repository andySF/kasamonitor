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
