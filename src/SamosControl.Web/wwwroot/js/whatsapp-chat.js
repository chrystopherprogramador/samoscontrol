(function () {
  var wa = document.getElementById('wa'); if (!wa) return;
  var scope = wa.dataset.scope, canSend = wa.dataset.canSend === 'true';
  var $ = function (id) { return document.getElementById(id); };
  var csrf = (document.querySelector('meta[name=csrf]') || {}).content;
  var current = parseInt(wa.dataset.current || '0', 10) || 0, lastId = 0, lastDay = null, canSendConv = true;
  var convsEl = $('waConvs'), msgsEl = $('waMsgs'), search = $('waSearch'), searchTimer;

  function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : s; return d.innerHTML; }
  function getJson(url) { return fetch(url, { credentials: 'same-origin' }).then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); }); }
  function tick(s) {
    s = (s || '').toUpperCase();
    if (s === 'READ' || s === 'PLAYED') return '<i class="bi bi-check2-all text-info"></i>';
    if (s === 'DELIVERY_ACK' || s === 'DELIVERED') return '<i class="bi bi-check2-all"></i>';
    return '<i class="bi bi-check2"></i>';
  }

  function loadList() {
    var q = search.value.trim();
    return getJson('/WhatsApp/List?scope=' + encodeURIComponent(scope) + '&q=' + encodeURIComponent(q)).then(function (rows) {
      if (!rows.length) { convsEl.innerHTML = '<div class="p-3 text-muted small">Nenhuma conversa.</div>'; return; }
      convsEl.innerHTML = rows.map(function (c) {
        return '<div class="wa-conv' + (c.id === current ? ' active' : '') + '" data-id="' + c.id + '">' +
          '<span class="wa-av">' + esc(c.initial) + '</span>' +
          '<div class="wa-conv-body"><div class="d-flex justify-content-between"><span class="fw-semibold text-truncate">' + esc(c.name) + '</span><small class="text-muted ms-2">' + esc(c.time) + '</small></div>' +
          '<div class="d-flex justify-content-between"><small class="text-muted text-truncate">' + esc(c.preview || c.phone) + '</small>' +
          (c.unread > 0 ? '<span class="badge bg-success rounded-pill ms-2">' + c.unread + '</span>' : '') + '</div>' +
          (c.seller && wa.dataset.all === 'true' && scope === 'all' ? '<small class="wa-tag">' + esc(c.seller) + '</small>' : '') + '</div></div>';
      }).join('');
    }).catch(function () { });
  }

  convsEl.addEventListener('click', function (e) {
    var el = e.target.closest('.wa-conv'); if (el) openConv(parseInt(el.dataset.id, 10));
  });
  search.addEventListener('input', function () { clearTimeout(searchTimer); searchTimer = setTimeout(loadList, 250); });

  function openConv(id) {
    current = id; lastId = 0; lastDay = null; msgsEl.innerHTML = '';
    $('waEmpty').classList.add('d-none'); $('waChat').classList.remove('d-none'); wa.classList.add('chat-open');
    history.replaceState(null, '', '?scope=' + encodeURIComponent(scope) + '&id=' + id);
    loadMessages(true); loadList();
  }

  function loadMessages(first) {
    if (!current) return Promise.resolve();
    var id = current;
    return getJson('/WhatsApp/Messages?id=' + id + '&after=' + lastId).then(function (d) {
      if (id !== current) return;
      var c = d.conversation;
      $('waHeadAv').textContent = (c.name || '#').charAt(0).toUpperCase();
      $('waHeadName').textContent = c.name;
      $('waHeadSub').textContent = '+' + c.phone + (c.seller ? ' · WhatsApp de ' + c.seller : '');
      var links = '';
      if (c.leadId) links += '<a class="btn btn-sm btn-outline-secondary me-1" href="/Leads/Details/' + c.leadId + '">Lead</a>';
      if (c.customerId) links += '<a class="btn btn-sm btn-outline-secondary" href="/Customers/Details/' + c.customerId + '">Cliente</a>';
      $('waHeadLinks').innerHTML = links;
      canSendConv = c.canSend;
      var warn = $('waWarn');
      if (!c.canSend) { warn.textContent = 'WhatsApp deste número desconectado — não é possível enviar.'; warn.classList.remove('d-none'); } else warn.classList.add('d-none');
      var form = $('waForm'); if (form) form.classList.toggle('d-none', !c.canSend);
      var nearBottom = first || msgsEl.scrollHeight - msgsEl.scrollTop - msgsEl.clientHeight < 120;
      var html = '';
      d.messages.forEach(function (m) {
        if (m.day !== lastDay) { html += '<div class="wa-day"><span>' + esc(m.day) + '</span></div>'; lastDay = m.day; }
        var body = esc(m.body).replace(/(https?:\/\/[^\s<]+)/g, '<a href="$1" target="_blank" rel="noopener">$1</a>');
        html += '<div class="bubble ' + m.dir + '">' + body + '<div class="wa-meta">' + (m.agent ? 'IA · ' : '') + esc(m.time) + (m.dir === 'out' ? ' ' + tick(m.status) : '') + '</div></div>';
        lastId = Math.max(lastId, m.id);
      });
      if (html) { msgsEl.insertAdjacentHTML('beforeend', html); if (nearBottom) msgsEl.scrollTop = msgsEl.scrollHeight; }
    }).catch(function () { });
  }

  function send(text) {
    return fetch('/WhatsApp/Send', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf }, body: JSON.stringify({ id: current, text: text }) })
      .then(function (r) { return r.json().then(function (j) { if (!r.ok) throw new Error(j.error || 'Falha no envio'); return j; }); });
  }

  var form = $('waForm');
  if (form) {
    var ta = $('waText'), btn = $('waSend');
    var autosize = function () { ta.style.height = 'auto'; ta.style.height = Math.min(ta.scrollHeight, 140) + 'px'; };
    ta.addEventListener('input', autosize);
    ta.addEventListener('keydown', function (e) { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); form.requestSubmit(); } });
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var t = ta.value.trim(); if (!t || !current || !canSendConv) return;
      btn.disabled = true;
      send(t).then(function () { ta.value = ''; autosize(); return loadMessages(true); })
        .catch(function (err) { alert(err.message); })
        .then(function () { btn.disabled = false; ta.focus(); loadList(); });
    });
    $('waAttach').addEventListener('click', function () { $('waFile').click(); });
    $('waFile').addEventListener('change', function () {
      var f = this.files[0]; if (!f || !current) return;
      var cap = ta.value.trim();
      var fd = new FormData(); fd.append('id', current); fd.append('file', f); if (cap) fd.append('caption', cap);
      btn.disabled = true;
      fetch('/WhatsApp/SendMedia', { method: 'POST', credentials: 'same-origin', headers: { 'X-CSRF-TOKEN': csrf }, body: fd })
        .then(function (r) { return r.json().then(function (j) { if (!r.ok) throw new Error(j.error || 'Falha no envio'); }); })
        .then(function () { ta.value = ''; autosize(); return loadMessages(true); })
        .catch(function (err) { alert(err.message); })
        .then(function () { btn.disabled = false; $('waFile').value = ''; loadList(); });
    });
  }

  var nw = $('waNew');
  if (nw) nw.addEventListener('click', function () {
    var phone = prompt('Telefone com DDD (ex.: 65999998888):'); if (!phone) return;
    var name = prompt('Nome do contato (opcional):') || '';
    fetch('/WhatsApp/NewChat', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf }, body: JSON.stringify({ scope: parseInt(scope, 10) || 0, phone: phone, name: name }) })
      .then(function (r) { return r.json().then(function (j) { if (!r.ok) throw new Error(j.error || 'Erro'); return j; }); })
      .then(function (j) { openConv(j.id); })
      .catch(function (err) { alert(err.message); });
  });

  $('waBack').addEventListener('click', function () { wa.classList.remove('chat-open'); });

  loadList();
  if (current) openConv(current);
  setInterval(function () { if (document.hidden) return; loadMessages(false); loadList(); }, 4000);
})();
