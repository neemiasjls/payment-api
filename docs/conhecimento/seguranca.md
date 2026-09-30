# Segurança (SEG)

Cabeçalhos HTTP, HTTPS, tratamento de erros, segredos e resultado da auditoria de segurança.
Código: `src/PaymentGateway.Api/Middleware/SecurityHeadersMiddleware.cs`, `Api/Middleware/ErrorHandlingMiddleware.cs`,
`Api/Program.cs` (HSTS/HTTPS, validação `sk_test_`/`whsec_`), `.env.example`, `.dockerignore`.
Regras de API key e rate limit em [acesso.md](acesso.md); anti-SSRF em [webhooks-lojista.md](webhooks-lojista.md).

## Cabeçalhos e transporte

- **SEG-01** (I) Toda resposta leva `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY` e `Referrer-Policy: no-referrer`. Fora de `/swagger` também `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` e `Cache-Control: no-store`. Porquê: a API só devolve JSON com dados de pagamento; o Swagger UI precisa carregar scripts. Fonte: SecurityHeadersMiddleware.cs:19-38; teste Respostas_TrazemHeadersDeSeguranca.
- **SEG-02** (I) HSTS + redirecionamento HTTPS ativos fora de Development/Testing; nesses dois ambientes a API roda em HTTP. Fonte: Program.cs:156-163.
- **SEG-03** (I) O header `Server` do Kestrel é removido; toda resposta leva `X-Gateway-Mode: sandbox`. Fonte: Program.cs:32, 166-170.

## Erros

- **SEG-04** (I) Erros viram ProblemDetails (RFC 7807): `NotFoundException` → 404, `DomainException` → 422, recusa Stripe → 422, outra falha Stripe → 502, qualquer outra exceção → 500 com mensagem genérica; detalhes só no log. Fonte: ErrorHandlingMiddleware.cs:22-57.
- **SEG-05** (I) Mensagens de erro de rede dos webhooks e da Stripe não repassam host, URL ou exceção interna ao cliente. Fonte: WebhookDispatcher.cs:203-207; ErrorHandlingMiddleware.cs:43-48.

## Segredos

- **SEG-06** (D) Segredos só por variáveis de ambiente (`Gateway__Processor`, `Stripe__SecretKey`, `Stripe__WebhookSecret`) ou pelo `.env` do Compose (`GATEWAY_PROCESSOR`, `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`). `.env` é ignorado pelo Git e pelo contexto da imagem; nunca versionar chaves. Fonte: README §Stripe Test Mode; .env.example:1-6; .dockerignore:6-14; docker-compose.yml:11-13.
- **SEG-07** (D) Nunca usar chave secreta de produção (live): a inicialização exige `sk_test_` (ver PRV-05). Fonte: .env.example:3; README §Stripe Test Mode.
- **SEG-08** (D) A chave demo `pk_test_demo_1234567890abcdef` é pública por design e só existe em Development (ver ACS-07). Fonte: README §Executar localmente.

## Auditoria de segurança (2026-09-28)

- **SEG-09** (C) Resultado da auditoria de 20 itens: não se aplicam — item "Public Key DB" (o SQLite não é exposto), cookies (autenticação sem estado por header), uploads (só JSON); parciais — criptografia em repouso (`WebhookSecret` em texto puro, ver WHK-10) e proteção contra bots (só rate limit); corrigidos no commit 15dc9df — cabeçalhos de segurança, HTTPS/HSTS e varredura de dependências. Fonte: usuário 2026-09-28.
