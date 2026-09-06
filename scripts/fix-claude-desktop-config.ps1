#Requires -Version 5.1
<#
.SYNOPSIS
    Диагностика и починка claude_desktop_config.json для сервера revit-mcp.

.DESCRIPTION
    Ручная правка конфигурации Claude Desktop ломается предсказуемо: вставленный
    блок даёт два корневых объекта, в путях остаётся плейсхолдер, слэши не
    удвоены, а Set-Content в PowerShell 5.1 дописывает BOM, на котором спотыкается
    разбор JSON. Скрипт проверяет всё это и собирает конфигурацию заново, находя
    node.exe и index.js на диске сам.

    Существующий файл сохраняется в .bak.<дата> перед любой записью. Другие
    MCP-серверы в конфигурации сохраняются, если файл удалось разобрать.

.PARAMETER RevitVersion
    Для какой версии Revit прописать сервер. По умолчанию берётся самая новая
    из найденных установок плагина.

.PARAMETER Check
    Только проверить и показать диагноз, ничего не записывать.

.EXAMPLE
    .\fix-claude-desktop-config.ps1 -Check
    .\fix-claude-desktop-config.ps1 -RevitVersion 2026
#>
param(
    [ValidateSet('2023','2024','2025','2026','2027')]
    [string]$RevitVersion,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

function Write-Ok   { param($m) Write-Host "  [+] $m" -ForegroundColor Green  }
function Write-Err  { param($m) Write-Host "  [-] $m" -ForegroundColor Red    }
function Write-Warn { param($m) Write-Host "  [!] $m" -ForegroundColor Yellow }
function Write-Step { param($m) Write-Host "  [*] $m" -ForegroundColor Cyan   }
function Write-Info { param($m) Write-Host "      $m" -ForegroundColor Gray   }

Write-Host ""
Write-Host "  Проверка конфигурации Claude Desktop" -ForegroundColor White
Write-Host ""

# -- 1. Найти каталог Claude Desktop ------------------------------------------
$claudeDir = $null
foreach ($c in @(
    "$env:APPDATA\Claude",
    (Get-ChildItem "$env:LOCALAPPDATA\Packages" -Filter 'Claude_*' -ErrorAction SilentlyContinue |
        Select-Object -First 1 | ForEach-Object { "$($_.FullName)\LocalCache\Roaming\Claude" })
)) {
    if ($c -and (Test-Path $c)) { $claudeDir = $c; break }
}
if (-not $claudeDir) {
    Write-Err "Каталог Claude Desktop не найден -- приложение установлено?"
    exit 1
}
$configPath = Join-Path $claudeDir 'claude_desktop_config.json'
Write-Ok "каталог: $claudeDir"

# -- 2. Разобрать текущий файл ------------------------------------------------
$config = $null
if (Test-Path $configPath) {
    $bytes = [IO.File]::ReadAllBytes($configPath)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    if ($hasBom) {
        Write-Warn "файл начинается с BOM -- разбор JSON на нём падает"
    }
    # BOM снимаем до разбора, иначе ConvertFrom-Json спотыкается о него так же.
    $raw = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)

    try {
        $config = $raw | ConvertFrom-Json
        Write-Ok "JSON корректен"
    } catch {
        Write-Err "JSON повреждён: $($_.Exception.Message)"

        # Два корневых объекта -- самый частый след ручной вставки.
        if ($raw -match '\}\s*\{') {
            Write-Info "Найдено '}{' -- похоже, блок вставлен рядом с существующим объектом,"
            Write-Info "а не внутрь него. Это делает файл невалидным."
        }
        if ($raw -match '<[^>]+>') {
            Write-Info "В файле остался плейсхолдер вида <...> -- его нужно было заменить."
        }
        if ($raw -match '(?<!\\)\\(?![\\"/bfnrtu])') {
            Write-Info "Одиночный обратный слэш в строке -- в JSON пути пишутся как C:\\\\Users\\\\..."
        }
        Write-Host ""
        Write-Info "Скрипт соберёт конфигурацию заново. Прочие MCP-серверы из"
        Write-Info "повреждённого файла восстановить нельзя -- добавьте их потом вручную"
        Write-Info "из резервной копии."
    }
} else {
    Write-Warn "файла нет -- будет создан"
}

# -- 3. Найти сервер и Node ---------------------------------------------------
$years = if ($RevitVersion) { @($RevitVersion) } else { 2027..2023 }
$serverJs = $null; $nodeExe = $null; $foundYear = $null

foreach ($y in $years) {
    $root = "$env:APPDATA\Autodesk\Revit\Addins\$y\revit_mcp_plugin\Commands\RevitMCPCommandSet\server"
    if (Test-Path "$root\build\index.js") {
        $serverJs  = "$root\build\index.js"
        $foundYear = $y
        if (Test-Path "$root\runtime\node.exe") { $nodeExe = "$root\runtime\node.exe" }
        break
    }
}

if (-not $serverJs) {
    Write-Err "MCP-сервер не найден в папках Addins."
    Write-Info "Плагин не установлен либо установлен для другой версии Revit."
    Write-Info "Переустановите: install.ps1 -RevitVersion 2026"
    exit 1
}
Write-Ok "сервер (Revit $foundYear): $serverJs"

if (-not $nodeExe) {
    $sys = Get-Command node -ErrorAction SilentlyContinue
    if ($sys) {
        $nodeExe = $sys.Source
        Write-Warn "портативный node.exe не найден, взят системный: $nodeExe"
    } else {
        Write-Err "Node.js не найден -- ни портативный, ни системный."
        Write-Info "Переустановите плагин из релизного ZIP: в нём node.exe идёт в комплекте."
        exit 1
    }
} else {
    Write-Ok "node: $nodeExe"
}

if ($Check) {
    Write-Host ""
    Write-Ok "Режим проверки -- файл не изменён."
    exit 0
}

# -- 4. Собрать и записать ----------------------------------------------------
# Работаем через упорядоченный словарь: у PSCustomObject из ConvertFrom-Json
# добавление вложенного ключа требует Add-Member на каждом уровне.
$servers = [ordered]@{}
if ($config -and $config.mcpServers) {
    foreach ($p in $config.mcpServers.PSObject.Properties) {
        if ($p.Name -ne 'revit-mcp') { $servers[$p.Name] = $p.Value }
    }
}
$servers['revit-mcp'] = [ordered]@{ command = $nodeExe; args = @($serverJs) }

$root = [ordered]@{}
if ($config) {
    foreach ($p in $config.PSObject.Properties) {
        if ($p.Name -ne 'mcpServers') { $root[$p.Name] = $p.Value }
    }
}
$root['mcpServers'] = $servers

if (Test-Path $configPath) {
    $backup = "$configPath.bak.$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item $configPath $backup -Force
    Write-Ok "резервная копия: $backup"
}

# UTF-8 строго без BOM: Set-Content -Encoding UTF8 в PS 5.1 добавляет его,
# и разбор конфигурации в Claude Desktop падает на первом же символе.
$json = $root | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($configPath, $json, (New-Object Text.UTF8Encoding($false)))

Write-Host ""
Write-Ok "записано: $configPath"
Write-Host ""
Write-Host $json -ForegroundColor DarkGray
Write-Host ""
Write-Info "Закройте Claude Desktop полностью (значок в трее -> Quit) и запустите снова."
Write-Host ""
