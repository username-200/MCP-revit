<#
.SYNOPSIS
    Собирает аддин MCP Revit Bridge и устанавливает его в папку аддинов Revit.

.EXAMPLE
    .\scripts\install-addin.ps1 -RevitVersion 2023
    .\scripts\install-addin.ps1 -RevitVersion 2023 -Clean
    .\scripts\install-addin.ps1 -RevitVersion 2025 -Token "s3cret" -NoAutoStart
#>
[CmdletBinding()]
param(
    [ValidateSet("2023", "2024", "2025", "2026")]
    [string]$RevitVersion = "2023",

    [string]$RevitApiDir,

    [int]$Port = 8765,

    [string]$Token = "",

    [switch]$NoAutoStart,

    # Снести прошлую сборку и установленный аддин, затем собрать с нуля.
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "revit-addin\McpRevit\McpRevit.csproj"

if (-not $RevitApiDir) {
    $RevitApiDir = "C:\Program Files\Autodesk\Revit $RevitVersion"
}

if (-not (Test-Path (Join-Path $RevitApiDir "RevitAPI.dll"))) {
    throw "RevitAPI.dll не найден в '$RevitApiDir'. Укажите путь параметром -RevitApiDir."
}

if ($Token -match '[^\x20-\x7E]') {
    throw "Токен должен состоять только из ASCII-символов: он передаётся в HTTP-заголовке."
}

# dotnet может быть установлен как один только runtime — для сборки нужен SDK.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw @"
Команда 'dotnet' не найдена. Установите .NET SDK:
    winget install Microsoft.DotNet.SDK.8
или скачайте с https://dotnet.microsoft.com/download
"@
}

$sdks = & dotnet --list-sdks 2>$null
if (-not $sdks) {
    throw @"
Установлен только runtime .NET, а для сборки нужен SDK. Поставьте его:
    winget install Microsoft.DotNet.SDK.8
или скачайте с https://dotnet.microsoft.com/download
После установки откройте новое окно PowerShell и повторите запуск.
"@
}

$addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$targetDir = Join-Path $addinsDir "McpRevit"

# Загруженный аддин держит свою DLL открытой, и копирование поверх падает.
if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    if (Test-Path (Join-Path $targetDir "McpRevit.dll")) {
        throw "Revit запущен и держит установленный аддин. Закройте Revit и повторите."
    }
    Write-Warning "Revit запущен: перезапустите его после установки, иначе аддин не подхватится."
}

if ($Clean) {
    Write-Host "Очистка прошлой сборки и установленного аддина..." -ForegroundColor Cyan
    foreach ($dir in @("bin", "obj")) {
        $path = Join-Path $root "revit-addin\McpRevit\$dir"
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
            Write-Host "  удалено: $path" -ForegroundColor DarkGray
        }
    }
    foreach ($path in @($targetDir, (Join-Path $addinsDir "McpRevit.addin"))) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
            Write-Host "  удалено: $path" -ForegroundColor DarkGray
        }
    }
}

Write-Host "Сборка аддина для Revit $RevitVersion..." -ForegroundColor Cyan
dotnet build $project -c Release -p:RevitVersion=$RevitVersion -p:RevitApiDir=$RevitApiDir
if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с ошибкой." }

# Путь к сборке зависит от платформы и TFM, поэтому ищем свежий McpRevit.dll,
# а не полагаемся на bin\Release.
$binDir = Join-Path $root "revit-addin\McpRevit\bin"
$built = Get-ChildItem -Path $binDir -Recurse -Filter "McpRevit.dll" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $built) {
    throw "Сборка McpRevit.dll не найдена в '$binDir'. Проверьте вывод dotnet build выше."
}

$output = $built.Directory.FullName
Write-Host "Собрано: $($built.FullName)" -ForegroundColor DarkGray

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

Copy-Item $built.FullName $targetDir -Force
Copy-Item (Join-Path $output "McpRevit.pdb") $targetDir -Force -ErrorAction SilentlyContinue

# Манифест лежит уровнем выше, а сборка — в подпапке: правим путь к DLL.
$manifest = Get-Content (Join-Path $output "McpRevit.addin") -Raw
$manifest = $manifest -replace "<Assembly>McpRevit.dll</Assembly>", "<Assembly>McpRevit\McpRevit.dll</Assembly>"
Set-Content -Path (Join-Path $addinsDir "McpRevit.addin") -Value $manifest -Encoding UTF8

$config = [ordered]@{
    port       = $Port
    token      = $Token
    auto_start = -not $NoAutoStart.IsPresent
}
$config | ConvertTo-Json | Set-Content -Path (Join-Path $targetDir "mcp-revit.config.json") -Encoding UTF8

Write-Host "Готово." -ForegroundColor Green
Write-Host "  Аддин:  $targetDir"
Write-Host "  Адрес:  http://127.0.0.1:$Port"
Write-Host "  Токен:  $(if ($Token) { 'задан' } else { 'не используется' })"
Write-Host ""
Write-Host "Запустите Revit и откройте проект. Проверить мост: curl http://127.0.0.1:$Port/health"
