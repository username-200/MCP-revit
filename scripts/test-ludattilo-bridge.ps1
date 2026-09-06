#Requires -Version 5.1
<#
.SYNOPSIS
    Сквозная диагностика связки Claude Desktop -- MCP -- плагин LuDattilo в Revit.

.DESCRIPTION
    Проверяет обе половины цепочки по отдельности, чтобы отличить неисправность
    моста в Revit от неисправности конфигурации Claude Desktop:

      A. Сторона Revit -- TCP-сокет плагина, JSON-RPC 2.0 с разделением по \n.
         Опрашивается напрямую, минуя Claude Desktop.
      B. Сторона Desktop -- конфигурация, наличие node.exe и index.js.

    С ключом -CreateWall дополнительно строит стену длиной 1 м: это
    подтверждает, что через мост проходят не только чтения, но и транзакции.

.PARAMETER Port
    Порт плагина. По умолчанию читается из mcp-port.txt, иначе перебирается
    диапазон 8080-8089.

.PARAMETER RevitVersion
    Версия Revit для поиска файлов плагина. По умолчанию 2026.

.PARAMETER CreateWall
    Построить тестовую стену 1000 мм на уровне с наименьшей отметкой.

.EXAMPLE
    .\test-ludattilo-bridge.ps1
    .\test-ludattilo-bridge.ps1 -CreateWall
#>
param(
    [int]$Port,
    [ValidateSet('2023','2024','2025','2026','2027')]
    [string]$RevitVersion = '2026',
    [switch]$CreateWall
)

$ErrorActionPreference = 'Stop'

function Write-Ok   { param($m) Write-Host "  [+] $m" -ForegroundColor Green  }
function Write-Err  { param($m) Write-Host "  [-] $m" -ForegroundColor Red    }
function Write-Warn { param($m) Write-Host "  [!] $m" -ForegroundColor Yellow }
function Write-Step { param($m) Write-Host "  [*] $m" -ForegroundColor Cyan   }
function Write-Info { param($m) Write-Host "      $m" -ForegroundColor Gray   }
function Write-Head { param($m) Write-Host ""; Write-Host "  $m" -ForegroundColor White }

$pluginDir = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion\revit_mcp_plugin"
$serverDir = "$pluginDir\Commands\RevitMCPCommandSet\server"

# =============================================================================
# A. СТОРОНА REVIT -- сокет плагина
# =============================================================================
Write-Head "A. Мост в Revit $RevitVersion"

if (-not $Port) {
    $portFile = Join-Path $pluginDir 'mcp-port.txt'
    if (Test-Path $portFile) {
        $fromFile = (Get-Content $portFile -Raw).Trim()
        if ($fromFile -match '^\d+$') { $Port = [int]$fromFile; Write-Info "порт из mcp-port.txt: $Port" }
    }
}

$candidates = if ($Port) { @($Port) } else { 8080..8089 }
$livePort = $null
foreach ($p in $candidates) {
    $probe = New-Object Net.Sockets.TcpClient
    try {
        # Короткий таймаут: закрытый порт на локальной петле отвечает мгновенно.
        if ($probe.ConnectAsync('127.0.0.1', $p).Wait(700) -and $probe.Connected) {
            $livePort = $p; $probe.Close(); break
        }
    } catch { } finally { $probe.Dispose() }
}

if (-not $livePort) {
    Write-Err "Ни один порт из $($candidates[0])..$($candidates[-1]) не отвечает."
    Write-Info "В Revit: вкладка Add-Ins -> панель 'Revit MCP Plugin' -> кнопка 'Revit MCP Switch'."
    Write-Info "Проверьте также, что открыт проект, а не пустой экран Revit."
    exit 1
}
Write-Ok "плагин слушает 127.0.0.1:$livePort"

function Invoke-RevitRpc {
    param([string]$Method, $Params = @{}, [int]$TimeoutSec = 120)

    $client = New-Object Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', $livePort)
        $stream = $client.GetStream()
        $stream.ReadTimeout = $TimeoutSec * 1000

        $req = [ordered]@{ jsonrpc = '2.0'; method = $Method; params = $Params; id = [guid]::NewGuid().ToString('N').Substring(0,8) }
        # Плагин режет поток по \n, поэтому перевод строки обязателен.
        $line  = ($req | ConvertTo-Json -Depth 12 -Compress) + "`n"
        $bytes = [Text.Encoding]::UTF8.GetBytes($line)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()

        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
        $resp = $reader.ReadLine()
        if (-not $resp) { throw "Плагин закрыл соединение, не ответив." }
        return $resp | ConvertFrom-Json
    } finally {
        $client.Close(); $client.Dispose()
    }
}

Write-Step "get_project_info"
try {
    $info = Invoke-RevitRpc 'get_project_info' @{ includeLevels = $true }
} catch {
    Write-Err "Запрос не прошёл: $($_.Exception.Message)"
    Write-Info "Порт открыт, но плагин не отвечает по протоколу -- вероятно, в Revit открыт модальный диалог."
    exit 1
}

