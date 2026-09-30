# Payment Gateway API — sandbox

API de pagamentos em C# e .NET 10 para portfólio. Ela oferece autorização, captura, cancelamento antes da captura e estorno completo depois da captura. O provedor pode ser um simulador local (`fake`) ou a **Stripe Test Mode** (`stripe`). Nenhum dos dois movimenta dinheiro real.

> **Escopo:** os lojistas, saldos, taxas e lançamentos do ledger são demonstrativos. A integração usa uma única conta Stripe de testes e **não** faz onboarding, split de pagamento nem repasses para vários lojistas. Não use esta aplicação para pagamentos reais.

## O que está implementado

- `IPaymentProcessor` separa o domínio dos provedores `fake` e `stripe`.
- A Stripe usa o SDK oficial, `PaymentIntent` com `capture_method=manual`, cancelamento do intent e `Refund` após a captura.
- A API recebe apenas `paymentMethodId` de teste. Ela não recebe número de cartão nem CVV. O provedor e o ID do pagamento no provedor ficam persistidos.
- Cada chamada que altera um pagamento exige `Idempotency-Key`. A mesma chave com outros dados ou outra operação é rejeitada. As chamadas à Stripe usam chaves estáveis por pagamento e operação.
- Estados `Pending` e `RequiresAction` não são tratados como autorização concluída. Uma resposta de ação adicional pode incluir `clientSecret`; sem uma interface de autenticação, o roteiro abaixo apenas demonstra esse estado.
- A versão de concorrência no pagamento protege as transições (e, com elas, os lançamentos do ledger) contra duplicação; índices únicos protegem as chaves de idempotência.
- O endpoint de entrada da Stripe valida a assinatura do corpo original, rejeita eventos `livemode`, deduplica IDs de evento e consulta o estado atual do provedor para reconciliar o pagamento.
- Com Stripe ativo, um worker também consulta na Stripe pagamentos `Pending`, `RequiresAction`, `Authorized` ou `Captured` com `providerPaymentId` ao iniciar e, por padrão, a cada cinco minutos em lotes de 50. Isso permite detectar mudanças mesmo se um webhook não chegar.
- Eventos destinados ao webhook do lojista são gravados no SQLite junto com a mudança de estado. O worker varre pendências após reinício, reserva cada entrega e tenta novamente até cinco vezes. O consumidor deve deduplicar pelo header `X-Webhook-Event-Id`: entrega é **pelo menos uma vez**, não exatamente uma vez.
- API key por lojista, limite global de 100 requisições por minuto por IP, health check, logs estruturados e OpenAPI em Development/Testing.

