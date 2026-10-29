using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        if (db.Database.IsRelational())
        {
            for (var attempt = 1; ; attempt++)
            {
                try { await db.Database.MigrateAsync(ct); break; }
                catch (Exception ex) when (attempt < 15)
                {
                    log.LogWarning("Banco indisponível (tentativa {Attempt}/15): {Msg}", attempt, ex.Message);
                    await Task.Delay(3000, ct);
                }
            }
        }
        else await db.Database.EnsureCreatedAsync(ct);

        await SeedRolesAsync(db, ct);
        await SeedAdminAsync(db, cfg, log, ct);
        await SeedDefaultsAsync(db, ct);
        if (string.Equals(cfg["SEED_DEMO_DATA"], "true", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("SEED_DEMO_DATA=true: inserindo dados demonstrativos (desabilite em produção).");
            await SeedDemoAsync(db, ct);
        }
    }

    private static async Task SeedRolesAsync(AppDbContext db, CancellationToken ct)
    {
        foreach (var (name, perms) in Perm.DefaultRoles)
        {
            var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Name == name, ct);
            if (role == null)
            {
                role = new Role { Name = name, IsSystem = true, Description = $"Perfil padrão {name}" };
                db.Roles.Add(role);
                role.Permissions.AddRange(perms.Select(p => new RolePermission { Permission = p }));
            }
            else if (name == "ADMIN") // the admin role always has every permission, including new ones from upgrades
            {
                foreach (var p in perms.Where(p => role.Permissions.All(x => x.Permission != p))) role.Permissions.Add(new RolePermission { Permission = p });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedAdminAsync(AppDbContext db, IConfiguration cfg, ILogger log, CancellationToken ct)
    {
        var email = (cfg["ADMIN_EMAIL"] ?? "").Trim().ToLowerInvariant();
        if (email.Length == 0) email = "admin@samos.local";
        if (await db.Users.AnyAsync(u => u.Role!.Name == "ADMIN", ct)) return;
        var password = cfg["ADMIN_PASSWORD"];
        var generated = false;
        if (AuthService.ValidatePassword(password) != null)
        {
            password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18)).Replace('+', 'A').Replace('/', 'b') + "9x";
            generated = true;
        }
        var role = await db.Roles.FirstAsync(r => r.Name == "ADMIN", ct);
        db.Users.Add(new User { Name = "Administrador", Email = email, PasswordHash = AuthService.Hash(password!), RoleId = role.Id });
        await db.SaveChangesAsync(ct);
        if (generated)
            log.LogWarning("ADMIN_PASSWORD ausente ou fraca (mín. 10 caracteres, letras e números). Senha inicial GERADA para {Email}: {Password}  — troque após o primeiro acesso e defina ADMIN_PASSWORD.", email, password);
        else log.LogInformation("Usuário administrador inicial criado: {Email}", email);
    }

    private static async Task SeedDefaultsAsync(AppDbContext db, CancellationToken ct)
    {
        foreach (var s in new[] { "Manual", "Google Maps", "Indicação", "Site", "WhatsApp" })
            if (!await db.LeadSources.AnyAsync(x => x.Name == s, ct)) db.LeadSources.Add(new LeadSource { Name = s });

        if (!await db.AutomationJobs.AnyAsync(j => j.IsSystem, ct))
        {
            var now = DateTime.UtcNow;
            db.AutomationJobs.AddRange(
                new AutomationJob { Name = "Sincronizar leads (Google Maps)", Type = JobType.SyncLeads, IsSystem = true, IsEnabled = false, RecurrenceMinutes = 60, NextRunAt = now.AddMinutes(60) },
                new AutomationJob { Name = "Verificar cobranças vencidas", Type = JobType.CheckBilling, IsSystem = true, IsEnabled = true, RecurrenceMinutes = 30, NextRunAt = now.AddMinutes(5) },
                new AutomationJob { Name = "Relatório diário", Type = JobType.GenerateReport, IsSystem = true, IsEnabled = false, RecurrenceMinutes = 1440, NextRunAt = now.AddDays(1), ConfigJson = "{\"phones\":\"\"}" });
        }
        // Efí charges are detected by polling, so the billing job (default 360 min) now runs every 30 min. Custom intervals are left alone.
        var billingJob = await db.AutomationJobs.FirstOrDefaultAsync(j => j.IsSystem && j.Type == JobType.CheckBilling && j.RecurrenceMinutes == 360, ct);
        if (billingJob != null) billingJob.RecurrenceMinutes = 30;
        // A shortened interval must not leave the next run scheduled far in the future.
        var nowUtc = DateTime.UtcNow;
        foreach (var j in await db.AutomationJobs.Where(j => j.IsEnabled && j.RecurrenceMinutes != null && j.NextRunAt != null).ToListAsync(ct))
            if (j.NextRunAt > nowUtc.AddMinutes(j.RecurrenceMinutes!.Value)) j.NextRunAt = nowUtc.AddMinutes(j.RecurrenceMinutes.Value);
        if (!await db.ContractTemplates.AnyAsync(ct))
            db.ContractTemplates.Add(new ContractTemplate { Name = "Prestação de serviços (exemplo)", Body = DefaultTemplate });
        if (!await db.ContractTemplates.AnyAsync(t => t.Name == SystemTemplateName, ct))
            db.ContractTemplates.Add(new ContractTemplate { Name = SystemTemplateName, Body = SystemTemplate });
        if (!await db.ContractTemplates.AnyAsync(t => t.Name == PartnershipTemplateName, ct))
            db.ContractTemplates.Add(new ContractTemplate { Name = PartnershipTemplateName, Body = PartnershipTemplate });
        await db.SaveChangesAsync(ct);
    }

    public const string PartnershipTemplateName = "Parceria comercial — vendedor (modelo)";

    /// <summary>Starting point only: must be reviewed by the company's lawyer before use.</summary>
    public const string PartnershipTemplate =
