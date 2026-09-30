# Idempotência (IDP)

Header `Idempotency-Key` na API e chaves idempotentes enviadas ao provedor.
Código: `src/PaymentGateway.Domain/Entities/IdempotencyRecord.cs`, `src/PaymentGateway.Data/AppDbContext.cs`,
`src/PaymentGateway.Services/PaymentService.cs` (AuthorizeAsync, ValidateAndStoreOperationKeyAsync), `Api/Controllers/PaymentsController.cs`.

- **IDP-01** (D) `Idempotency-Key` é obrigatória em toda chamada que altera pagamento (criar, capture, void, refund); ausente ou vazia → 400. Máximo de 100 caracteres (acima → 422). Fonte: README §Endpoints; PaymentsController.cs:33-34, 114-120; PaymentService.cs:520-524.
- **IDP-02** (I) A obrigatoriedade é aplicada no controller; o `PaymentService` aceita chave nula (usado em testes e chamadas internas). Porquê: consequência, não regra de negócio — nova rota que altere pagamento deve validar a chave no controller. Fonte: PaymentService.cs:88, 486-487; PaymentsController.cs:33-34.
- **IDP-03** (D) A chave é única por lojista (índice único `MerchantId + IdempotencyKey`). A mesma chave com outros dados ou em outra operação é rejeitada (422). Fonte: README §O que está implementado; AppDbContext.cs:71-80; PaymentService.cs:526-530; testes Authorize_ReusedKeyWithDifferentPayload_IsRejectedBeforeSecondProviderCall, FinancialOperation_ReusedKeyForDifferentOperation_IsRejected. ⚠ ver PEN-06.
- **IDP-04** (I) Na criação, a comparação usa hash SHA-256 de valor, moeda (normalizada em maiúsculas), descrição e `paymentMethodId`; nas operações, hash de `operação:paymentId`. Fonte: PaymentService.cs:66-69, 489.
- **IDP-05** (D) Criação nova responde 201; repetição idempotente da criação responde 200 com o mesmo pagamento. Fonte: README §Endpoints; PaymentsController.cs:38-41; teste Idempotencia_MesmaChave_NaoCriaSegundoPagamento.
- **IDP-06** (D) Autorização Stripe repetida que ainda está pendente sem `ProviderPaymentId` há ≥ 23 h NÃO é reenviada à Stripe (422 orientando aguardar webhook ou reconciliar manualmente). Porquê: a chave idempotente da Stripe pode ter expirado (~24 h) e a repetição criaria outra cobrança. Fonte: README §Stripe Test Mode; PaymentService.cs:118-123; teste OldUnresolvedStripeAuthorizationDoesNotCreateAnotherIntentOnRetry.
- **IDP-07** (I) Corrida entre duas requisições com a mesma chave: o índice único faz uma falhar no `SaveChanges`; a perdedora relê o registro vencedor e segue como repetição (ou rejeita se os dados divergirem). Fonte: PaymentService.cs:98-111, 506-517.
- **IDP-08** (I) Validação de entrada (PaymentMethod, valor, moeda) acontece antes de gravar o registro de idempotência; entrada inválida não consome a chave. Fonte: PaymentService.cs:59-70; teste UnsupportedPaymentMethodDoesNotLeavePendingPaymentOrConsumeKey.
- **IDP-09** (D) As chamadas ao provedor usam chaves próprias, estáveis por pagamento + operação, independentes da chave do cliente. Ver PRV-08. Fonte: README §O que está implementado.
- **IDP-10** (I) Registros de idempotência não expiram nem são limpos pela aplicação. Fonte: IdempotencyRecord.cs; ausência de rotina de limpeza em src/.
