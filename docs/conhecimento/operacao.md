# Operação: execução, configuração, CI e build (OPS)

Como a aplicação sobe, onde fica cada configuração e o que o CI verifica.
Código: `src/PaymentGateway.Api/Program.cs`, `appsettings*.json`, `Properties/launchSettings.json`, `Dockerfile`,
`docker-compose.yml`, `.github/workflows/ci.yml`, `.github/dependabot.yml`, `start.ps1`, `stop.ps1`,
`Directory.Build.props`, `dotnet-tools.json`. Comandos de uso: README §Executar localmente e §Testes e engenharia.

## Execução

- **OPS-01** (D) Portas: 5223 com `dotnet run` (perfil `http`; o perfil `https` usa também 7246) e 8080 no Docker Compose (publicada só em 127.0.0.1). Fonte: README §Executar localmente, §Docker Compose; launchSettings.json:4-20; docker-compose.yml:4-7.
- **OPS-02** (D) Em Development a aplicação aplica as migrations e semeia o lojista demo na inicialização; em qualquer outro ambiente as migrations devem ser aplicadas antes de subir a API. Fonte: README §Testes e engenharia; Program.cs:145-154.
- **OPS-03** (I) Em Testing o schema é criado pela própria suíte (`EnsureCreated`), não por migrations. Fonte: Program.cs:145-146; ApiFactory.cs:50-54; TestDb.cs:91.
- **OPS-04** (I) Swagger/OpenAPI só em Development/Testing; a raiz `/` informa modo e provedor. Fonte: Program.cs:175-188.
- **OPS-05** (D) O Compose roda em Development (demo) e guarda o SQLite no volume `gateway-data`; `docker compose down` preserva dados, remover o volume apaga a demo. Fonte: README §Docker Compose; docker-compose.yml:8-19.
- **OPS-06** (D) A imagem é multi-stage (SDK só no build) e roda com usuário sem privilégios (`app`). Fonte: README §Docker Compose; Dockerfile:1-24.
- **OPS-07** (I) `start.ps1` sobe a API em segundo plano (padrão .NET na 5223; `-Docker` usa Compose na 8080; `-NoBrowser` não abre o Swagger), não duplica se a porta já estiver em uso, grava saída em `logs/` e espera `/health` por até 90 s. `stop.ps1` encerra o processo na 5223 e o Compose. Scripts em ASCII/CRLF. Fonte: start.ps1:1-110; stop.ps1:1-51; commit d53a8ac.
- **OPS-08** (I) Configuração por seção: `ConnectionStrings:Default`, `Gateway` (FeeBps, RateLimitPerMinute, Processor), `Stripe` (SecretKey, WebhookSecret), `PaymentReconciliation` (Interval, BatchSize), `Serilog`. Sobrescrever por variável de ambiente com `__`. Fonte: appsettings.json:1-23; Program.cs:36-45.
- **OPS-09** (I) `GET /health` verifica a conexão com o banco (health check do EF Core). Fonte: Program.cs:76-79, 189.

## Build, CI e dependências

- **OPS-10** (I) Warnings são erros em todos os projetos (analisadores .NET em `latest`), exceto avisos de auditoria NuGet NU1901–NU1904 localmente. Porquê: não quebrar o build de quem clona por vulnerabilidade externa. Fonte: Directory.Build.props:1-15.
- **OPS-11** (D) CI (push/PR em `main`): restore com auditoria NuGet (NU1902–NU1904 viram erro só no CI), build Release, testes, `docker compose config` e build da imagem. Fonte: README §Testes e engenharia; ci.yml:1-39.
- **OPS-12** (I) Dependabot semanal para NuGet (agrupado) e GitHub Actions. Fonte: dependabot.yml:1-16.
- **OPS-13** (I) Solução no formato `.slnx`; `dotnet-ef` fixado no manifesto local de ferramentas (`dotnet tool restore`). Fonte: PaymentGateway.slnx; dotnet-tools.json:4-11.
- **OPS-14** (D) SQLite foi escolhido para demonstração local de instância única; implantação distribuída exigiria revisar banco, recuperação, provisionamento e segredos. Ver DEC-01. Fonte: README §Testes e engenharia.