"""
# CONTRATO DE PARCERIA COMERCIAL E INTERMEDIAÇÃO DE VENDAS

EMPRESA: {{EMPRESA_NOME}}, inscrita no CNPJ {{EMPRESA_CNPJ}}, com sede em {{EMPRESA_ENDERECO}}, representada por {{REPRESENTANTE_NOME}}, CPF {{REPRESENTANTE_CPF}}.
PARCEIRO COMERCIAL: {{VENDEDOR_NOME}}, inscrito(a) no CPF/CNPJ {{VENDEDOR_CPF_CNPJ}}, e-mail {{VENDEDOR_EMAIL}}, endereço {{VENDEDOR_ENDERECO}}.

# CLÁUSULA 1 - DO OBJETO
1.1. O PARCEIRO atuará, de forma autônoma e sem exclusividade, na prospecção e indicação de clientes para os sistemas e serviços da EMPRESA.
1.2. Este contrato não gera vínculo empregatício, societário ou de representação com poderes para firmar contratos em nome da EMPRESA. Cabe ao PARCEIRO organizar seu tempo e meios.

# CLÁUSULA 2 - DA CARTEIRA DE CLIENTES
2.1. Considera-se cliente da carteira do PARCEIRO aquele cujo contrato for efetivamente fechado e assinado em decorrência da atuação do PARCEIRO e registrado como tal no sistema da EMPRESA.

# CLÁUSULA 3 - DA COMISSÃO
3.1. Implantação: {{COMISSAO_IMPLANTACAO}} do valor efetivamente recebido pela EMPRESA a título de implantação.
3.2. Mensalidade: {{COMISSAO_MENSALIDADE}} de cada mensalidade efetivamente recebida, {{COMISSAO_PRAZO}}.
3.3. As comissões são devidas somente após a confirmação do pagamento pelo cliente e calculadas sobre o valor efetivamente recebido, não incidindo sobre valores inadimplidos, estornados ou cancelados. Parcelas estornadas após o pagamento da comissão poderão ser compensadas em comissões futuras.
3.4. O pagamento será realizado por Pix na chave informada: {{VENDEDOR_PIX}}. O PARCEIRO é responsável pela exatidão da chave e pelos tributos incidentes sobre seus rendimentos.
3.5. Os percentuais acima valem para os contratos fechados a partir desta data; contratos já emitidos mantêm o percentual vigente em sua emissão.

# CLÁUSULA 4 - DAS OBRIGAÇÕES DO PARCEIRO
4.1. Agir com boa-fé e ética, não prometer condições, prazos ou funcionalidades não autorizados pela EMPRESA e não praticar publicidade enganosa.
4.2. Tratar dados pessoais obtidos em conformidade com a Lei nº 13.709/2018 (LGPD), usando-os apenas para a finalidade deste contrato.

# CLÁUSULA 5 - DA CONFIDENCIALIDADE
5.1. As partes manterão sigilo sobre informações comerciais, preços, códigos e dados de clientes a que tiverem acesso, durante a vigência e por 2 (dois) anos após o término.

# CLÁUSULA 6 - DA VIGÊNCIA E RESCISÃO
6.1. O contrato tem prazo indeterminado e pode ser rescindido por qualquer parte, mediante aviso escrito de 30 (trinta) dias.
6.2. Após a rescisão, o PARCEIRO mantém o direito às comissões sobre pagamentos recebidos pela EMPRESA até a data da rescisão, salvo se a rescisão decorrer de descumprimento por parte do PARCEIRO.

# CLÁUSULA 7 - DA ASSINATURA ELETRÔNICA
7.1. As partes concordam em firmar este contrato por meio eletrônico e reconhecem como válidos os registros de assinatura (data e hora, IP, dispositivo, código de confirmação enviado ao WhatsApp e hash do documento), nos termos do art. 10, §2º, da Medida Provisória nº 2.200-2/2001 e da Lei nº 14.063/2020.
7.2. O código de verificação {{CODIGO_VERIFICACAO}} permite conferir a autenticidade do documento na página pública de verificação.

# CLÁUSULA 8 - DO FORO
8.1. Firmado em {{DATA_CONTRATO}}. Fica eleito o foro da comarca da sede da EMPRESA, com renúncia a qualquer outro.
""";

    public const string SystemTemplateName = "Licença de sistema + implantação + mensalidade (modelo)";

    /// <summary>Starting point only: must be reviewed by the company's lawyer before use.</summary>
    public const string SystemTemplate =
