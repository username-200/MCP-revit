#Requires -Version 5.1
<#
.SYNOPSIS
    Настройка Claude Desktop на работу с плагином revit-mcp и проверка результата.

.DESCRIPTION
    Собирает claude_desktop_config.json из всех уцелевших копий, чтобы ранее
    настроенные серверы не потерялись, добавляет к ним revit-mcp с путями,
    найденными на диске, и записывает файл в UTF-8 без BOM.

    С ключом -Restart дополнительно перезапускает Claude Desktop и ждёт
    появления лога mcp-server-revit-mcp.log -- это единственное прямое
    доказательство, что приложение нашло сервер и запустило его.

.PARAMETER RevitVersion
    Версия Revit, чей плагин прописать. По умолчанию 2026.

.PARAMETER Restart
    Закрыть и запустить Claude Desktop, затем дождаться лога сервера.

.PARAMETER WaitSeconds
    Сколько ждать появления лога после запуска. По умолчанию 45.

.EXAMPLE
    .\setup-claude-desktop-revit.ps1
    .\setup-claude-desktop-revit.ps1 -Restart
#>
param(
    [ValidateSet('2023','2024','2025','2026','2027')]
    [string]$RevitVersion = '2026',
    [switch]$Restart,
    [int]$WaitSeconds = 45
)

$ErrorActionPreference = 'Stop'

function Write-Ok   { param($m) Write-Host "  [+] $m" -ForegroundColor Green  }
function Write-Err  { param($m) Write-Host "  [-] $m" -ForegroundColor Red    }
function Write-Warn { param($m) Write-Host "  [!] $m" -ForegroundColor Yellow }
function Write-Step { param($m) Write-Host "  [*] $m" -ForegroundColor Cyan   }
function Write-Info { param($m) Write-Host "      $m" -ForegroundColor Gray   }
function Write-Head { param($m) Write-Host ""; Write-Host "  $m" -ForegroundColor White }

$claudeDir = "$env:APPDATA\Claude"
$cfgPath   = Join-Path $claudeDir 'claude_desktop_config.json'
$logDir    = Join-Path $claudeDir 'logs'
$revitLog  = Join-Path $logDir 'mcp-server-revit-mcp.log'

if (-not (Test-Path $claudeDir)) {
    Write-Err "Каталог Claude Desktop не найден: $claudeDir"
    exit 1
}

# =============================================================================
# 1. ПУТИ ПЛАГИНА
# =============================================================================
Write-Head "1. Файлы MCP-сервера (Revit $RevitVersion)"

$srvDir  = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion\revit_mcp_plugin\Commands\RevitMCPCommandSet\server"
$indexJs = "$srvDir\build\index.js"
$nodeExe = "$srvDir\runtime\node.exe"

if (-not (Test-Path $indexJs)) {
    Write-Err "Сервер не найден: $indexJs"
    Write-Info "Плагин не установлен для Revit $RevitVersion."
    exit 1
}
Write-Ok "index.js"

if (-not (Test-Path $nodeExe)) {
    $sys = Get-Command node -ErrorAction SilentlyContinue
    if (-not $sys) {
        Write-Err "Node.js не найден -- ни портативный, ни системный."
        exit 1
    }
    $nodeExe = $sys.Source
    Write-Warn "портативного node.exe нет, взят системный: $nodeExe"
} else {
    Write-Ok "node.exe"
}

# =============================================================================
# 2. СБОРКА КОНФИГУРАЦИИ
# =============================================================================
Write-Head "2. Конфигурация"

# Ранняя правка вручную могла стереть соседние серверы, поэтому собираем их
# из всех копий: от старой к свежей, чтобы позднее описание перекрыло раннее.
$servers = [ordered]@{}
Get-ChildItem $claudeDir -Filter 'claude_desktop_config.json*' |
    Sort-Object LastWriteTime |
    ForEach-Object {
        $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($_.FullName)).TrimStart([char]0xFEFF)
        try {
            $found = ($text | ConvertFrom-Json).mcpServers
            if ($found) {
                $names = @($found.PSObject.Properties.Name)
                Write-Info "$($_.Name): $($names -join ', ')"
                $found.PSObject.Properties | ForEach-Object { $servers[$_.Name] = $_.Value }
            }
        } catch {
            Write-Info "$($_.Name): не разбирается, пропущен"
        }
    }

$servers['revit-mcp'] = [ordered]@{ command = $nodeExe; args = @($indexJs) }

if (Test-Path $cfgPath) {
    $backup = "$cfgPath.bak.$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item $cfgPath $backup -Force
    Write-Ok "копия: $(Split-Path $backup -Leaf)"
}