## Pré-requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) ou Docker Compose.
- Para o provedor Stripe: conta Stripe em ambiente de testes, chave secreta `sk_test_...` e [Stripe CLI](https://docs.stripe.com/cli/install) para encaminhar webhooks locais.

O modo padrão é `fake`; não é preciso criar conta Stripe para executar o projeto e os testes.

## Executar localmente

No PowerShell, na raiz do repositório:

```powershell
dotnet run --project src/PaymentGateway.Api --launch-profile http
```

A API fica em `http://localhost:5223`; abra `http://localhost:5223/swagger`. Em **Development**, a aplicação aplica as migrations e cria um lojista demo em um banco novo:

```text
X-Api-Key: pk_test_demo_1234567890abcdef
```

Essa chave é pública e serve somente para a demonstração. O cadastro anônimo de outros lojistas (`POST /api/v1/merchants`) também só está disponível em **Development/Testing**. Fora desses ambientes, não há cadastro público nem lojista demo; é preciso provisionar um lojista por um fluxo administrativo antes de servir requisições autenticadas.

### Docker Compose

```powershell
docker compose up --build
```

A API fica em `http://localhost:8080/swagger`. O Compose roda em `Development` para esta demonstração e guarda o banco SQLite no volume nomeado `gateway-data`. `docker compose down` preserva os dados; remover o volume apaga pagamentos, chaves e eventos da demonstração. A imagem executa com usuário sem privilégios.

### Stripe Test Mode

1. No painel da Stripe, ative o ambiente de testes e obtenha sua chave secreta `sk_test_...` (nunca `sk_live_...`). Instale a CLI e faça `stripe login`.
2. Em outro terminal, encaminhe os [eventos snapshot](https://docs.stripe.com/webhooks#local-listener) para a API:

   ```powershell
   stripe listen --forward-to localhost:5223/api/v1/webhooks/stripe
   ```

   Para o Compose, use `localhost:8080` no lugar de `localhost:5223`. Copie o `whsec_...` exibido pela CLI. O segredo desse listener é diferente do segredo de um endpoint cadastrado no Dashboard e diferente do segredo HMAC de um lojista local.
3. Para executar pelo SDK, configure as variáveis **no mesmo terminal que iniciará a API**:

   ```powershell
   $env:Gateway__Processor = 'stripe'
   $env:Stripe__SecretKey = 'sk_test_SUBSTITUA_PELA_SUA_CHAVE'
   $env:Stripe__WebhookSecret = 'whsec_SUBSTITUA_PELO_SEGREDO_DA_CLI'
   dotnet run --project src/PaymentGateway.Api --launch-profile http
   ```

   Para o Compose, copie `.env.example` para `.env`, preencha `GATEWAY_PROCESSOR=stripe`, `STRIPE_SECRET_KEY` e `STRIPE_WEBHOOK_SECRET`, e rode `docker compose up --build`. `.env` é ignorado pelo Git e pelo contexto de build da imagem.

A inicialização falha se o processador Stripe não receber uma chave `sk_test_` e um segredo `whsec_`. O adaptador também rejeita respostas da Stripe marcadas como `livemode`. Use um banco novo ao trocar `fake` por `stripe`: pagamentos antigos apontam para o provedor anterior. O simulador guarda seu próprio estado em memória; por isso, uma autorização `fake` ainda no SQLite após reiniciar pode não ser capturável ou cancelável. Para testar retomada de operações entre reinícios, use Stripe Test Mode.

A reconciliação periódica da Stripe usa `PaymentReconciliation__Interval=00:05:00` e `PaymentReconciliation__BatchSize=50` como padrões configuráveis. Ela só consulta pagamentos cujo ID remoto já foi salvo. Se uma autorização continuar pendente **sem** `providerPaymentId` por mais de 23 horas, a API não a reenviará à Stripe: a chave idempotente do provedor pode ter expirado, e a repetição poderia criar outro pagamento. Esse caso exige um webhook tardio ou investigação e reconciliação manual; o worker periódico não consegue recuperá-lo sozinho.

## Roteiro de demonstração

O arquivo [PaymentGateway.Api.http](src/PaymentGateway.Api/PaymentGateway.Api.http) contém estas chamadas prontas para REST Client ou Visual Studio. Ajuste `@baseUrl` para `http://localhost:8080` se usar Docker.

1. Autorize `15000` centavos em `BRL` com `paymentMethodId: "pm_card_visa"` e um `Idempotency-Key` novo. A resposta traz o ID local, `provider` e `providerPaymentId`. Em Stripe, o status esperado é `Authorized` após o intent alcançar `requires_capture`.
2. Repita **a mesma** requisição e chave: a API devolve o mesmo pagamento. Tente a mesma chave com valor diferente: a API rejeita o conflito.
3. Use o ID local retornado para `POST /capture` com **outra** chave de idempotência. Consulte `/api/v1/merchants/me/balance` e `/events`.
4. Faça `POST /refund` com uma terceira chave. Consulte o saldo e os eventos novamente. Para exercitar `/void`, crie outro pagamento autorizado e cancele **antes** de capturá-lo.
5. Experimente `pm_card_visa_chargeDeclined` para recusa e `pm_card_authenticationRequired` para `RequiresAction`. Sem frontend de autenticação 3D Secure, o pagamento `RequiresAction` não pode ser capturado neste roteiro.
6. Com Stripe CLI rodando, observe a entrega no terminal e confira a transição via `GET /api/v1/payments/{id}`. O endpoint da API aceita apenas webhooks assinados e eventos de teste.

Os identificadores `pm_card_...` acima são [PaymentMethods de teste documentados pela Stripe](https://docs.stripe.com/testing). O projeto permite apenas uma lista pequena desses identificadores; não envie dados de cartão no JSON.

Exemplo mínimo de autorização:

```http
POST /api/v1/payments HTTP/1.1
X-Api-Key: pk_test_demo_1234567890abcdef
Idempotency-Key: pedido-0001-autorizar
Content-Type: application/json

{
  "amountInCents": 15000,
  "currency": "BRL",
  "description": "Pedido #0001",
  "paymentMethodId": "pm_card_visa"
}
```

## Endpoints

| Método | Rota | Uso |
| --- | --- | --- |
| `GET` | `/health` | Verifica conexão com o banco. |
| `POST` | `/api/v1/merchants` | Cadastro anônimo somente em Development/Testing; retorna API key uma vez. |
| `GET` | `/api/v1/merchants/me` | Consulta lojista autenticado. |
| `GET` | `/api/v1/merchants/me/balance` | Saldo demonstrativo derivado do ledger. |
| `POST` | `/api/v1/payments` | Autoriza; `Idempotency-Key` obrigatória. |
| `GET` | `/api/v1/payments` | Lista paginada e filtrável por status. |
| `GET` | `/api/v1/payments/{id}` | Consulta e reconcilia estado pendente. |
| `POST` | `/api/v1/payments/{id}/capture` | Captura; `Idempotency-Key` obrigatória. |
| `POST` | `/api/v1/payments/{id}/void` | Cancela antes da captura; `Idempotency-Key` obrigatória. |
| `POST` | `/api/v1/payments/{id}/refund` | Estorna totalmente após a captura; `Idempotency-Key` obrigatória. |
| `GET` | `/api/v1/payments/{id}/events` | Eventos e tentativas de entrega ao lojista. |
| `POST` | `/api/v1/webhooks/stripe` | Entrada pública autenticada por `Stripe-Signature`, apenas com provedor Stripe ativo. |

Todas as rotas de lojista e pagamento exigem `X-Api-Key`. A demonstração aceita apenas `BRL`, com valores inteiros de 50 a 99.999.999 centavos. `Idempotency-Key` tem no máximo 100 caracteres e deve ser única por operação lógica; reutilizá-la com outra operação ou outros dados gera erro. A resposta HTTP de um pagamento novo é `201`, e uma repetição idempotente da criação retorna `200`.

### Webhooks do lojista

Ao cadastrar um lojista no ambiente de demonstração, `webhookUrl` é opcional. A API aceita URL **HTTPS pública na porta 443**, sem redirecionamentos; loopback HTTP(S) é permitido apenas em Development/Testing. A validação também bloqueia endereços não públicos no momento da conexão para reduzir SSRF. Se a URL for fornecida, o cadastro devolve `webhookSecret` uma única vez.

As notificações incluem `X-Webhook-Signature` (HMAC-SHA256 do corpo, prefixo `sha256=`) e `X-Webhook-Event-Id`. Confirme a assinatura usando o corpo recebido sem reserializar e compare em tempo constante. Guarde cada ID de evento processado para lidar com retentativas. O `whsec_...` do lojista valida **somente** os webhooks enviados por este gateway; o `whsec_...` da Stripe valida os webhooks **recebidos da Stripe**.

## Testes e engenharia

```powershell
dotnet restore
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release
```

O CI executa restore com auditoria NuGet, build, testes, validação do Compose e build da imagem. Os testes de Stripe usam um cliente HTTP simulado do SDK oficial; eles não dependem de credenciais. A confirmação contra a API da Stripe exige suas credenciais de teste.

O SQLite mantém idempotência, ledger, IDs do provedor e outbox entre reinícios, mas foi escolhido para uma demonstração local de instância única. O worker deixa de tentar entregar um evento após cinco falhas; não existe interface de reenvio manual. A implantação distribuída exigiria revisar banco, recuperação operacional, provisionamento de lojistas e gestão de segredos. Em ambientes fora de Development, as migrations precisam ser aplicadas antes de subir a API.

## Licença

[MIT](LICENSE) — © 2026 Neemias Silva
