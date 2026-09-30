# Webhooks enviados ao lojista (WHK)

Outbox de eventos, entrega com retentativa, assinatura e proteção contra SSRF.
Código: `src/PaymentGateway.Services/Webhooks/*` (WebhookDispatcher, IWebhookQueue, WebhookSigner, WebhookTargetValidator),
`src/PaymentGateway.Domain/Entities/PaymentEvent.cs`. Webhooks recebidos da Stripe ficam em [provedores.md](provedores.md).

## Outbox e entrega

- **WHK-01** (D) Eventos (`payment.authorized`, `payment.captured`, `payment.<op>_pending`, `payment.<op>_failed`…) são gravados em `PaymentEvents` no mesmo `SaveChanges` da mudança de estado (outbox). A fila em memória é só um sinal para acordar o dispatcher; perdê-lo não perde entrega. Fonte: README §O que está implementado; PaymentEvent.cs:3-6, 21-22; IWebhookQueue.cs:3-6; PaymentService.cs:420-441.
- **WHK-02** (I) O dispatcher varre o banco a cada 2 s (ou ao receber sinal), em lotes de 32, pegando eventos não entregues, não pulados, com tentativas < 5 e vencidos. Retoma pendências após reinício. Fonte: WebhookDispatcher.cs:19-22, 85-100; teste FailedDelivery_RetriesFromDatabaseAfterDispatcherRestart.
- **WHK-03** (I) Cada entrega é reservada por lease de 1 min via UPDATE condicional (`ExecuteUpdate` com token); só o dono do token finaliza a tentativa; lease órfão expira e permite nova tentativa. Impede entrega dupla entre instâncias concorrentes. Fonte: WebhookDispatcher.cs:125-144, 222-240; teste ConcurrentDispatchers_ClaimAnEventOnlyOnce.
- **WHK-04** (D) Até 5 tentativas com espera exponencial (2^n s); depois desiste, registra aviso no log e não há reenvio manual. Fonte: README §Testes e engenharia; WebhookDispatcher.cs:19, 226-245.
- **WHK-05** (I) Lojista sem `WebhookUrl`/segredo: o evento é marcado como pulado (`DeliverySkippedAtUtc`) e nunca mais tentado. Fonte: WebhookDispatcher.cs:151-155, 213-220; teste EventWithoutEndpoint_IsPersistentlySkipped.
- **WHK-06** (D) Entrega "pelo menos uma vez": o consumidor deve deduplicar por `X-Webhook-Event-Id` (ID do evento). Fonte: README §O que está implementado, §Webhooks do lojista; WebhookDispatcher.cs:191.
- **WHK-07** (I) Qualquer status HTTP 2xx conta como entregue; o erro gravado é genérico (`HTTP <código>`, `Timeout`, `NetworkError`, `UnsafeDestination`) para não guardar hosts/URLs internos. Timeout de 10 s. Fonte: WebhookDispatcher.cs:40-43, 182-207.

## Assinatura

- **WHK-08** (D) Header `X-Webhook-Signature: sha256=<hex>` = HMAC-SHA256 do corpo exato enviado, com o segredo do lojista. O lojista deve validar sobre o corpo recebido (sem reserializar) com comparação em tempo constante. Fonte: README §Webhooks do lojista; WebhookSigner.cs:17-24.
- **WHK-09** (D) Segredo do lojista = `whsec_` + 32 bytes aleatórios; é diferente do `whsec_` da Stripe (este valida o que o gateway RECEBE da Stripe; o do lojista, o que o gateway ENVIA). Fonte: README §Webhooks do lojista; WebhookSigner.cs:14-15.
- **WHK-10** (C) `WebhookSecret` fica em texto puro no banco porque o gateway precisa dele para assinar; é limitação conhecida (criptografia em repouso não implementada). Fonte: usuário 2026-09-28 (auditoria de segurança); Merchant.cs:17-21.

## Destino seguro (anti-SSRF)

- **WHK-11** (D) URL aceita: HTTPS, porta 443, hostname DNS público com ponto (sem IP literal, userinfo, fragmento, `.local`, `.internal`, `.test`, `.localhost`), até 500 caracteres no cadastro. Loopback HTTP(S) só em Development/Testing. Fonte: README §Webhooks do lojista; WebhookTargetValidator.cs:12-45; MerchantService.cs:36-43.
- **WHK-12** (D) Na conexão, o DNS é resolvido uma vez e todos os IPs são revalidados como públicos (bloqueia privados, link-local, CGNAT, faixas de documentação); conecta pelo IP aprovado mantendo o hostname para TLS. Sem seguir redirects e sem proxy. Fonte: README §Webhooks do lojista; WebhookTargetValidator.cs:47-125; WebhookDispatcher.cs:248-256; teste RedirectResponse_IsRecordedWithoutFollowingLocation.
- **WHK-13** (I) A URL é revalidada a cada entrega (não só no cadastro); falha → tentativa registrada como `UnsafeDestination`. Fonte: WebhookDispatcher.cs:157-162.
