#Requires -Version 5.1
<#
.SYNOPSIS
    Самопроверка моста MCP-Revit: связь, уровни, создание тестовой стены 1 м.

.DESCRIPTION
    Обращается напрямую к HTTP-мосту аддина (по умолчанию http://127.0.0.1:8765),
    минуя Claude Desktop. Это позволяет отделить проблемы моста/аддина от
    проблем конфигурации MCP-клиента.

    Шаги:
      1. GET  /health          -- мост запущен, нужен ли токен
      2. POST document.info    -- проект открыт
      3. POST levels.list      -- находит уровень (по имени или самый нижний)
      4. POST walls.create     -- стена 1000 мм по оси X из точки (0,0)

.PARAMETER Url
    Адрес моста. По умолчанию берётся из $env:REVIT_BRIDGE_URL, иначе 127.0.0.1:8765.

.PARAMETER Token
    Значение заголовка X-Mcp-Token, если в аддине включена авторизация.
    По умолчанию берётся из $env:REVIT_BRIDGE_TOKEN.

.PARAMETER LevelName
    Имя уровня для стены. Если не найдено -- берётся уровень с наименьшей отметкой.

.PARAMETER LengthMm
    Длина тестовой стены в миллиметрах.

.PARAMETER DryRun
    Выполнить только проверки 1-3, стену не создавать.

.EXAMPLE
    .\test-bridge-wall.ps1
    .\test-bridge-wall.ps1 -LevelName 'Level 1' -DryRun
#>
param(
    [string]$Url,
    [string]$Token,
    [string]$LevelName = 'Уровень 1',
    [double]$LengthMm  = 1000,
    [double]$HeightMm  = 3000,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not $Url)   { $Url   = if ($env:REVIT_BRIDGE_URL)   { $env:REVIT_BRIDGE_URL }   else { 'http://127.0.0.1:8765' } }
if (-not $Token) { $Token = $env:REVIT_BRIDGE_TOKEN }
$Url = $Url.TrimEnd('/')

$headers = @{}
if ($Token) {
    if ($Token -cmatch '[^\x00-\x7F]') {
        Write-Host "  [-] Токен должен состоять только из ASCII-символов." -ForegroundColor Red
        exit 1
    }
    $headers['X-Mcp-Token'] = $Token
}

function Write-Ok   { param($m) Write-Host "  [+] $m" -ForegroundColor Green  }
function Write-Err  { param($m) Write-Host "  [-] $m" -ForegroundColor Red    }
function Write-Step { param($m) Write-Host "  [*] $m" -ForegroundColor Cyan   }
function Write-Info { param($m) Write-Host "      $m" -ForegroundColor Gray   }

function Invoke-Bridge {
    param([string]$Command, [hashtable]$Params = @{}, [double]$TimeoutSec = 120)

    $payload = @{ command = $Command; params = $Params; timeout_sec = $TimeoutSec - 5 } |
        ConvertTo-Json -Depth 10 -Compress
    # Ответы моста в UTF-8; без явной перекодировки имена уровней приходят "кракозябрами".
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)

    $resp = Invoke-RestMethod -Uri "$Url/command" -Method Post -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -Headers $headers -TimeoutSec $TimeoutSec

    if (-not $resp.ok) {
        $err = $resp.error
        throw "Мост отклонил '$Command': $($err.message)  [$($err.type)]"
    }
    return $resp.result
}

Write-Host ""
Write-Host "  Проверка моста MCP-Revit  --  $Url" -ForegroundColor White
Write-Host ""

# -- 1. Живость моста ---------------------------------------------------------
Write-Step "1/4  GET /health"
try {
    $health = (Invoke-RestMethod -Uri "$Url/health" -Method Get -Headers $headers -TimeoutSec 10).result
} catch {
    Write-Err "Мост недоступен: $($_.Exception.Message)"
    Write-Info "Запустите Revit, откройте проект и включите мост на вкладке «MCP»."
    exit 1
}
Write-Ok "мост отвечает"
if ($health.auth_required -and -not $Token) {
    Write-Err "Мост требует токен, а он не задан."
    Write-Info "Передайте -Token <значение> или задайте REVIT_BRIDGE_TOKEN."
    exit 1
}

# -- 2. Открытый документ -----------------------------------------------------
Write-Step "2/4  document.info"
try {
    $doc = Invoke-Bridge 'document.info'
} catch {
    Write-Err $_.Exception.Message
    Write-Info "Скорее всего, в Revit не открыт проект."
    exit 1
}
Write-Ok "проект: $($doc.title)"

# -- 3. Уровни ----------------------------------------------------------------
Write-Step "3/4  levels.list"
$levels = @((Invoke-Bridge 'levels.list').levels)
if ($levels.Count -eq 0) {
    Write-Err "В проекте нет уровней -- стену ставить не на что."
    exit 1
}
foreach ($l in $levels) { Write-Info "$($l.name)  ->  id=$($l.id), отметка $($l.elevation_mm) мм" }

$level = $levels | Where-Object { $_.name -eq $LevelName } | Select-Object -First 1
if (-not $level) {
    $level = $levels | Sort-Object { [double]$_.elevation_mm } | Select-Object -First 1
    Write-Info "Уровень '$LevelName' не найден -- взят нижний: '$($level.name)'"
}
Write-Ok "целевой уровень: $($level.name) (id=$($level.id))"

if ($DryRun) {
    Write-Host ""
    Write-Ok "DryRun: связь исправна, стена не создавалась."
    exit 0
}

# -- 4. Тестовая стена --------------------------------------------------------
Write-Step "4/4  walls.create  --  стена $LengthMm мм"
$segments = @(
    @{ start = @{ x = 0; y = 0 }; end = @{ x = $LengthMm; y = 0 } }
)
try {
    $result = Invoke-Bridge 'walls.create' @{
        level_id   = $level.id
        segments   = $segments
        height_mm  = $HeightMm
        structural = $false
    }
} catch {
    Write-Err $_.Exception.Message
    exit 1
}

Write-Host ""
Write-Ok "стена создана"
Write-Info ($result | ConvertTo-Json -Depth 6)
Write-Host ""
Write-Info "Проверьте её в Revit: план уровня '$($level.name)', отрезок от (0,0) до ($LengthMm,0)."
Write-Info "Отменить: Ctrl+Z в Revit."
Write-Host ""
