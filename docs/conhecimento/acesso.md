# Acesso: lojistas, API keys e limite de requisições (ACS)

Autenticação de lojistas, cadastro, lojista demo e rate limiting.
Código: `src/PaymentGateway.Domain/Entities/Merchant.cs`, `src/PaymentGateway.Services/MerchantService.cs`,
`Services/Security/ApiKeyHasher.cs`, `src/PaymentGateway.Api/Middleware/ApiKeyMiddleware.cs`, `Api/Setup/DbSeeder.cs`,
`Api/Controllers/MerchantsController.cs`, rate limiter em `Api/Program.cs`.

## API key

- **ACS-01** (I) Autenticação pelo header `X-Api-Key` em todas as rotas de lojista e pagamento; ausente ou inválida → 401 (ProblemDetails). O lojista autenticado fica em `HttpContext.Items`. Fonte: ApiKeyMiddleware.cs:14-53; README §Endpoints.
- **ACS-02** (I) Chave = `pk_` + 32 bytes aleatórios criptográficos em hexadecimal, devolvida uma única vez no cadastro. Só o hash SHA-256 é gravado (índice único). Fonte: ApiKeyHasher.cs:13-25; MerchantService.cs:50-59; AppDbContext.cs:25-26; teste Register_RetornaApiKey_EArmazenaApenasOHash.
- **ACS-03** (C) SHA-256 é adequado para guardar a API key porque ela é aleatória e de alta entropia; seria inadequado para senhas escolhidas por pessoas. Fonte: usuário 2026-09-28 (auditoria de segurança).
- **ACS-04** (I) Rotas públicas (sem API key): `/`, `/health`, `/swagger*`, `POST /api/v1/webhooks/stripe` e, só em Development/Testing, `POST /api/v1/merchants`. Fonte: ApiKeyMiddleware.cs:56-75; teste PublicRoutes_AreRestrictedToDemoRegistrationAndStripeWebhook.

## Cadastro e lojista demo

- **ACS-05** (D) Cadastro anônimo (`POST /api/v1/merchants`) só em Development/Testing; em outros ambientes → 403. Fora da demo, lojistas precisam de provisionamento administrativo (não implementado). Fonte: README §Executar localmente; MerchantsController.cs:30-36; teste Registration_IsForbiddenOutsideDemoEnvironments.
- **ACS-06** (I) E-mail de lojista é único (verificação + índice único) → duplicado = 422. Fonte: MerchantService.cs:46-48; AppDbContext.cs:27.
- **ACS-07** (D) Lojista demo (`pk_test_demo_1234567890abcdef`, chave pública por design) é semeado só em Development e só se não houver nenhum lojista. Fonte: README §Executar localmente; DbSeeder.cs:13-27; Program.cs:147-154.
- **ACS-08** (D) `webhookUrl` é opcional no cadastro; se informado, o `webhookSecret` é devolvido uma única vez. Regras da URL em [webhooks-lojista.md](webhooks-lojista.md). Fonte: README §Webhooks do lojista; MerchantService.cs:33-51.

## Limite de requisições

- **ACS-09** (D) Rate limit global de `Gateway:RateLimitPerMinute` (padrão 100) requisições por minuto POR IP de origem, janela fixa, sem fila; excesso → 429 (ProblemDetails). Histórico em DEC-08. ⚠ ver PEN-01. Fonte: README §O que está implementado; Program.cs:81-111; appsettings.json:7; teste Invalid_api_keys_share_the_source_ip_rate_limit.
- **ACS-10** (I) O rate limiter roda antes da autenticação, então chaves inválidas também consomem o limite do IP. Fonte: Program.cs:173, 181.
