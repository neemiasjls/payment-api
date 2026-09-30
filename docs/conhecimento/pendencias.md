# Pendências

Conflitos, dúvidas, possíveis bugs e limpezas que aguardam decisão. Ao resolver, atualize a regra afetada,
remova o marcador `⚠ ver PEN-NN` e apague a pendência (o histórico fica no Git).

### PEN-01 [conflito] Comentário do rate limit ainda diz "por API key"
Fontes: `GatewayOptions.cs:14` ("por API key") × `Program.cs:81-99` e README §O que está implementado (por IP).
Decidir: atualizar o comentário de `RateLimitPerMinute` para "por IP de origem". Regra afetada: ACS-09. Data: 2026-09-30.

### PEN-02 [limpeza] Comentário do enum PaymentStatus não cita Pending/RequiresAction
Fontes: `PaymentStatus.cs:3-8` (ciclo só a partir de Authorized) × `Payment.cs:49-84` (criação em Pending, RequiresAction).
Decidir: reescrever o comentário com o ciclo atual (ver PAG-05). Regra afetada: PAG-04. Data: 2026-09-30.

### PEN-03 [limpeza] Factories antigas Payment.Authorize e Payment.Decline sem uso
Fontes: `Payment.cs:114-148` × busca por uso em src/ e tests/ (nenhuma chamada encontrada em 2026-09-30).
Decidir: remover as factories do fluxo antigo com cartão ou manter por algum motivo. Data: 2026-09-30.

### PEN-04 [dúvida] Idioma misto em código, comentários e nomes de teste
Fontes: código e testes antigos em português (ex.: PaymentServiceTests.cs) × código novo de Stripe/reconciliação e parte dos testes em inglês (StripePaymentProcessor.cs, PaymentReconciliationWorker.cs, RateLimitingTests.cs).
Decidir: qual é a convenção para código novo (idioma de comentários, mensagens e nomes de teste). Regras afetadas: CNV-07, CNV-11. Data: 2026-09-30.

### PEN-05 [conflito] README fala em índices únicos protegendo o ledger, mas LedgerEntries não tem índice único
Fontes: README §O que está implementado ("versão de concorrência … e índices únicos protegem … os lançamentos do ledger") × `AppDbContext.cs:51-58` e migrations (só índices não únicos em LedgerEntries). A proteção real é o `Version` + transições (testes de captura/estorno concorrentes passam).
Decidir: ajustar a redação do README ou adicionar índice único (ex.: PaymentId + Account + Type). Regra afetada: LED-07. Data: 2026-09-30.

### PEN-06 [limpeza] Comentário de IdempotencyRecord cita só a criação de pagamentos
Fontes: `IdempotencyRecord.cs:3-7` × `PaymentService.cs:482-518` (a mesma tabela guarda chaves de capture, void e refund).
Decidir: atualizar o comentário. Regra afetada: IDP-03. Data: 2026-09-30.
