# Glossário

- **Autorização**: reserva do valor no meio de pagamento do cliente, sem cobrar ainda. Estado `Authorized`.
- **bps (basis points)**: centésimo de ponto percentual; 1 bps = 0,01%, 250 bps = 2,5%. Usado em `Gateway:FeeBps`.
- **Captura**: confirmação da cobrança de uma autorização; é quando o dinheiro "entra" e o ledger é lançado. Estado `Captured`.
- **Estorno (refund)**: devolução ao cliente de um pagamento já capturado; aqui sempre total. Estado `Refunded`.
- **HMAC**: código de autenticação de mensagem com chave secreta (aqui HMAC-SHA256); prova origem e integridade do corpo do webhook.
- **Idempotência**: repetir a mesma requisição produz o mesmo efeito de uma única execução; controlada pelo header `Idempotency-Key`.
- **Lease**: reserva temporária (1 min) de um evento por um dispatcher; se ele morrer, a reserva expira e outro pode tentar.
- **Ledger / partidas dobradas**: razão contábil em que cada movimento tem débitos e créditos de mesmo total; o saldo é a soma dos lançamentos.
- **livemode**: campo da Stripe que indica objeto de produção (dinheiro real); este projeto só aceita `false`.
- **Lojista (merchant)**: estabelecimento que usa o gateway; autentica com API key e recebe webhooks.
- **MDR (Merchant Discount Rate)**: taxa descontada do lojista por transação de cartão; aqui simulada pela taxa do gateway.
- **Outbox**: padrão em que o evento a notificar é gravado na mesma transação da mudança de estado e enviado depois por um processo separado.
- **PaymentIntent**: objeto da Stripe que representa a intenção de cobrar um valor e acompanha seu ciclo (autorizar, capturar, cancelar).
- **PaymentMethod**: objeto/identificador da Stripe que representa o meio de pagamento (ex.: `pm_card_visa`), em vez dos dados do cartão.
- **Reconciliação**: consultar o provedor e alinhar o estado local ao estado real do pagamento (via GET, webhook ou worker periódico).
- **SSRF (Server-Side Request Forgery)**: ataque que faz o servidor chamar endereços internos; mitigado na validação da URL de webhook.
- **Void**: cancelamento de uma autorização antes da captura; nada é cobrado nem lançado. Estado `Voided`.
