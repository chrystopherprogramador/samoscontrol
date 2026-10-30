(function () {
  var NS = 'http://www.w3.org/2000/svg';
  var canvas = document.getElementById('mapCanvas'), tip = document.getElementById('mapTip');
  var csrf = (document.querySelector('meta[name=csrf]') || {}).content;
  var metric = 'score', showCities = true, selected = null, data = null, shapes = null;
  var states = {}, cityNodes = [], svg, gStates, gCities, view = null, FULL = null;

  function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : String(s); return d.innerHTML; }
  function proj(lon, lat) { return [(lon + 74.2) * 19, (5.4 - lat) * 20]; }
  function el(n, a) { var e = document.createElementNS(NS, n); for (var k in a) e.setAttribute(k, a[k]); return e; }
  function val(s) { return s[metric] || 0; }
  function ramp(t) { return 'rgba(99,102,241,' + (0.16 + 0.8 * t).toFixed(2) + ')'; }
  function cityColor(t) { var h = 190 - 170 * t; return 'hsl(' + h.toFixed(0) + ',90%,55%)'; }

  function setView(v, animate) {
    if (!animate || !view) { view = v; svg.setAttribute('viewBox', v.join(' ')); return; }
    var from = view.slice(), steps = 22, i = 0;
    (function tick() {
      i++; var t = i / steps; t = 1 - Math.pow(1 - t, 3);
      view = from.map(function (x, k) { return x + (v[k] - x) * t; });
      svg.setAttribute('viewBox', view.join(' '));
      if (i < steps) requestAnimationFrame(tick);
    })();
  }

  function build() {
    canvas.innerHTML = '';
    svg = el('svg', { viewBox: '0 0 800 800', class: 'map-svg', preserveAspectRatio: 'xMidYMid meet' });
    gStates = el('g', {}); gCities = el('g', {});
    svg.appendChild(gStates); svg.appendChild(gCities);
    var minx = 1e9, miny = 1e9, maxx = -1e9, maxy = -1e9;
    Object.keys(shapes).forEach(function (uf) {
      var d = '', bb = [1e9, 1e9, -1e9, -1e9];
      shapes[uf].r.forEach(function (ring) {
        ring.forEach(function (p, i) {
          var q = proj(p[0], p[1]); d += (i ? 'L' : 'M') + q[0].toFixed(1) + ',' + q[1].toFixed(1);
          bb[0] = Math.min(bb[0], q[0]); bb[1] = Math.min(bb[1], q[1]); bb[2] = Math.max(bb[2], q[0]); bb[3] = Math.max(bb[3], q[1]);
        });
        d += 'Z';
      });
      var p = el('path', { d: d, class: 'st', 'data-uf': uf });
      gStates.appendChild(p);
      states[uf] = { node: p, bb: bb, name: shapes[uf].n };
      minx = Math.min(minx, bb[0]); miny = Math.min(miny, bb[1]); maxx = Math.max(maxx, bb[2]); maxy = Math.max(maxy, bb[3]);
      p.addEventListener('mousemove', function (e) { showTip(e, stateTip(uf)); });
      p.addEventListener('mouseleave', hideTip);
      p.addEventListener('click', function () { selectState(uf); });
    });
    FULL = [minx - 10, miny - 10, maxx - minx + 20, maxy - miny + 20];
    setView(FULL.slice(), false);
    canvas.appendChild(svg);
    cityNodes = [];
    var maxScore = Math.max.apply(null, data.cities.map(function (c) { return c.score; }).concat([1]));
    data.cities.slice().reverse().forEach(function (c, idx, arr) {
      var q = proj(c.lng, c.lat), t = c.score / maxScore;
      var r = Math.min(16, 3.5 + Math.sqrt(c.total) * 2.4);
      var g = el('g', { class: 'city', transform: 'translate(' + q[0].toFixed(1) + ',' + q[1].toFixed(1) + ')' });
      if (idx >= arr.length - 3) g.appendChild(el('circle', { r: r, class: 'pulse', fill: cityColor(t) }));
      g.appendChild(el('circle', { r: r, fill: cityColor(t), class: 'dot' }));
      g.addEventListener('mousemove', function (e) { showTip(e, cityTip(c)); });
      g.addEventListener('mouseleave', hideTip);
      g.addEventListener('click', function (e) { e.stopPropagation(); selectCity(c); });
      gCities.appendChild(g); cityNodes.push(g);
    });
    paint();
  }

  function paint() {
    var max = Math.max.apply(null, data.states.map(val).concat([1]));
    Object.keys(states).forEach(function (uf) { states[uf].node.style.fill = ''; states[uf].node.classList.remove('has', 'sel'); });
    data.states.forEach(function (s) {
      var n = states[s.uf]; if (!n) return;
      n.node.classList.add('has'); n.node.style.fill = ramp(val(s) / max);
    });
    if (selected && states[selected]) states[selected].node.classList.add('sel');
    gCities.style.display = showCities ? '' : 'none';
  }

  function stateTip(uf) {
    var s = data.states.filter(function (x) { return x.uf === uf; })[0];
    if (!s) return '<b>' + esc(states[uf].name) + '</b><br><span class="text-muted">Sem leads</span>';
    return '<b>' + esc(s.name) + '</b><br>' + s.total + ' leads · ' + s.novos + ' novos<br>' + s.ganhos + ' ganhos · conversão ' + s.conv + '%';
  }
  function cityTip(c) { return '<b>' + esc(c.city) + '/' + c.uf + '</b><br>' + c.total + ' leads · ' + c.novos + ' novos<br>' + c.ganhos + ' ganhos' + (c.topSegment ? '<br>' + esc(c.topSegment) : ''); }
  function showTip(e, html) {
    var r = canvas.parentNode.getBoundingClientRect();
    tip.innerHTML = html; tip.classList.remove('d-none');
    tip.style.left = Math.min(e.clientX - r.left + 14, r.width - 190) + 'px'; tip.style.top = (e.clientY - r.top + 14) + 'px';
  }
  function hideTip() { tip.classList.add('d-none'); }

  function zoomTo(bb) {
    var w = bb[2] - bb[0], h = bb[3] - bb[1], pad = Math.max(w, h) * 0.25, side = Math.max(w, h) + pad * 2;
    setView([bb[0] - (side - w) / 2, bb[1] - (side - h) / 2, side, side], true);
  }

  function leadsLink(uf, city) { return '/Leads?state=' + encodeURIComponent(uf) + (city ? '&city=' + encodeURIComponent(city) : ''); }
  function stat(l, v) { return '<div class="col-4"><div class="kpi-label">' + l + '</div><div class="fw-bold fs-5">' + v + '</div></div>'; }

  function detail(title, s, segs, cities, uf, city) {
    document.getElementById('detailHint').classList.add('d-none');
    var b = document.getElementById('detailBody'); b.classList.remove('d-none');
    var h = '<div class="d-flex align-items-center mb-2"><h6 class="mb-0">' + esc(title) + '</h6><a class="btn btn-sm btn-outline-primary ms-auto" href="' + leadsLink(uf, city) + '"><i class="bi bi-list-ul"></i> Ver leads</a></div>';
    h += '<div class="row g-2 mb-2">' + stat('Leads', s.total) + stat('Novos', s.novos) + stat('Em aberto', s.open) + stat('Ganhos', s.ganhos) + stat('Perdidos', s.perdidos) + stat('Score', s.score) + '</div>';
    if (segs && segs.length) h += '<div class="mb-2">' + segs.map(function (x) { return '<span class="badge text-bg-secondary me-1">' + esc(x) + '</span>'; }).join('') + '</div>';
    if (cities && cities.length) h += '<div class="small text-muted mb-1">Cidades</div>' + cities.slice(0, 6).map(function (c) {
      return '<a href="#" class="rank-row" data-city="' + esc(c.city) + '"><span>' + esc(c.city) + '</span><b>' + c.total + '</b></a>'; }).join('');
    b.innerHTML = h;
    b.querySelectorAll('[data-city]').forEach(function (a) {
      a.addEventListener('click', function (e) { e.preventDefault(); var c = data.cities.filter(function (x) { return x.uf === uf && x.city === a.getAttribute('data-city'); })[0]; if (c) selectCity(c); });
    });
  }

  function selectState(uf) {
    selected = uf; paint();
    var s = data.states.filter(function (x) { return x.uf === uf; })[0];
    zoomTo(states[uf].bb);
    if (!s) { document.getElementById('detailHint').classList.add('d-none'); var b = document.getElementById('detailBody'); b.classList.remove('d-none'); b.innerHTML = '<h6>' + esc(states[uf].name) + '</h6><div class="text-muted small">Nenhum lead neste estado ainda. Que tal criar uma pesquisa no Google Scraper?</div>'; return; }
    detail(s.name, s, s.segments.map(function (x) { return x.key + ' (' + x.value + ')'; }), data.cities.filter(function (c) { return c.uf === uf; }), uf, null);
  }
  function selectCity(c) {
    selected = c.uf; paint(); zoomTo(states[c.uf].bb);
    detail(c.city + '/' + c.uf, c, c.topSegment ? [c.topSegment] : [], null, c.uf, c.city);
  }

  function rank() {
    var max = Math.max.apply(null, data.states.map(function (s) { return s.score; }).concat([1]));
    document.getElementById('rank').innerHTML = data.states.slice(0, 8).map(function (s, i) {
      return '<a href="#" class="rank-row" data-uf="' + s.uf + '"><span class="rank-n">' + (i + 1) + '</span><span class="flex-grow-1"><span class="d-flex justify-content-between"><span>' + esc(s.name) + '</span><b>' + s.score + '</b></span><span class="bar"><i style="width:' + Math.max(4, 100 * s.score / max) + '%"></i></span></span></a>';
    }).join('') || '<div class="text-muted small">Sem dados.</div>';
    document.querySelectorAll('#rank [data-uf]').forEach(function (a) { a.addEventListener('click', function (e) { e.preventDefault(); selectState(a.getAttribute('data-uf')); }); });
  }

  function insights() {
    var btn = document.getElementById('aiRun'), out = document.getElementById('aiOut');
    btn.disabled = true; out.innerHTML = '<span class="spinner-border spinner-border-sm"></span> Analisando seus leads...';
    fetch('/Map/Insights', { method: 'POST', headers: { 'X-CSRF-TOKEN': csrf, 'Content-Type': 'application/json' }, body: '{}' })
      .then(function (r) { return r.json(); }).then(function (r) {
        var h = r.summary ? '<p class="mb-2">' + esc(r.summary) + '</p>' : '';
        h += (r.actions || []).map(function (a, i) {
          return '<div class="ai-card p-' + esc(a.priority) + '" data-i="' + i + '"><div class="d-flex align-items-center"><b>' + esc(a.title) + '</b><span class="badge ms-auto prio-' + esc(a.priority) + '">' + esc(a.priority) + '</span></div>' +
            '<div class="text-muted">' + esc(a.why) + '</div><ul>' + (a.steps || []).map(function (s) { return '<li>' + esc(s) + '</li>'; }).join('') + '</ul>' +
            (a.uf ? '<a href="#" class="small" data-go="' + i + '"><i class="bi bi-geo-alt"></i> Ver no mapa</a>' : '') + '</div>';
        }).join('');
        h += '<div class="text-muted" style="font-size:.7rem">' + (r.ai ? 'Gerado por IA a partir dos seus dados.' : 'Sugestões automáticas (configure a IA em Configurações para análises mais ricas).') + '</div>';
        out.innerHTML = h || 'Sem sugestões.';
        out.querySelectorAll('[data-go]').forEach(function (a) {
          a.addEventListener('click', function (e) {
            e.preventDefault(); var x = r.actions[+a.getAttribute('data-go')];
            var c = x.city && data.cities.filter(function (k) { return k.uf === x.uf && k.city.toLowerCase() === x.city.toLowerCase(); })[0];
            if (c) selectCity(c); else selectState(x.uf);
            canvas.scrollIntoView({ behavior: 'smooth', block: 'center' });
          });
        });
      }).catch(function () { out.textContent = 'Não foi possível gerar agora.'; })
      .finally(function () { btn.disabled = false; });
  }

  document.getElementById('mapMetric').addEventListener('click', function (e) {
    var b = e.target.closest('[data-m]'); if (!b) return; metric = b.getAttribute('data-m');
    this.querySelectorAll('.btn').forEach(function (x) { x.classList.toggle('active', x === b); }); paint();
  });
  document.getElementById('mapCities').addEventListener('change', function () { showCities = this.checked; paint(); });
  document.getElementById('mapReset').addEventListener('click', function () { selected = null; paint(); setView(FULL.slice(), true); });
  document.getElementById('aiRun').addEventListener('click', insights);

  Promise.all([fetch('/data/br-states.json').then(function (r) { return r.json(); }), fetch('/Map/Data').then(function (r) { return r.json(); })]).then(function (res) {
    shapes = res[0]; data = res[1];
    document.getElementById('kpiTotal').textContent = data.total;
    document.getElementById('kpiStates').textContent = data.states.length;
    document.getElementById('kpiUnloc').textContent = data.unlocated;
    build(); rank();
  }).catch(function () { canvas.innerHTML = '<div class="map-loading">Não foi possível carregar o mapa.</div>'; });
})();
