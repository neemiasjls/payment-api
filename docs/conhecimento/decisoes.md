# Decisões de arquitetura (DEC)

Formato: contexto, decisão, porquê, consequência. Decisão substituída continua registrada com "substituída por".

### DEC-01 SQLite para demonstração de instância única — vigente
Contexto: portfólio que precisa rodar com um comando, sem servidor de banco.
Decisão: SQLite via EF Core com migrations; arquivo local ou volume Docker.
Porquê: zero infraestrutura, mas com transações, índices únicos e persistência entre reinícios.
Consequência: sem rowversion, concorrência via `Version` (ver PAG-12); implantação distribuída exigiria outro banco. Confiança: (D). Fonte: README §Testes e engenharia.

### DEC-02 Dinheiro em centavos (long) — vigente
Contexto: valores monetários e cálculo de taxa.
Decisão: todo valor é `long` em centavos; taxa em basis points com aritmética inteira.
Porquê: evita erro de arredondamento de ponto flutuante.
Consequência: truncamento explícito na taxa (LED-04). Confiança: (D). Fonte: Payment.cs:16-20; GatewayOptions.cs:8-12.

### DEC-03 Ledger de partidas dobradas com saldo derivado — vigente
Contexto: saldo do lojista e receita do gateway.
Decisão: lançamentos imutáveis em três contas; saldo = soma dos lançamentos.
Porquê: rastreabilidade e impossibilidade de "perder" saldo por atualização concorrente de um campo.
Consequência: estorno gera lançamentos inversos com a taxa gravada (LED-05). Confiança: (D). Fonte: LedgerEntry.cs:5-10; MerchantService.cs:76-77.

### DEC-04 Provedor abstraído (fake | stripe) — vigente
Contexto: demonstrar integração real sem exigir conta de terceiros.
Decisão: `IPaymentProcessor` com simulador em memória e adaptador Stripe; escolha por `Gateway:Processor`.
Porquê: projeto e testes rodam sem credenciais; Stripe prova integração com provedor real.
Consequência: pagamentos amarrados ao provedor; trocar exige banco novo (PRV-02). Confiança: (D). Fonte: README §O que está implementado.

### DEC-05 Stripe Test Mode com captura manual — vigente
Contexto: fluxo autorizar → capturar/cancelar → estornar.
Decisão: PaymentIntent com `capture_method=manual`, somente `sk_test_`, eventos `livemode` rejeitados.
Porquê: separa autorização de captura como em adquirência; impede uso acidental de dinheiro real.
Consequência: sem interface 3D Secure, `RequiresAction` não conclui no roteiro. Confiança: (D). Fonte: README §O que está implementado, §Roteiro de demonstração.

### DEC-06 Sem PAN/CVV: só paymentMethodId de teste — vigente
Contexto: API de pagamentos pública em portfólio.
Decisão: a API aceita apenas identificadores `pm_card_...` de uma allowlist; nunca número de cartão ou CVV.
Porquê: fora do escopo PCI DSS; nada sensível trafega ou é gravado.
Consequência: sem interface de captura de cartão. Confiança: (D). Fonte: README §O que está implementado; TestPaymentMethods.cs.

### DEC-07 Outbox para webhooks do lojista — vigente
Contexto: notificar o lojista sem perder eventos em reinício ou falha.
Decisão: evento gravado na mesma transação da mudança de estado; dispatcher com lease e até 5 tentativas.
Porquê: consistência entre estado e notificação; retomada após reinício; sem entrega dupla entre instâncias.
Consequência: entrega pelo menos uma vez; o consumidor deduplica (WHK-06). Substituiu a entrega direta via fila em memória da versão inicial (I, pelo diff de 08bc9e3). Confiança: (D). Fonte: README §O que está implementado; commit 08bc9e3.

### DEC-08 Rate limit por IP — vigente (substitui a partição por API key)
Contexto: proteção contra abuso antes da autenticação.
Decisão: limite global de 100 req/min por IP de origem.
Porquê: com partição por API key, qualquer chave inventada criava um bucket novo e contornava o limite.
Consequência: clientes atrás do mesmo IP (NAT) dividem o limite. Antes: partição por API key, com IP como reserva (de 4f2b139, 2026-07-07, até 5cafae9, 2026-09-29). Confiança: (D). Fonte: Program.cs:81-99; teste Invalid_api_keys_share_the_source_ip_rate_limit.

### DEC-09 Cadastro anônimo e lojista demo só na demonstração — vigente
Contexto: facilitar o teste local sem abrir cadastro público.
Decisão: `POST /api/v1/merchants` e o seed demo só em Development/Testing.
Porquê: em outro ambiente, cadastro aberto permitiria criar lojistas sem controle.
Consequência: fora da demo, lojistas exigem provisionamento administrativo (não implementado). Confiança: (D). Fonte: README §Executar localmente.

### DEC-10 HTTPS/HSTS e cabeçalhos de segurança — vigente
Contexto: auditoria de segurança de 2026-09-28.
Decisão: cabeçalhos restritivos em todas as respostas; HSTS e redirecionamento HTTPS fora de Development/Testing; varredura de dependências no CI.
Porquê: defesas baratas para respostas com dados de pagamento.
Consequência: rotas /swagger ficam sem CSP e sem no-store; ambiente local roda em HTTP. Confiança: (D). Fonte: commit 15dc9df; SEG-01, SEG-02, SEG-09.
