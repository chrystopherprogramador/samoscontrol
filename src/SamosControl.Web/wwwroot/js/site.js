(function () {
  var csrf = (document.querySelector('meta[name=csrf]') || {}).content;
  window.samosPost = function (url, body) {
    return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf }, body: JSON.stringify(body || {}) })
      .then(function (r) { return r.json(); });
  };

  var shell = document.querySelector('.axis-shell');
  var mt = document.getElementById('menuToggle');
  var overlay = document.getElementById('axisSidebarOverlay');
  var mobile = function () { return window.matchMedia('(max-width: 768px)').matches; };
  if (shell && localStorage.getItem('samosSidebarCollapsed') === '1') shell.classList.add('sidebar-collapsed');
  if (mt && shell) mt.addEventListener('click', function () {
    if (mobile()) { shell.classList.toggle('sidebar-open'); return; }
    shell.classList.toggle('sidebar-collapsed');
    localStorage.setItem('samosSidebarCollapsed', shell.classList.contains('sidebar-collapsed') ? '1' : '0');
  });
  if (overlay && shell) overlay.addEventListener('click', function () { shell.classList.remove('sidebar-open'); });

  document.addEventListener('submit', function (e) {
    var m = e.target.getAttribute('data-confirm');
    if (m && !window.confirm(m)) e.preventDefault();
  });

  // Charts: <canvas data-chart="bar|line|doughnut" data-labels='[..]' data-values='[..]' data-label="...">
  if (window.Chart) {
    document.querySelectorAll('canvas[data-chart]').forEach(function (c) {
      var type = c.dataset.chart, labels = JSON.parse(c.dataset.labels || '[]'), values = JSON.parse(c.dataset.values || '[]');
      var palette = ['#4f46e5', '#0ea5e9', '#f59e0b', '#8b5cf6', '#14b8a6', '#22c55e', '#ef4444'];
      new Chart(c, {
        type: type,
        data: { labels: labels, datasets: [{ label: c.dataset.label || '', data: values, borderColor: '#4f46e5', tension: .3,
          backgroundColor: type === 'doughnut' ? palette : (type === 'line' ? 'rgba(79,70,229,.15)' : '#4f46e5'), fill: type === 'line' }] },
        options: { responsive: true, plugins: { legend: { display: type === 'doughnut' } }, scales: type === 'doughnut' ? {} : { y: { beginAtZero: true, ticks: { precision: 0 } } } }
      });
    });
  }

  // Notifications
  var cnt = document.getElementById('notifCount'), menu = document.getElementById('notifMenu');
  function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : s; return d.innerHTML; }
  function refresh() {
    fetch('/Notifications/Latest').then(function (r) { return r.ok ? r.json() : null; }).then(function (d) {
      if (!d || !cnt) return;
      cnt.textContent = d.unread; cnt.classList.toggle('d-none', d.unread === 0);
      var h = d.items.map(function (n) {
        return '<a class="dropdown-item small border-bottom ' + (n.read ? 'text-muted' : 'fw-semibold') + '" href="' + esc(n.link || '/Notifications') + '"><div>' + esc(n.title) + '</div><div class="text-muted">' + esc(n.at) + '</div></a>';
      }).join('') || '<div class="p-3 text-muted small">Sem notificações.</div>';
      h += '<div class="d-flex justify-content-between p-2"><a href="/Notifications" class="small">Ver todas</a><a href="#" id="markAll" class="small">Marcar como lidas</a></div>';
      menu.innerHTML = h;
      var ma = document.getElementById('markAll');
      if (ma) ma.addEventListener('click', function (e) { e.preventDefault(); samosPost('/Notifications/MarkRead', {}).then(refresh); });
    }).catch(function () { });
  }
  if (cnt) {
    refresh(); setInterval(refresh, 30000);
    if (window.signalR) {
      var conn = new signalR.HubConnectionBuilder().withUrl('/hubs/notifications').withAutomaticReconnect().build();
      conn.on('notification', refresh);
      conn.start().catch(function () { });
    }
  }

  // Assistant chat
  var form = document.getElementById('chatForm');
  if (form) {
    var box = document.getElementById('chatBox'), input = document.getElementById('chatInput');
    box.scrollTop = box.scrollHeight;
    function add(role, text) {
      var d = document.createElement('div'); d.className = 'bubble ' + (role === 'user' ? 'out' : 'in'); d.textContent = text;
      box.appendChild(d); box.scrollTop = box.scrollHeight; return d;
    }
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var t = input.value.trim(); if (!t) return;
      input.value = ''; add('user', t); var w = add('assistant', '...');
      samosPost('/Assistant/Ask', { message: t }).then(function (r) { w.textContent = r.reply || r.error || 'Sem resposta.'; box.scrollTop = box.scrollHeight; })
        .catch(function () { w.textContent = 'Falha ao contatar o servidor.'; });
    });
  }
})();