if ($info.error) {
    Write-Err "Плагин вернул ошибку: $($info.error.message)"
    exit 1
}
if (-not $info.result.Success) {
    Write-Err "Плагин отказал: $($info.result.Message)"
    exit 1
}
Write-Ok "модель отвечает"
# Полезная нагрузка лежит на два уровня глубже: result.Response, не result.
$payload = $info.result.Response
Write-Info ($payload | ConvertTo-Json -Depth 6)

# Отметка уровня нужна как baseLevel: плагин ждёт миллиметры, а не ID уровня.
$levels = @($payload.levels)
$baseLevelMm = 0
if ($levels.Count -gt 0) {
    $lowest = $levels | Sort-Object { [double]$_.elevation } | Select-Object -First 1
    $baseLevelMm = [double]$lowest.elevation
    Write-Ok "нижний уровень: '$($lowest.name)' на отметке $baseLevelMm"
} else {
    Write-Warn "уровни не пришли -- baseLevel останется 0"
}

# =============================================================================
# B. СТОРОНА CLAUDE DESKTOP
# =============================================================================
Write-Head "B. Конфигурация Claude Desktop"

$cfg = "$env:APPDATA\Claude\claude_desktop_config.json"
if (-not (Test-Path $cfg)) {
    Write-Err "Файл не найден: $cfg"
} else {
    $bytes = [IO.File]::ReadAllBytes($cfg)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        Write-Err "файл начинается с BOM -- разбор JSON падает на первом символе"
    }
    $raw = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)
    try {
        $parsed = $raw | ConvertFrom-Json
        Write-Ok "JSON корректен ($($raw.Length) символов)"

        $entry = $parsed.mcpServers.'revit-mcp'
        if (-not $entry) {
            Write-Err "в mcpServers нет ключа 'revit-mcp'"
        } else {
            foreach ($pair in @(@{ L = 'command'; P = $entry.command }, @{ L = 'args[0]'; P = $entry.args[0] })) {
                if (Test-Path $pair.P) { Write-Ok "$($pair.L): $($pair.P)" }
                else { Write-Err "$($pair.L) указывает на несуществующий файл: $($pair.P)" }
            }
        }
    } catch {
        Write-Err "JSON повреждён: $($_.Exception.Message)"
        if ($raw -match '\}\s*\{') { Write-Info "Найдено '}{' -- в файле два корневых объекта." }
    }
}

Write-Step "исполняемые файлы сервера"
foreach ($pair in @(
    @{ L = 'node.exe'; P = "$serverDir\runtime\node.exe" },
    @{ L = 'index.js'; P = "$serverDir\build\index.js"   }
)) {
    if (Test-Path $pair.P) { Write-Ok "$($pair.L) на месте" } else { Write-Err "$($pair.L) отсутствует: $($pair.P)" }
}

$nodeExe = "$serverDir\runtime\node.exe"
if (Test-Path $nodeExe) {
    try { Write-Ok "node $((& $nodeExe --version 2>$null))" }
    catch { Write-Err "node.exe не запускается: $($_.Exception.Message)" }
}

# =============================================================================
# C. ТЕСТОВАЯ СТЕНА
# =============================================================================
if ($CreateWall) {
    Write-Head "C. Тестовая стена 1000 мм"

    # thickness обязателен по схеме, но для стен игнорируется: Wall.Create берёт
    # толщину из типа. typeId не задаём -- плагин подставит первый доступный тип.
    $params = @{
        data = @(@{
            category     = 'OST_Walls'
            locationLine = @{
                p0 = @{ x = 0;    y = 0; z = 0 }
                p1 = @{ x = 1000; y = 0; z = 0 }
            }
            thickness  = 200
            height     = 3000
            baseLevel  = $baseLevelMm
            baseOffset = 0
        })
    }

    try {
        $res = Invoke-RevitRpc 'create_line_based_element' $params
    } catch {
        Write-Err "Команда не прошла: $($_.Exception.Message)"
        exit 1
    }

    if ($res.error) {
        Write-Err "Плагин вернул ошибку: $($res.error.message)"
        exit 1
    }
    if (-not $res.result.Success) {
        Write-Err "Плагин отказал: $($res.result.Message)"
        exit 1
    }
    Write-Ok "стена создана"
    Write-Info ($res.result.Response | ConvertTo-Json -Depth 6)
    Write-Info "Откатить: Ctrl+Z в Revit."
}

Write-Host ""
Write-Ok "Проверка завершена."
Write-Info "Раздел A зелёный, B красный -> проблема в Claude Desktop, плагин ни при чём."
Write-Info "Раздел A красный -> мост в Revit не поднят, конфигурацию править бесполезно."
Write-Host ""