"""
# CONTRATO DE LICENÇA DE USO DE SISTEMA E PRESTAÇÃO DE SERVIÇOS

CONTRATADA: {{EMPRESA_NOME}}, inscrita no CNPJ {{EMPRESA_CNPJ}}.
CONTRATANTE: {{CLIENTE_NOME}}, inscrito(a) no CPF/CNPJ {{CLIENTE_CPF_CNPJ}}, com endereço em {{CLIENTE_ENDERECO}} e e-mail {{CLIENTE_EMAIL}}.

# CLÁUSULA 1 - DO OBJETO
1.1. O presente contrato tem por objeto a licença de uso, em modalidade de assinatura (SaaS), e a implantação do seguinte sistema/serviço: {{SERVICO}}.
1.2. A implantação compreende a configuração inicial, parametrização e treinamento básico, conforme combinado entre as partes.

# CLÁUSULA 2 - DAS CONDIÇÕES FINANCEIRAS
{{CONDICOES_FINANCEIRAS}}
2.1. Os valores serão cobrados por meio de PIX ou boleto emitidos pela CONTRATADA, com os vencimentos acima.
2.2. O atraso no pagamento implica multa de 2% (dois por cento) sobre o valor devido, juros de mora de 1% (um por cento) ao mês e correção monetária pelo IPCA, calculados pro rata die.
2.3. A mensalidade será reajustada a cada 12 (doze) meses pela variação acumulada do IPCA, ou outro índice que venha a substituí-lo.
2.4. O atraso superior a 15 (quinze) dias autoriza a CONTRATADA a suspender o acesso ao sistema até a regularização, sem prejuízo da cobrança dos valores devidos.

# CLÁUSULA 3 - DA VIGÊNCIA E RESCISÃO
3.1. O contrato vigora a partir da data de assinatura, pelo prazo de mensalidade indicado na Cláusula 2 ({{PRAZO_MESES}} mês(es)), renovando-se automaticamente por períodos iguais caso o prazo seja determinado e nenhuma das partes se manifeste em contrário.
3.2. Qualquer das partes poderá rescindir o contrato mediante aviso prévio escrito de 30 (trinta) dias, permanecendo devidos os valores vencidos e os proporcionais ao período de uso.
3.3. Valores de implantação já executada não são reembolsáveis, salvo descumprimento da CONTRATADA.

# CLÁUSULA 4 - DAS OBRIGAÇÕES DA CONTRATADA
4.1. Disponibilizar o sistema com nível de serviço razoável, prestar suporte nos horários comerciais e manter rotinas de backup.
4.2. Adotar medidas técnicas e administrativas de segurança para proteger os dados tratados.

# CLÁUSULA 5 - DAS OBRIGAÇÕES DA CONTRATANTE
5.1. Efetuar os pagamentos nas datas acordadas, fornecer as informações necessárias à implantação e utilizar o sistema de forma lícita.
5.2. Manter sigilo sobre credenciais de acesso, responsabilizando-se pelos atos praticados com elas.

# CLÁUSULA 6 - DA PROTEÇÃO DE DADOS (LGPD)
6.1. As partes se comprometem a tratar dados pessoais em conformidade com a Lei nº 13.709/2018 (LGPD). Em relação aos dados dos clientes finais da CONTRATANTE inseridos no sistema, a CONTRATANTE atua como controladora e a CONTRATADA como operadora, tratando-os apenas conforme as instruções da CONTRATANTE e para a execução deste contrato.
6.2. A CONTRATADA comunicará à CONTRATANTE incidentes de segurança que possam acarretar risco relevante aos titulares, em prazo razoável.

# CLÁUSULA 7 - DA PROPRIEDADE INTELECTUAL E CONFIDENCIALIDADE
7.1. O sistema e seu código permanecem de propriedade da CONTRATADA, sendo concedida à CONTRATANTE apenas licença de uso, não exclusiva e intransferível, durante a vigência.
7.2. As partes manterão sigilo sobre as informações confidenciais a que tiverem acesso, durante a vigência e por 2 (dois) anos após o seu término.

# CLÁUSULA 8 - DA ASSINATURA ELETRÔNICA
8.1. As partes concordam que este contrato seja firmado por meio eletrônico e reconhecem como válidos, para comprovar autoria e integridade, os registros de assinatura (data e hora, IP, dispositivo, código de confirmação enviado ao WhatsApp e hash do documento), nos termos do art. 10, §2º, da Medida Provisória nº 2.200-2/2001 e da Lei nº 14.063/2020.
8.2. O código de verificação {{CODIGO_VERIFICACAO}} permite conferir a autenticidade do documento na página pública de verificação.

# CLÁUSULA 9 - DISPOSIÇÕES GERAIS E FORO
9.1. Este contrato é firmado em {{DATA_CONTRATO}}, constituindo o acordo integral entre as partes. Alterações exigem forma escrita.
9.2. Fica eleito o foro da comarca da sede da CONTRATADA para dirimir quaisquer controvérsias, com renúncia a qualquer outro, ressalvadas as normas de competência de ordem pública.
""";

    public const string DefaultTemplate =
