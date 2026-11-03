(function () {
    var running = false;
    async function poll() {
        if (running || document.hidden) return;
        running = true;
        try {
            var badge = document.getElementById('callState');
            var url = badge ? '/Voice/Status?id=' + badge.dataset.id : '/Voice/Status';
            var r = await fetch(url, { credentials: 'same-origin' });
            if (!r.ok) return;
            var rows = await r.json();
            var wasLive = badge && badge.dataset.live === 'true';
            rows.forEach(function (c) {
                if (badge) {
                    badge.textContent = c.state;
                    if (wasLive && !c.active) location.reload();
                    return;
                }
                var tr = document.querySelector('#voiceTable tr[data-id="' + c.id + '"]');
                if (!tr) return;
                var s = tr.querySelector('[data-role=state]'); if (s) s.textContent = c.state;
                var d = tr.querySelector('[data-role=dur]'); if (d && c.durationSeconds != null) d.textContent = c.durationSeconds + ' s';
            });
        } catch (e) { /* tenta no próximo ciclo */ } finally { running = false; }
    }
    setInterval(poll, 2000);
})();
