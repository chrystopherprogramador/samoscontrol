(function () {
  var badge = document.getElementById('waNavBadge');
  var baseTitle = document.title, last = null, ctx = null;
  var onChat = !!document.getElementById('wa');
  window.waNotify = {
    soundOn: function () { return localStorage.getItem('wa.sound') !== 'off'; },
    setSound: function (v) { localStorage.setItem('wa.sound', v ? 'on' : 'off'); },
    beep: beep, askPermission: askPermission
  };

  function beep() {
    if (localStorage.getItem('wa.sound') === 'off') return;
    try {
      ctx = ctx || new (window.AudioContext || window.webkitAudioContext)();
      if (ctx.state === 'suspended') ctx.resume();
      var t = ctx.currentTime;
      [880, 1175].forEach(function (f, i) {
        var o = ctx.createOscillator(), g = ctx.createGain();
        o.type = 'sine'; o.frequency.value = f; o.connect(g); g.connect(ctx.destination);
        g.gain.setValueAtTime(0.0001, t + i * 0.14); g.gain.exponentialRampToValueAtTime(0.25, t + i * 0.14 + 0.02); g.gain.exponentialRampToValueAtTime(0.0001, t + i * 0.14 + 0.22);
        o.start(t + i * 0.14); o.stop(t + i * 0.14 + 0.25);
      });
    } catch (e) { }
  }

  function askPermission() { if ('Notification' in window && Notification.permission === 'default') Notification.requestPermission(); }
  // Navegadores só liberam áudio/notificação após um gesto do usuário.
  document.addEventListener('click', function once() { askPermission(); try { ctx = ctx || new (window.AudioContext || window.webkitAudioContext)(); ctx.resume(); } catch (e) { } document.removeEventListener('click', once); });

  function show(unread) {
    if (badge) { badge.textContent = unread; badge.classList.toggle('d-none', !unread); }
    document.title = unread > 0 ? '(' + unread + ') ' + baseTitle : baseTitle;
  }

  function notify(items) {
    beep();
    if (!('Notification' in window) || Notification.permission !== 'granted') return;
    items.slice(-3).forEach(function (it) {
      var n = new Notification(it.name, { body: it.text || 'Nova mensagem', tag: 'wa-' + it.conv, icon: '/favicon.ico' });
      n.onclick = function () { window.focus(); location.href = '/WhatsApp?scope=' + it.scope + '&id=' + it.conv; n.close(); };
    });
  }

  function poll() {
    if (document.hidden && last === null) return;
    fetch('/WhatsApp/Poll?after=' + (last || 0), { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (d) {
      if (!d) return;
      var first = last === null;
      if (first) { var saved = parseInt(sessionStorage.getItem('wa.last') || '0', 10); last = saved || d.last; if (saved) return poll(); }
      else { last = Math.max(last, d.last); if (d.items.length) notify(d.items); }
      sessionStorage.setItem('wa.last', last);
      show(d.unread);
    }).catch(function () { });
  }

  poll();
  setInterval(poll, 6000);
})();
