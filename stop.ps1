<#
  stop.ps1 - Derruba o Payment Gateway API com um comando so.

  - Mata os processos que estiverem escutando nas portas do sistema
    (5223 = API via dotnet run).
  - Para os containers do docker compose do projeto, se existirem
    (avisa e segue em frente se o Docker estiver fechado).
#>
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$ports = @(5223)
$stoppedSomething = $false

# ----- Processos locais, pelas portas -----
foreach ($port in $ports) {
    $listeners = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    $ownerPids = $listeners | Select-Object -ExpandProperty OwningProcess -Unique

    foreach ($ownerPid in $ownerPids) {
        $process = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
        if ($process) {
            Write-Host "Parando $($process.ProcessName) (PID $ownerPid) na porta $port..."
            Stop-Process -Id $ownerPid -Force
            $stoppedSomething = $true
        }
    }
}

# ----- Containers do docker compose (porta 8080) -----
if (Get-Command docker -ErrorAction SilentlyContinue) {
    docker info *> $null
    if ($LASTEXITCODE -eq 0) {
        $composeFile = Join-Path $root "docker-compose.yml"
        $containers = docker compose -f $composeFile ps -q 2>$null
        if ($containers) {
            Write-Host "Parando containers do docker compose..."
            docker compose -f $composeFile down *> $null
            $stoppedSomething = $true
        }
    }
    else {
        Write-Host "(Docker instalado mas com o engine fechado - nada a parar por la.)"
    }
}

if ($stoppedSomething) {
    Write-Host "[OK] Tudo parado."
}
else {
    Write-Host "[OK] Nada estava rodando."
}