"""
# CONTRATO DE PRESTAÇÃO DE SERVIÇOS

Pelo presente instrumento, de um lado {{EMPRESA_NOME}}, inscrita no CNPJ {{EMPRESA_CNPJ}}, doravante CONTRATADA, e de outro {{CLIENTE_NOME}}, inscrito(a) no CPF/CNPJ {{CLIENTE_CPF_CNPJ}}, com endereço em {{CLIENTE_ENDERECO}} e e-mail {{CLIENTE_EMAIL}}, doravante CONTRATANTE, têm entre si justo e contratado o que segue.

# CLÁUSULA 1 - DO OBJETO
A CONTRATADA prestará ao CONTRATANTE o seguinte serviço: {{SERVICO}}.

# CLÁUSULA 2 - DO VALOR E PAGAMENTO
Pelos serviços, o CONTRATANTE pagará à CONTRATADA o valor de {{VALOR}}, com vencimento em {{VENCIMENTO}}.

# CLÁUSULA 3 - DISPOSIÇÕES GERAIS
Este contrato é firmado em {{DATA_CONTRATO}} e poderá ser assinado eletronicamente, com registro de evidências de autoria e integridade.
""";

    private static async Task SeedDemoAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Leads.AnyAsync(l => l.Notes == "[demo]", ct)) return;
        var rnd = new Random(42);
        var src = await db.LeadSources.ToListAsync(ct);
        var segs = new[] { "Loja de roupas", "Restaurante", "Clínica", "Academia", "Oficina" };
        var cities = new[] { "Goiânia", "Anápolis", "Brasília", "São Paulo" };
        for (var i = 1; i <= 40; i++)
        {
            var l = new Lead
            {
                Name = $"Contato Demo {i}", CompanyName = $"{segs[i % segs.Length]} Demo {i}", Phone = $"(62) 3{rnd.Next(100, 999)}-{rnd.Next(1000, 9999)}",
                City = cities[i % cities.Length], State = "GO", Segment = segs[i % segs.Length], SourceId = src[i % src.Count].Id,
                Status = (LeadStatus)(i % 7), Notes = "[demo]", Address = $"Rua {i}, {i * 10}", CreatedAt = DateTime.UtcNow.AddDays(-(i % 20)).AddHours(-i % 5)
            };
            LeadService.ApplyKeys(l);
            db.Leads.Add(l);
        }
        var customers = new List<Customer>();
        for (var i = 1; i <= 5; i++)
            customers.Add(new Customer { Name = $"Cliente Demo {i} Ltda", Document = $"00.000.00{i}/0001-0{i}", Email = $"cliente{i}@demo.example", WhatsApp = $"5562999{i:D6}", City = "Goiânia", State = "GO" });
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync(ct);
        var statuses = new[] { ChargeStatus.Pago, ChargeStatus.Pendente, ChargeStatus.Vencido, ChargeStatus.Pago, ChargeStatus.Pendente };
        for (var i = 0; i < customers.Count; i++)
            db.Charges.Add(new Charge
            {
                CustomerId = customers[i].Id, Amount = 500 * (i + 1), ServiceName = "Mensalidade demo", Status = statuses[i], DueDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(statuses[i] == ChargeStatus.Vencido ? -5 : 7), DateTimeKind.Utc),
                PaidAt = statuses[i] == ChargeStatus.Pago ? DateTime.UtcNow.AddDays(-1) : null, PaidAmount = statuses[i] == ChargeStatus.Pago ? 500 * (i + 1) : null
            });
        var server = new Server { Name = "srv-local", Host = "localhost", Type = "Aplicação", Notes = "Demo" };
        server.Services.Add(new MonitoredService { Name = "SAMOS CONTROL (health)", Kind = ServiceKind.Http, Url = "http://localhost:8080/health", Port = 8080 });
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
    }
}
