# Convenções de código, testes e commits (CNV)

Código: `.editorconfig`, `Directory.Build.props`, `tests/PaymentGateway.Tests/*` (ApiFactory, TestDb), histórico do Git.

## Arquitetura e estilo

- **CNV-01** (I) Camadas lineares Api → Services → Data → Domain (dependências só apontam para baixo; Api referencia só Services); regras de negócio no Domain e em Services, controllers finos. Fonte: src/*/*.csproj (ProjectReference); PaymentsController.cs.
- **CNV-02** (I) Entidades de domínio com setters privados e transições como métodos da própria entidade (ex.: `Capture`, `Void`, `Refund`), construtor privado para o EF Core. Entidades de registro técnico (LedgerEntry, PaymentEvent, Merchant) usam setters públicos. Fonte: Payment.cs:11-47; LedgerEntry.cs:11-21.
- **CNV-03** (D) Dinheiro sempre `long` em centavos; nunca `float`/`double`/`decimal` para valores. Fonte: Payment.cs:16-20.
- **CNV-04** (I) Erros de regra de negócio: lançar `DomainException` (→ 422); recurso inexistente ou de outro lojista: `NotFoundException` (→ 404). Nada de try/catch nos controllers; o middleware converte. Fonte: DomainException.cs:3-6; NotFoundException.cs:3; ErrorHandlingMiddleware.cs:7-10.
- **CNV-05** (I) Estilo: namespaces file-scoped, 4 espaços em C#, 2 em JSON/XML/csproj, tipos explícitos preferidos a `var` (nível silent), `Nullable` e `ImplicitUsings` ativos. Fonte: .editorconfig:23-29, 94-96, 129; Directory.Build.props:9-10.
- **CNV-06** (C) O código deve ser explicável por um desenvolvedor C# intermediário: soluções sólidas, sem abstrações excessivas; toda decisão precisa de um porquê defensável. Fonte: usuário 2026-07-06.
- **CNV-07** (I) Comentários explicam o porquê da decisão (não o quê) e ficam em português no código original. ⚠ ver PEN-04. Fonte: ex. Payment.cs:16-19; PaymentService.cs:175-176.

## Testes

- **CNV-08** (I) xUnit. Testes de serviço usam `TestDb` (SQLite em memória, conexão aberta durante o teste, `EnsureCreated`); testes de concorrência usam banco em arquivo com contextos separados. Fonte: TestDb.cs:68-99; PaymentReliabilityTests.cs (FileDatabase).
- **CNV-09** (I) Testes de integração usam `ApiFactory` (WebApplicationFactory, ambiente Testing, SQLite em memória) e removem o `WebhookDispatcher` do host, porque a conexão SQLite em memória não suporta uso concorrente. Fonte: ApiFactory.cs:14-48.
- **CNV-10** (I) Stripe é testada com cliente HTTP simulado do SDK oficial, sem credenciais nem rede. Fonte: README §Testes e engenharia; StripePaymentProcessorTests.cs.
- **CNV-11** (I) Nomes de teste seguem três estilos: `Metodo_Cenario_Resultado` em português, `Metodo_Scenario_Result` em inglês e frases em snake_case. ⚠ ver PEN-04. Fonte: PaymentServiceTests.cs; PaymentSafetyTests.cs; RateLimitingTests.cs.

## Commits

- **CNV-12** (C) Commits sem trailers de coautoria automáticos; autor com o e-mail noreply do GitHub configurado no repositório. Fonte: usuário 2026-09-28.
- **CNV-13** (I) Commits recentes seguem Conventional Commits em português sem acentos (`feat:`, `build:`, `docs:`); os mais antigos usam frase no presente ("Adiciona …") com corpo em tópicos. Fonte: git log (034fa09 … dee3b93).
- **CNV-14** (I) Branch principal `main`; CI roda em push e PR para `main`. Fonte: ci.yml:3-7.
