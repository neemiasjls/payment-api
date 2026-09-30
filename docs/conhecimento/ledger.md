# Ledger e saldo (LED)

Razão contábil de partidas dobradas, taxa do gateway e saldo do lojista.
Código: `src/PaymentGateway.Domain/Entities/LedgerEntry.cs`, `Domain/Enums/LedgerAccount.cs`, `Domain/Enums/LedgerEntryType.cs`,
`src/PaymentGateway.Services/GatewayOptions.cs`, `PaymentService.cs` (AddCaptureEntries, AddRefundEntries), `MerchantService.cs` (GetBalanceAsync).

- **LED-01** (D) Ledger de partidas dobradas: em cada movimento, soma dos débitos = soma dos créditos. O saldo do lojista nunca é um campo mutável; é sempre derivado dos lançamentos (créditos − débitos em `MerchantPayable`). Porquê: rastreabilidade de cada centavo. Fonte: LedgerEntry.cs:5-10; LedgerAccount.cs:3-6; MerchantService.cs:74-90.
- **LED-02** (I) Contas: `AcquirerCash` (dinheiro que entra na captura), `MerchantPayable` (a repassar ao lojista), `AcquirerRevenue` (taxa do gateway). Tipos: `Debit`/`Credit`, gravados como texto. Fonte: LedgerAccount.cs:7-17; AppDbContext.cs:53-54.
- **LED-03** (I) Captura gera 3 lançamentos: débito `AcquirerCash` = valor; crédito `MerchantPayable` = valor − taxa; crédito `AcquirerRevenue` = taxa. Fonte: PaymentService.cs:443-454; teste Capture_PagamentoAutorizado_GeraLedgerBalanceadoESaldo.
- **LED-04** (I) Taxa = `Gateway:FeeBps` (padrão 250 bps = 2,5%, próximo do MDR médio de cartão de crédito no Brasil); aritmética inteira `valor * bps / 10000` com truncamento e `checked`. Valor fora de 0–10000 impede criar o serviço. Fonte: GatewayOptions.cs:8-12; appsettings.json:6; PaymentService.cs:52-53, 445.
- **LED-05** (I) Estorno gera os 3 lançamentos inversos usando a taxa GRAVADA na captura, não a configuração atual. Porquê: mudar `FeeBps` entre captura e estorno não pode desbalancear o lojista. Fonte: PaymentService.cs:456-480; teste RefundReversesCapturedFeeEvenAfterConfigurationChanges.
- **LED-06** (I) Void não gera lançamentos (nada foi capturado). Recusa (Declined) também não. Fonte: PaymentService.cs:385-390; teste Void_PagamentoAutorizado_CancelaSemMexerNoLedger.
- **LED-07** (I) Ledger duplicado é impedido pelo token `Version` do pagamento e pelas transições da entidade (lançamentos só são adicionados na mesma gravação da transição Authorized→Captured ou Captured→Refunded). Não existe índice único em `LedgerEntries`; os índices únicos protegem as chaves de idempotência (IDP-03) e `Provider + ProviderPaymentId`. Fonte: AppDbContext.cs:51-58; PaymentService.cs:381-384, 403-407; testes ConcurrentCapture_WithSeparateContexts_WritesLedgerOnce, ConcurrentRefund_WithSeparateContexts_WritesReversalOnce.
- **LED-08** (D) Lançamentos são imutáveis (só inserção; correção = lançamento inverso). Fonte: LedgerEntry.cs:5-6.
- **LED-09** (D) Saldos, taxas e lançamentos são demonstrativos: não há repasse real, split nem onboarding de lojistas; saldo sempre em BRL. Fonte: README (aviso de Escopo); MerchantService.cs:89.
