(function () {
  var root = document.getElementById('waconn'); if (!root) return;
  var id = root.dataset.id, csrf = (document.querySelector('meta[name=csrf]') || {}).content;
  var $ = function (i) { return document.getElementById(i); };
  var poll = null, qrTimer = null;

  function show(err) { var e = $('waErr'); if (err) { e.textContent = err; e.classList.remove('d-none'); } else e.classList.add('d-none'); }
  function post(url) { return fetch(url, { method: 'POST', credentials: 'same-origin', headers: { 'X-CSRF-TOKEN': csrf } }).then(function (r) { return r.json().then(function (j) { if (!r.ok) throw new Error(j.error || 'Erro ' + r.status); return j; }); }); }
  function setQr(r) {
    if (r.qr) { $('waQr').src = r.qr; $('waQr').classList.remove('d-none'); $('waQrMsg').classList.add('d-none'); }
    if (r.pairingCode) { $('waPairCode').textContent = r.pairingCode; $('waPair').classList.remove('d-none'); }
  }
  function badge(st) {
    var b = $('waStatusBadge'); b.textContent = st;
    b.className = 'badge ' + (st === 'CONECTADO' ? 'bg-success' : st === 'CONECTANDO' ? 'bg-warning text-dark' : 'bg-secondary');
  }
  function stop() { clearInterval(poll); clearInterval(qrTimer); }
  function connected(s) {
    stop(); badge('CONECTADO');
    $('waNumber').textContent = (s.number ? '+' + s.number : '') + (s.profile ? ' · ' + s.profile : '');
    $('waQr').classList.add('d-none'); $('waPair').classList.add('d-none');
    $('waQrMsg').classList.remove('d-none'); $('waQrMsg').innerHTML = '<i class="bi bi-check-circle-fill text-success fs-1"></i><div class="mt-2 fw-semibold">WhatsApp conectado!</div><div class="small">As conversas já aparecem em Conversas.</div>';
    $('waStart').disabled = true;
  }
  function watch() {
    stop();
    poll = setInterval(function () {
      fetch('/WhatsApp/ConnectStatus/' + id, { credentials: 'same-origin' }).then(function (r) { return r.json(); })
        .then(function (s) { if (s.status === 'CONECTADO') connected(s); }).catch(function () { });
    }, 3000);
    qrTimer = setInterval(function () {
      post('/WhatsApp/ConnectQr/' + id).then(function (r) { if (r.state === 'open') return; setQr(r); }).catch(function () { });
    }, 25000);
  }

  $('waStart').addEventListener('click', function () {
    var b = this; b.disabled = true; show(null);
    $('waQrMsg').textContent = 'Criando instância e configurando webhook…';
    post('/WhatsApp/ConnectStart/' + id).then(function (r) {
      if (r.state === 'open') { connected({}); return; }
      badge('CONECTANDO'); setQr(r);
      if (!r.qr && !r.pairingCode) $('waQrMsg').textContent = 'Aguardando QR code da Evolution…';
      watch();
      if (!r.qr) setTimeout(function () { post('/WhatsApp/ConnectQr/' + id).then(setQr).catch(function () { }); }, 2500);
    }).catch(function (e) { show(e.message); $('waQrMsg').textContent = 'Falha ao conectar.'; }).then(function () { b.disabled = root.dataset.status === 'CONECTADO'; });
  });

  if (root.dataset.status === 'CONECTANDO') watch();
})();
