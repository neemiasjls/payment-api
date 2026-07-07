# 💳 Payment Gateway API

API de gateway de pagamentos construída em **C# / .NET 10**, simulando o núcleo de um sistema de adquirência: autorização, captura, cancelamento e estorno de transações com cartão, contabilidade de partidas dobradas, idempotência e notificação por webhooks.

> Projeto de portfólio com foco no domínio de **meios de pagamento** — o ciclo de vida completo de uma transação, do "passou o cartão" ao repasse (menos a taxa) para o lojista.

## Funcionalidades

- **Ciclo de vida da transação** — `Authorized → Captured → Refunded` (ou `Voided` antes da captura, `Declined` na recusa), com as transições de estado protegidas dentro da própria entidade de domínio.
- **Ledger de partidas dobradas** — nenhum saldo é um campo mutável no banco; tudo é derivado de lançamentos contábeis imutáveis. Em toda captura, débitos = créditos (valor bruto = repasse ao lojista + taxa do gateway).
- **Idempotência** — o header `Idempotency-Key` garante que uma retentativa de rede não cobre o cliente duas vezes, com proteção contra corrida via índice único no banco.
- **Autenticação por API key** — cada lojista recebe uma chave `pk_...` exibida uma única vez; apenas o hash SHA-256 é armazenado.
- **Consciência de PCI DSS** — número completo do cartão e CVV **nunca** são persistidos; apenas bandeira e últimos 4 dígitos.
- **Validação de cartão** — algoritmo de Luhn, detecção de bandeira (incluindo Elo e Hipercard) e checagem de validade.
- **Webhooks com retentativas** — eventos (`payment.authorized`, `payment.captured`...) são entregues por um serviço em background com backoff exponencial, fora do ciclo requisição/resposta.
- **36 testes unitários** com xUnit + SQLite em memória.

## Arquitetura

```mermaid
graph LR
    subgraph API["PaymentGateway.Api"]
        MW["Middlewares<br/>(API key + erros)"] --> C["Controllers"]
    end
    subgraph SVC["PaymentGateway.Services"]
        PS["PaymentService"]
        MS["MerchantService"]
        WD["WebhookDispatcher<br/>(background)"]
    end
    subgraph DATA["PaymentGateway.Data"]
        DB["AppDbContext<br/>(EF Core + SQLite)"]
    end
    subgraph DOM["PaymentGateway.Domain"]
        E["Entidades + regras<br/>de negócio"]
    end

    C --> PS & MS
    PS -. enfileira evento .-> WD
    PS & MS --> DB
    DB --> E
    WD -- "POST (retry 3x)" --> EXT["Servidor do lojista"]
```

Camadas em dependência linear — `Api → Services → Data → Domain` — cada uma conhece apenas a de baixo. O domínio é C# puro, sem dependência de framework.

## Como rodar

Pré-requisito: [.NET SDK 10](https://dotnet.microsoft.com/download)

```bash
dotnet run --project src/PaymentGateway.Api
```

Acesse **http://localhost:5223/swagger**. Em desenvolvimento, um lojista demo já vem criado com a API key:

```
pk_test_demo_1234567890abcdef
```

Clique em **Authorize** no Swagger e cole a chave — ou use o arquivo [PaymentGateway.Api.http](src/PaymentGateway.Api/PaymentGateway.Api.http), que contém o fluxo completo pronto para executar no VS Code (extensão REST Client) ou no Visual Studio.

### Rodar os testes

```bash
dotnet test
```

## Endpoints

| Método | Rota | Descrição | Auth |
|--------|------|-----------|:----:|
| POST | `/api/merchants` | Cadastra lojista e gera a API key | — |
| GET | `/api/merchants/me` | Dados do lojista autenticado | 🔑 |
| GET | `/api/merchants/me/balance` | Saldo calculado a partir do ledger | 🔑 |
| POST | `/api/payments` | Autoriza um pagamento (aceita `Idempotency-Key`) | 🔑 |
| GET | `/api/payments` | Lista com paginação e filtro por status | 🔑 |
| GET | `/api/payments/{id}` | Consulta um pagamento | 🔑 |
| POST | `/api/payments/{id}/capture` | Captura (confirma a cobrança) | 🔑 |
| POST | `/api/payments/{id}/void` | Cancela autorização não capturada | 🔑 |
| POST | `/api/payments/{id}/refund` | Estorna pagamento capturado | 🔑 |
| GET | `/api/payments/{id}/events` | Trilha de eventos / status dos webhooks | 🔑 |

## Cartões de teste

| Número | Resultado |
|--------|-----------|
| `4242 4242 4242 4242` | ✅ Aprovado (Visa) |
| `5555 5555 5555 4444` | ✅ Aprovado (Mastercard) |
| `4000 0000 0000 0002` | ❌ Recusado — `card_declined` |
| `4000 0000 0000 9995` | ❌ Recusado — `insufficient_funds` |
| `4000 0000 0000 9987` | ❌ Recusado — `lost_card` |

Qualquer número que falhe no algoritmo de Luhn é rejeitado com **HTTP 422** antes mesmo de chegar ao "emissor".

## Decisões técnicas

| Decisão | Motivo |
|---------|--------|
| Valores monetários em **centavos (inteiro)** | Elimina erros de arredondamento de ponto flutuante; mesmo padrão da Stripe |
| Saldo **derivado do ledger**, nunca armazenado | Auditabilidade total: cada centavo tem origem rastreável; impossível o saldo "desincronizar" |
| Transições de estado **dentro da entidade** `Payment` | Nenhum serviço consegue, por engano, estornar algo não capturado — a regra mora num lugar só |
| API key com **hash SHA-256** no banco | Se o banco vazar, as chaves não vazam junto (mesmo princípio de senha) |
| Idempotência com **índice único** no banco | Funciona mesmo com requisições concorrentes — a constraint decide o vencedor |
| Webhooks via **Channel + BackgroundService** | A API responde rápido; a entrega (lenta e falível) acontece em background com retry |
| SQLite + `EnsureCreated` | Zero configuração para rodar o projeto; em produção seria PostgreSQL + migrations |

## Possíveis evoluções

- Captura e estorno **parciais**
- Migrations do EF Core + PostgreSQL
- Assinatura HMAC nos webhooks (para o lojista validar a origem)
- Rate limiting por lojista
- Docker + docker-compose
- Relatório de conciliação diária (batch)

---

Desenvolvido por **Neemias Silva** como projeto de estudo do domínio de pagamentos.
