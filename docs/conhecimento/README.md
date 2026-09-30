# Base de conhecimento do Payment Gateway API

Regras de negócio, decisões e convenções do projeto, organizadas por domínio. Complementa o
[README principal](../../README.md) (instruções de uso) com o porquê de cada comportamento.
Comece por esta tabela e leia só o arquivo do domínio da tarefa.

| Domínio | Arquivo | Código principal | Palavras-chave |
|---|---|---|---|
| Pagamentos | [pagamentos.md](pagamentos.md) (PAG) | Domain/Entities/Payment.cs, Services/PaymentService.cs, PaymentsController | estados, captura, void, estorno, PendingOperation, Version |
| Provedores | [provedores.md](provedores.md) (PRV) | Services/Acquiring, Services/Reconciliation, StripeWebhooksController | fake, stripe, PaymentIntent, livemode, webhook recebido da Stripe, reconciliação |
| Idempotência | [idempotencia.md](idempotencia.md) (IDP) | IdempotencyRecord, PaymentService, AppDbContext | Idempotency-Key, 201/200, 23 h |
| Ledger | [ledger.md](ledger.md) (LED) | LedgerEntry, Enums/Ledger*, GatewayOptions, MerchantService | taxa, FeeBps, MDR, saldo, partidas dobradas |
| Acesso | [acesso.md](acesso.md) (ACS) | Merchant, MerchantService, ApiKeyMiddleware, ApiKeyHasher, DbSeeder | X-Api-Key, cadastro, demo, rate limit |
| Webhooks do lojista | [webhooks-lojista.md](webhooks-lojista.md) (WHK) | Services/Webhooks, PaymentEvent | webhook enviado ao lojista, outbox, lease, retentativa, falha de entrega, HMAC, SSRF |
| Segurança | [seguranca.md](seguranca.md) (SEG) | SecurityHeadersMiddleware, ErrorHandlingMiddleware, Program.cs | headers, HTTPS, erros, segredos, auditoria |
| Operação | [operacao.md](operacao.md) (OPS) | Program.cs, appsettings, Dockerfile, compose, CI, *.ps1 | portas, migrations, CI, Docker, configuração |
| Convenções | [convencoes.md](convencoes.md) (CNV) | .editorconfig, tests/, git log | camadas, estilo, testes, commits |
| Decisões | [decisoes.md](decisoes.md) (DEC) | — | arquitetura, histórico de decisões |
| Glossário | [glossario.md](glossario.md) | — | termos de pagamentos |
| Pendências | [pendencias.md](pendencias.md) (PEN) | — | conflitos, dúvidas, limpezas |

Pendências abertas: 6. Última manutenção: 2026-09-30.

## Legenda de confiança

- (C) confirmado pelo responsável do projeto — fonte "usuário AAAA-MM-DD"
- (D) documentado — fonte "README §seção" ou comentário de decisão no código
- (I) inferido do código — fonte "arquivo:linha" ou nome de teste
- (?) hipótese ou pendente — também listado em pendencias.md como PEN-NN

## Como manter esta base

- Formato de regra (1–3 linhas): `- **PREFIXO-NN** (C|D|I|?) Enunciado. Porquê: … Fonte: …`.
- IDs são sequenciais por arquivo e nunca reutilizados; ao mudar uma regra, edite-a no lugar e,
  se o histórico tiver valor, anote "Antes: … (até AAAA-MM-DD) — motivo" ou crie uma DEC nova.
- Busque (grep) pelo prefixo e por palavras-chave antes de criar regra nova, para não duplicar.
- Não promova (I) para (C) sem confirmação explícita; não apague regra (C) sem confirmação.
- Comportamento suspeito no código vira pendência "possível bug", não regra.
- Conflitos entre fontes vão para pendencias.md; marque as regras afetadas com `⚠ ver PEN-NN`.
- Aponte arquivo:linha em vez de copiar código; não duplique o README principal, cite a seção.
- Nunca registre segredos; registre só o nome da variável onde são configurados.
- Limites: este índice ≤ 50 linhas; cada arquivo de domínio ≤ 200 linhas.
