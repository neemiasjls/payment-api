# Provedores de pagamento (PRV)

Abstração de provedor (`fake` | `stripe`), Stripe Test Mode, webhook de entrada da Stripe e reconciliação.
Código: `src/PaymentGateway.Services/Acquiring/*`, `Services/Reconciliation/*`,
`src/PaymentGateway.Api/Controllers/StripeWebhooksController.cs`, `Domain/Entities/ProviderWebhookEvent.cs`.
Passo a passo de configuração: README §Stripe Test Mode (não duplicado aqui).

## Abstração

- **PRV-01** (D) `IPaymentProcessor` separa o domínio do provedor; `Gateway:Processor` escolhe `fake` (padrão) ou `stripe`; outro valor falha na inicialização. Porquê: rodar e testar sem conta Stripe. Fonte: README §O que está implementado; Program.cs:47-67; IPaymentProcessor.cs:35-53.
- **PRV-02** (D) Cada pagamento grava `Provider` e `ProviderPaymentId`; operar um pagamento de outro provedor que o configurado é rejeitado (422). Trocar `fake` ↔ `stripe` exige banco novo. Fonte: README §Stripe Test Mode; PaymentService.cs:546-551; teste SwitchingProcessorCannotSendExistingPaymentToWrongProvider.
- **PRV-03** (D) O `fake` guarda estado em memória (`ConcurrentDictionary`): após reiniciar, autorizações `fake` que ficaram no SQLite podem não ser capturáveis/canceláveis. Para testar retomada entre reinícios, usar Stripe Test Mode. Fonte: README §Stripe Test Mode; FakePaymentProcessor.cs:9.
- **PRV-04** (I) Resultados de recusa do `fake` são determinísticos por PaymentMethod (`generic_decline`, `insufficient_funds`, `expired_card`); `pm_card_authenticationRequired` gera RequiresAction. Fonte: AcquirerSimulator.cs:9-15; FakePaymentProcessor.cs:24-28.

## Stripe Test Mode

- **PRV-05** (D) Stripe só com chave `sk_test_` e segredo `whsec_` não vazios; caso contrário a aplicação não sobe. O adaptador revalida a chave. Porquê: impossibilitar o uso de chave de produção (live). Fonte: README §Stripe Test Mode; Program.cs:53-63; StripePaymentProcessor.cs:243-250.
- **PRV-06** (D) Qualquer PaymentIntent ou evento com `livemode` verdadeiro é rejeitado, mesmo com chave de teste. Fonte: README §Stripe Test Mode; StripePaymentProcessor.cs:237-241; StripeWebhooksController.cs:59-60.
- **PRV-07** (D) Autorização = PaymentIntent confirmado com `capture_method=manual`; `requires_capture` → Authorized, `succeeded` → Captured, `canceled` → Voided, `requires_payment_method` → Declined. Void = cancelar o intent; estorno = `Refund` total. Fonte: README §O que está implementado; StripePaymentProcessor.cs:42-57, 208-216.
- **PRV-08** (D) Chaves idempotentes enviadas à Stripe são estáveis por pagamento + operação (`<provedor>:<id>:<operação>`, com hash SHA-256 no adaptador). Porquê: repetir a chamada nunca cria segunda cobrança. Fonte: README §O que está implementado; PaymentService.cs:543-544; StripePaymentProcessor.cs:265-270.
- **PRV-09** (I) Todo resultado da Stripe é conferido contra o pagamento local (metadata `gateway_payment_id`, valor e moeda); divergência → 422 e nada é gravado. Fonte: PaymentService.cs:553-573; teste ForeignStripeIntentCannotBindOrWriteLedger.
- **PRV-10** (I) A Stripe mantém o intent em `succeeded` após estorno; o adaptador soma os refunds (paginando) para decidir Refunded/Pending/RequiresAction. Um refund `succeeded` só vira Refunded quando o total estornado aparece no intent. Fonte: StripePaymentProcessor.cs:125-132, 151-199; testes Stripe_GetSumsRefundsAcrossPagesBeforeReportingFullRefund, Stripe_RefundSuccessRemainsPendingUntilFullRefundIsVisible.
- **PRV-11** (I) Recusa de cartão da Stripe vira 422 com o código de recusa; outras falhas da Stripe viram 502 com orientação para consultar o pagamento antes de repetir. Fonte: ErrorHandlingMiddleware.cs:36-48.

## Webhook de entrada da Stripe

- **PRV-12** (D) `POST /api/v1/webhooks/stripe` é público (sem `X-Api-Key`), autenticado pela assinatura `Stripe-Signature` sobre o corpo bruto (tolerância 300 s, corpo ≤ 64 KB); responde 404 se o provedor não for `stripe`. Fonte: README §Endpoints; StripeWebhooksController.cs:28-46; ApiKeyMiddleware.cs:68-70.
- **PRV-13** (D) O evento não é aplicado diretamente: o gateway deduplica pelo `EventId` (tabela `ProviderWebhookEvents`, PK provedor+evento) e consulta o estado atual do PaymentIntent no provedor antes de atualizar. Porquê: eventos chegam fora de ordem e repetidos. Fonte: README §O que está implementado; PaymentService.cs:244-261; AppDbContext.cs:82-88; teste ProviderWebhook_ReconcilesCurrentProviderStateAndDeduplicatesEvents.
- **PRV-14** (I) Tipos tratados: `payment_intent.*` (usa `data.object.id` e metadata) e `refund.*`/`charge.refunded` (usa `payment_intent`). Evento de pagamento desconhecido é ignorado com 200 (pode ser de outra integração da mesma conta). Fonte: StripeWebhooksController.cs:77-102; PaymentService.cs:256-257.

## Reconciliação periódica

- **PRV-15** (D) Só com Stripe ativo, um worker roda ao iniciar e a cada `PaymentReconciliation:Interval` (padrão 5 min), em lotes de `BatchSize` (padrão 50, aceito 1–1000). Fonte: README §Stripe Test Mode; Program.cs:63; PaymentReconciliationOptions.cs:8-9; PaymentReconciliationWorker.cs:33-36.
- **PRV-16** (I) Candidatos: pagamentos `stripe` com `ProviderPaymentId` em Pending, RequiresAction, Authorized ou Captured. Cursor estável (CreatedAtUtc, Id) percorre todos e recomeça no fim; cada item usa escopo próprio para uma falha não contaminar os demais. Fonte: PaymentReconciliationWorker.cs:103-167; teste Sweep_rotates_through_tied_timestamps_and_continues_after_one_failure.
- **PRV-17** (D) Autorização Stripe pendente sem `ProviderPaymentId` não é recuperada pelo worker; ver IDP-06 para a regra de 23 h. Fonte: README §Stripe Test Mode.