$out = [ordered]@{ mcpServers = $servers }
# Строго без BOM: Set-Content -Encoding UTF8 в PS 5.1 добавляет его, и разбор
# конфигурации в Claude Desktop падает на первом же символе.
[IO.File]::WriteAllText($cfgPath, ($out | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))

Write-Ok "серверов записано: $($servers.Count) -- $($servers.Keys -join ', ')"

# Перечитываем с диска: так проверяется именно то, что прочтёт Claude Desktop.
try {
    $check = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($cfgPath)).TrimStart([char]0xFEFF) | ConvertFrom-Json
    foreach ($n in $check.mcpServers.PSObject.Properties.Name) {
        $e = $check.mcpServers.$n
        $okCmd = [bool]$e.command -and (Test-Path $e.command)
        if ($okCmd) { Write-Ok "$n -- команда на месте" } else { Write-Warn "$n -- команда недоступна: $($e.command)" }
    }
} catch {
    Write-Err "Записанный файл не разбирается: $($_.Exception.Message)"
    exit 1
}

# =============================================================================
# 3. МОСТ В REVIT
# =============================================================================
Write-Head "3. Мост в Revit"

$live = $null
foreach ($p in 8080..8089) {
    $probe = New-Object Net.Sockets.TcpClient
    try { if ($probe.ConnectAsync('127.0.0.1', $p).Wait(700) -and $probe.Connected) { $live = $p; $probe.Close(); break } }
    catch { } finally { $probe.Dispose() }
}
if ($live) {
    Write-Ok "плагин слушает порт $live"
} else {
    Write-Warn "порты 8080-8089 молчат -- откройте проект в Revit и нажмите 'Revit MCP Switch'"
    Write-Info "Конфигурацию это не ломает: сервер подключится, когда мост поднимется."
}

# =============================================================================
# 4. ПЕРЕЗАПУСК
# =============================================================================
if (-not $Restart) {
    Write-Head "Готово"
    Write-Info "Закройте Claude Desktop через значок в трее (Quit) и запустите снова,"
    Write-Info "либо повторите запуск скрипта с ключом -Restart."
    Write-Host ""
    exit 0
}

Write-Head "4. Перезапуск Claude Desktop"

# Путь берём у живого процесса: он точнее любого угадывания по каталогам.
$proc = Get-Process Claude -ErrorAction SilentlyContinue | Select-Object -First 1
$exe  = if ($proc) { $proc.Path } else { $null }

if (-not $exe) {
    foreach ($c in @(
        "$env:LOCALAPPDATA\AnthropicClaude\Claude.exe",
        "$env:LOCALAPPDATA\Programs\Claude\Claude.exe",
        "$env:PROGRAMFILES\Claude\Claude.exe"
    )) { if (Test-Path $c) { $exe = $c; break } }
}
if (-not $exe) {
    $lnk = Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Filter 'Claude*.lnk' -Recurse -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if ($lnk) { $exe = $lnk.FullName }
}

if ($proc) {
    Write-Step "закрываю (pid $($proc.Id))..."
    Get-Process Claude -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    Write-Ok "закрыт"
} else {
    Write-Info "приложение не запущено"
}

# Лог от прошлого сеанса помешал бы отличить новый запуск от старого.
if (Test-Path $revitLog) {
    Move-Item $revitLog "$revitLog.old" -Force
    Write-Info "прежний лог сервера отложен в mcp-server-revit-mcp.log.old"
}

if (-not $exe) {
    Write-Warn "исполняемый файл Claude Desktop не найден -- запустите приложение вручную"
    exit 0
}

Write-Step "запускаю: $exe"
Start-Process $exe
Write-Step "жду появления лога сервера (до $WaitSeconds с)..."

$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    if (Test-Path $revitLog) { break }
    Start-Sleep -Seconds 2
}

Write-Host ""
if (Test-Path $revitLog) {
    Write-Ok "Claude Desktop запустил revit-mcp"
    Write-Info "--- последние строки лога ---"
    Get-Content $revitLog -Tail 20 | ForEach-Object { Write-Info $_ }
    Write-Host ""
    Write-Info "В окне Claude Desktop спросите: «покажи информацию о проекте Revit»."
} else {
    Write-Err "Лог не появился за $WaitSeconds с."
    Write-Info "Посмотрите Settings -> Developer: есть ли revit-mcp в списке серверов."
    Write-Info "Другие логи: $logDir"
    Get-ChildItem $logDir -Filter 'mcp*' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 5 |
        ForEach-Object { Write-Info "$($_.Name)  $($_.LastWriteTime)" }
}
Write-Host ""
