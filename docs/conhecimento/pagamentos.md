# Pagamentos (PAG)

Ciclo de vida do pagamento: validação de entrada, estados, transições e isolamento por lojista.
Código: `src/PaymentGateway.Domain/Entities/Payment.cs`, `Domain/Enums/PaymentStatus.cs`,
`src/PaymentGateway.Services/PaymentService.cs`, `src/PaymentGateway.Api/Controllers/PaymentsController.cs`.
Idempotência em [idempotencia.md](idempotencia.md); lançamentos em [ledger.md](ledger.md).

## Entrada

- **PAG-01** (D) Valor inteiro entre 50 e 99.999.999 centavos; só `BRL`. Porquê: limites da demonstração e do provedor de testes. Fonte: README §Endpoints; Payment.cs:52-55; PaymentService.cs:60-63.
- **PAG-02** (D) A API nunca recebe número de cartão (PAN) nem CVV; só `paymentMethodId`. Cartão aparece apenas como `CardLast4`/`CardBrand` devolvidos pelo provedor. Porquê: fora de escopo PCI; não há interface de captura de cartão. Fonte: README §O que está implementado; Payment.cs:24-26.
- **PAG-03** (D) Só PaymentMethods de teste de uma allowlist pequena (`pm_card_visa`, `pm_card_visa_chargeDeclined`, `pm_card_authenticationRequired`…); qualquer outro vira 422 antes de gravar algo. Porquê: sandbox sem dados reais. Fonte: README §Roteiro de demonstração; TestPaymentMethods.cs:8-21; teste UnsupportedPaymentMethodDoesNotLeavePendingPaymentOrConsumeKey.

## Estados e transições

- **PAG-04** (I) Estados: `Pending`, `RequiresAction`, `Authorized`, `Captured`, `Voided`, `Refunded`, `Declined`, gravados como texto no banco. Porquê: texto é legível e estável se a ordem do enum mudar. Fonte: PaymentStatus.cs:10-19; AppDbContext.cs:43-45.
- **PAG-05** (I) Transições permitidas: Pending/RequiresAction → Pending/RequiresAction/Authorized/Declined; Authorized → Captured, Voided ou Declined (recusa tardia do provedor); Captured → Refunded. Voided, Refunded e Declined são finais. Porquê: a entidade controla as transições, independente de quem chama. Fonte: Payment.cs:68-84, 104-112, 115-154.
- **PAG-06** (I) Na reconciliação, se o provedor já estiver em Captured/Voided/Refunded enquanto o local está Pending, o pagamento passa por Authorized e pelas transições intermediárias, gerando os lançamentos de captura/estorno. Porquê: nunca pular regras da entidade nem o ledger. Fonte: PaymentService.cs:351-375.
- **PAG-07** (D) `Pending` e `RequiresAction` não contam como autorização concluída; não podem ser capturados. `RequiresAction` pode devolver `clientSecret`. Fonte: README §O que está implementado; teste Stripe_DoesNotTreatPendingOrActionAsAuthorized.
- **PAG-08** (D) Captura e void só a partir de `Authorized`; estorno só a partir de `Captured` e sempre total (sem estorno parcial). Fonte: README §Endpoints; Payment.cs:115-154; PaymentService.cs:169-171.
- **PAG-09** (I) Repetir captura/void/estorno de um pagamento que já está no estado-alvo devolve o estado atual sem nova chamada ao provedor. Porquê: retentativa do cliente é segura. Fonte: PaymentService.cs:157-164; teste Capture_DuasVezes_NaoDuplicaLedger.

## Confiabilidade

- **PAG-10** (I) Antes de chamar o provedor para captura/void/estorno, a intenção é gravada em `PendingOperation` (+ evento `payment.<op>_pending`). Se a resposta HTTP se perder, GET, webhook do provedor e reconciliação sabem qual operação concluir. Só uma operação pendente por vez. Fonte: PaymentService.cs:175-201; Payment.cs:86-102.
- **PAG-11** (I) Falha do provedor em operação pendente limpa `PendingOperation`, grava `LastOperationError` e emite `payment.<op>_failed`. Fonte: PaymentService.cs:395-398, 412-414, 420-426.
- **PAG-12** (I) `Payment.Version` é o token de concorrência (incrementado em toda transição) e funciona no SQLite, que não tem rowversion. Conflito → recarrega e reavalia (até 3 tentativas). Fonte: Payment.cs:32-33; AppDbContext.cs:39; PaymentService.cs:293-332.
- **PAG-13** (I) `GET /payments/{id}` consulta o provedor quando o pagamento está Pending/RequiresAction ou com operação pendente, e persiste o estado reconciliado. Fonte: PaymentService.cs:219-231; README §Endpoints.

## Isolamento

- **PAG-14** (I) Toda consulta e operação de pagamento filtra por `MerchantId` do lojista autenticado; pagamento de outro lojista responde 404 (não 403). Porquê: não revelar existência de recursos alheios. Fonte: PaymentService.cs:150-152, 268, 538-541; NotFoundException.cs:3; teste Get_PagamentoDeOutroLojista_LancaNotFound.
- **PAG-15** (I) Listagem paginada: `page` ≥ 1, `pageSize` entre 1 e 100 (padrão 20), ordem por criação decrescente. Fonte: PaymentService.cs:263-277; PaymentsController.cs:45-55.
