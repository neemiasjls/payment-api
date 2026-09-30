# Pendências

Conflitos, dúvidas, possíveis bugs e limpezas que aguardam decisão. Ao resolver, atualize a regra afetada,
remova o marcador `⚠ ver PEN-NN` e apague a pendência (o histórico fica no Git).

### PEN-04 [dúvida] Idioma misto em código, comentários e nomes de teste
Fontes: código e testes antigos em português (ex.: PaymentServiceTests.cs) × código novo de Stripe/reconciliação e parte dos testes em inglês (StripePaymentProcessor.cs, PaymentReconciliationWorker.cs, RateLimitingTests.cs).
Decidir: qual é a convenção para código novo (idioma de comentários, mensagens e nomes de teste). Regras afetadas: CNV-07, CNV-11. Data: 2026-09-30.
