(function () {
  var csrf = (document.querySelector('meta[name=csrf]') || {}).content;
  var q = document.getElementById('testQ'), out = document.getElementById('testOut'), go = document.getElementById('testGo');
  if (!q) return;
  function run() {
    var text = q.value.trim(); if (!text) return;
    go.disabled = true; out.textContent = 'Pensando...';
    fetch('/Training/Test', { method: 'POST', headers: { 'X-CSRF-TOKEN': csrf, 'Content-Type': 'application/json' }, body: JSON.stringify({ question: text }) })
      .then(function (r) { return r.json(); }).then(function (r) {
        out.textContent = '';
        var box = document.createElement('div'); box.className = 'test-reply';
        box.textContent = r.error || r.reply || '(sem resposta)'; out.appendChild(box);
        if (r.gap) { var w = document.createElement('div'); w.className = 'text-danger mt-1'; w.textContent = 'A IA não sabia: a pergunta foi adicionada às pendentes.'; out.appendChild(w); }
      }).catch(function () { out.textContent = 'Falha ao consultar.'; }).finally(function () { go.disabled = false; });
  }
  go.addEventListener('click', run);
  q.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); run(); } });
})();
