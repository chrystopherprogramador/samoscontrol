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

  function av(pic, ini) { return '<span class="wa-av">' + (pic ? '<img src="' + esc(pic) + '" alt="" referrerpolicy="no-referrer" onerror="this.remove()">' : '') + '<b>' + esc(ini) + '</b></span>'; }
  function post(url, body) {
    return fetch(url, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf }, body: body ? JSON.stringify(body) : undefined })
      .then(function (r) { return r.json().then(function (j) { if (!r.ok) throw new Error(j.error || 'Erro'); return j; }); });
  }
  var curConv = null;

  function loadList() {
    var q = search.value.trim();
    return getJson('/WhatsApp/List?scope=' + encodeURIComponent(scope) + '&q=' + encodeURIComponent(q)).then(function (rows) {
      if (!rows.length) { convsEl.innerHTML = '<div class="p-3 text-muted small">Nenhuma conversa.</div>'; return; }
      convsEl.innerHTML = rows.map(function (c) {
        return '<div class="wa-conv' + (c.id === current ? ' active' : '') + '" data-id="' + c.id + '">' +
          av(c.pic, c.initial) +
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
      curConv = c;
      $('waHeadAv').innerHTML = av(c.pic, (c.name || '#').charAt(0).toUpperCase()).replace(/^<span[^>]*>|<\/span>$/g, '');
      var hs = $('waHeadStatus');
      if (c.leadStatus && c.canEditLead) {
        if (hs.dataset.conv !== String(c.id) || hs.options.length === 0) { hs.innerHTML = c.statuses.map(function (s) { return '<option>' + s + '</option>'; }).join(''); hs.dataset.conv = c.id; }
        if (document.activeElement !== hs) hs.value = c.leadStatus;
        hs.classList.remove('d-none');
      } else hs.classList.add('d-none');
      var li = $('waLeadInfo'); li.textContent = c.leadInfo || ''; li.classList.toggle('d-none', !c.leadInfo);
      $('waDelete').classList.toggle('d-none', !c.canDelete);
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

  $('waHeadStatus').addEventListener('change', function () { var s = this.value; post('/WhatsApp/SetLeadStatus?id=' + current + '&status=' + encodeURIComponent(s)).catch(function (e) { alert(e.message); }); });
  $('waDelete').addEventListener('click', function () {
    if (!current || !confirm('Apagar esta conversa e todas as mensagens? Isso não pode ser desfeito.')) return;
    post('/WhatsApp/DeleteConversation?id=' + current).then(function () { current = 0; msgsEl.innerHTML = ''; $('waChat').classList.add('d-none'); $('waEmpty').classList.remove('d-none'); wa.classList.remove('chat-open'); history.replaceState(null, '', '?scope=' + encodeURIComponent(scope)); loadList(); }).catch(function (e) { alert(e.message); });
  });
  var snd = $('waSound');
  function paintSound() { var on = !window.waNotify || window.waNotify.soundOn(); snd.innerHTML = '<i class="bi ' + (on ? 'bi-bell' : 'bi-bell-slash') + '"></i>'; }
  snd.addEventListener('click', function () { if (!window.waNotify) return; window.waNotify.setSound(!window.waNotify.soundOn()); paintSound(); if (window.waNotify.soundOn()) window.waNotify.beep(); window.waNotify.askPermission(); });
  paintSound();

  var emo = $('waEmojis'), tpl = $('waTpls');
  if (emo) {
    emo.innerHTML = '😀 😁 😂 🤣 😊 😍 😘 😎 🤗 🤔 😅 😉 🙂 🙏 👍 👏 🙌 💪 🔥 ✨ 🎉 ❤️ 💙 ✅ 📞 📅 💰 📍 🚀 😢 😮 👋'.split(' ').map(function (e) { return '<button type="button">' + e + '</button>'; }).join('');
    emo.addEventListener('click', function (e) { if (e.target.tagName === 'BUTTON') { var t = $('waText'); t.setRangeText(e.target.textContent, t.selectionStart, t.selectionEnd, 'end'); t.focus(); } });
    $('waEmoji').addEventListener('click', function () { tpl.classList.add('d-none'); emo.classList.toggle('d-none'); });
    $('waTpl').addEventListener('click', function () { emo.classList.add('d-none'); if (tpl.classList.contains('d-none')) { tpl.classList.remove('d-none'); loadTpls(); } else tpl.classList.add('d-none'); });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') { emo.classList.add('d-none'); tpl.classList.add('d-none'); } });
  }

  function fill(body) {
    var c = curConv || {}, first = (c.name || '').split(' ')[0];
    return body.replace(/\{\{\s*NOME\s*\}\}/gi, first).replace(/\{\{\s*VENDEDOR\s*\}\}/gi, c.seller || '');
  }
  function loadTpls(edit) {
    getJson('/WhatsApp/Templates?conv=' + current).then(function (rows) {
      var h = '<div class="d-flex justify-content-between align-items-center mb-1"><strong class="small">Mensagens prontas</strong><span class="small text-muted">Use {{NOME}} e {{VENDEDOR}}</span></div>';
      h += rows.map(function (t) {
        return '<div class="tpl" data-id="' + t.id + '"><span class="tpl-act">' + (t.editable ? '<a href="#" data-act="edit">editar</a> · <a href="#" class="text-danger" data-act="del">apagar</a>' : '') + '</span><strong>' + esc(t.name) + (t.shared ? ' <span class="badge bg-secondary">todos</span>' : '') + '</strong><small>' + esc(t.body) + '</small></div>';
      }).join('') || '<div class="text-muted small p-2">Nenhuma ainda.</div>';
      var ed = edit || { id: 0, name: '', body: '' };
      h += '<form id="tplForm" class="mt-2 border-top pt-2"><input class="form-control form-control-sm" id="tplName" placeholder="Nome (ex.: Apresentação)" maxlength="80" value="' + esc(ed.name) + '"><textarea class="form-control form-control-sm" id="tplBody" rows="3" maxlength="2000" placeholder="Texto da mensagem">' + esc(ed.body) + '</textarea>' +
        '<div class="d-flex gap-2 align-items-center">' + (wa.dataset.all === 'true' ? '<label class="small me-auto"><input type="checkbox" id="tplShared"' + (ed.shared ? ' checked' : '') + '> Para todos</label>' : '<span class="me-auto"></span>') + '<button class="btn btn-sm btn-success">' + (ed.id ? 'Salvar' : 'Cadastrar') + '</button></div></form>';
      tpl.innerHTML = h; tpl._rows = rows; tpl._edit = ed;
    }).catch(function () { });
  }
  if (tpl) {
    tpl.addEventListener('click', function (e) {
      var row = e.target.closest('.tpl'); if (!row) return;
      var id = parseInt(row.dataset.id, 10), t = tpl._rows.filter(function (x) { return x.id === id; })[0], act = e.target.dataset.act;
      if (act) {
        e.preventDefault();
        if (act === 'edit') loadTpls(t);
        else if (confirm('Apagar “' + t.name + '”?')) post('/WhatsApp/TemplateDelete?id=' + id).then(function () { loadTpls(); }).catch(function (x) { alert(x.message); });
        return;
      }
      var ta = $('waText'); ta.value = fill(t.body); ta.dispatchEvent(new Event('input')); tpl.classList.add('d-none'); ta.focus();
    });
    tpl.addEventListener('submit', function (e) {
      e.preventDefault();
      var sh = $('tplShared');
      post('/WhatsApp/TemplateSave', { id: tpl._edit.id, conv: current, name: $('tplName').value, body: $('tplBody').value, shared: !!(sh && sh.checked) }).then(function () { loadTpls(); }).catch(function (x) { alert(x.message); });
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
