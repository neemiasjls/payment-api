<#
  start.ps1 - Sobe o Payment Gateway API em segundo plano.

  O sistema tem um unico servico: a API ASP.NET Core (o banco SQLite e
  embutido no processo, nao existe servidor de banco separado).

  Uso:
    .\start.ps1              # roda com o .NET SDK na porta 5223
    .\start.ps1 -Docker      # roda com docker compose na porta 8080
    .\start.ps1 -NoBrowser   # nao abre o navegador no final

  - Idempotente: se a porta ja estiver em uso, nao duplica o servico.
  - Saida de cada processo vai para arquivos em .\logs\
  - Espera o /health responder antes de declarar sucesso.
#>
[CmdletBinding()]
param(
    [switch]$Docker,
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Test-PortListening {
    param([int]$Port)
    return $null -ne (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

function Wait-Healthy {
    param([string]$Url, [int]$TimeoutSec)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) { return $true }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }
    return $false
}

$logsDir = Join-Path $root "logs"
New-Item -ItemType Directory -Force -Path $logsDir | Out-Null

if ($Docker) {
    # ----- Modo Docker: docker compose, porta 8080 -----
    $port = 8080
    $baseUrl = "http://localhost:$port"

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Write-Warning "Docker nao esta instalado. Instale o Docker Desktop ou rode .\start.ps1 sem -Docker."
        exit 1
    }

    docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Docker esta instalado, mas o engine nao esta rodando. Abra o Docker Desktop e tente de novo."
        exit 1
    }

    if (Test-PortListening $port) {
        Write-Host "[OK] Porta $port ja em uso - servico ja esta no ar, nao vou duplicar."
    }
    else {
        Write-Host "Subindo containers (docker compose up -d --build)... saida em logs\docker.log"
        docker compose up -d --build *> (Join-Path $logsDir "docker.log")
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Falha no docker compose. Confira logs\docker.log"
            exit 1
        }
    }
}
else {
    # ----- Modo padrao: .NET SDK, porta 5223 -----
    $port = 5223
    $baseUrl = "http://localhost:$port"

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Warning ".NET SDK nao encontrado. Instale em https://dotnet.microsoft.com/download ou use .\start.ps1 -Docker."
        exit 1
    }

    if (Test-PortListening $port) {
        Write-Host "[OK] API ja esta rodando na porta $port - nao vou duplicar."
    }
    else {
        Write-Host "Iniciando a API em segundo plano (porta $port)... saida em logs\api.log"
        Start-Process -FilePath "dotnet" `
            -ArgumentList "run", "--project", "src/PaymentGateway.Api", "--launch-profile", "http" `
            -WorkingDirectory $root `
            -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $logsDir "api.log") `
            -RedirectStandardError (Join-Path $logsDir "api.err.log")
    }
}

Write-Host "Aguardando $baseUrl/health responder..."
if (Wait-Healthy -Url "$baseUrl/health" -TimeoutSec 90) {
    Write-Host "[OK] API no ar: $baseUrl/swagger"
    if (-not $NoBrowser) {
        Start-Process "$baseUrl/swagger"
    }
}
else {
    Write-Warning "API nao respondeu em 90 segundos. Confira os arquivos em .\logs\"
    exit 1
}
