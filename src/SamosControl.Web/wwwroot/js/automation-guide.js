(function () {
  var type = document.getElementById('jobType');
  if (!type) return;
  var cfg = document.getElementById('configJson');
  var name = document.getElementById('jobName');
  var rec = document.getElementById('recurrence');
  var box = document.getElementById('typeHelp');
  var recHelp = document.getElementById('recHelp');

  var guide = {
    Reminder: { title: 'Lembrete', when: 'Aviso para você ou sua equipe.', what: 'Cria uma notificação no painel e, se o usuário tiver telefone e o WhatsApp estiver configurado, manda também no WhatsApp dele.', fields: 'message (texto do lembrete), userId (opcional: quem recebe; padrão = quem criou)', example: { message: 'Ligar para os leads em PROPOSTA' }, rec: 1440 },
    SendWhatsApp: { title: 'Enviar WhatsApp', when: 'Mensagem automática para um número.', what: 'Envia o texto pela Evolution API. Exige o WhatsApp configurado.', fields: 'phone (DDI+DDD+número, só dígitos), text (mensagem)', example: { phone: '5562999999999', text: 'Olá! Passando para confirmar nossa reunião.' }, rec: 10080 },
    GenerateReport: { title: 'Relatório', when: 'Resumo diário/semanal da operação.', what: 'Gera o resumo do dashboard (leads, contratos, cobranças, serviços) como notificação e envia por WhatsApp aos números informados.', fields: 'phones (números separados por vírgula, DDI+DDD+número)', example: { phones: '5562999999999,5562988888888' }, rec: 1440 },
    SyncLeads: { title: 'Sincronizar leads (Google Maps)', when: 'Importar o que o scraper já coletou.', what: 'Consulta as pesquisas em andamento no scraper e importa as concluídas sem duplicar. Sem configuração extra.', fields: '(nenhum)', example: null, rec: 60 },
    CheckServices: { title: 'Verificar serviços', when: 'Forçar uma checagem de todos os serviços do Monitoramento.', what: 'Testa HTTP/HTTPS/TCP de cada serviço ativo e atualiza o status. O monitoramento já roda sozinho; use para uma checagem extra.', fields: '(nenhum)', example: null, rec: 15 },
    CheckBilling: { title: 'Cobranças', when: 'Rotina financeira.', what: 'Marca cobranças vencidas, atualiza pagamentos Efí e gera as cobranças dos contratos que vencem.', fields: '(nenhum)', example: null, rec: 30 }
  };

  function esc(s) { var d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

  function render() {
    var g = guide[type.value];
    if (!g) { box.innerHTML = ''; return; }
    box.innerHTML = '<div class="fw-semibold mb-1">' + esc(g.title) + '</div>' +
      '<div class="small mb-1"><strong>Quando usar:</strong> ' + esc(g.when) + '</div>' +
      '<div class="small mb-1"><strong>O que faz:</strong> ' + esc(g.what) + '</div>' +
      '<div class="small mb-2"><strong>Configuração:</strong> <code>' + esc(g.fields) + '</code></div>' +
      '<button type="button" class="btn btn-sm btn-outline-primary" id="useExample">Usar exemplo e repetição sugerida</button>';
    document.getElementById('useExample').onclick = function () {
      cfg.value = g.example ? JSON.stringify(g.example, null, 2) : '';
      rec.value = g.rec;
      if (!name.value) name.value = g.title;
      hint();
    };
  }

  function hint() {
    var m = parseInt(rec.value, 10);
    if (!m) { recHelp.textContent = 'Sem repetição: executa uma única vez na data escolhida (ou só manualmente).'; return; }
    var t = m % 1440 === 0 ? (m / 1440) + ' dia(s)' : m % 60 === 0 ? (m / 60) + ' hora(s)' : m + ' minuto(s)';
    recHelp.textContent = 'Repete a cada ' + t + (document.getElementById('nextRun').value ? '' : '; primeira execução em ' + t + '.');
  }

  document.querySelectorAll('[data-rec]').forEach(function (b) { b.onclick = function () { rec.value = b.getAttribute('data-rec'); hint(); }; });
  type.addEventListener('change', render);
  rec.addEventListener('input', hint);
  document.getElementById('nextRun').addEventListener('input', hint);
  render(); hint();
})();
