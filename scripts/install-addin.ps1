<#
.SYNOPSIS
    Собирает аддин MCP Revit Bridge и устанавливает его в папку аддинов Revit.

.EXAMPLE
    .\scripts\install-addin.ps1 -RevitVersion 2023
    .\scripts\install-addin.ps1 -RevitVersion 2025 -Token "s3cret" -NoAutoStart
#>
[CmdletBinding()]
param(
    [ValidateSet("2023", "2024", "2025", "2026")]
    [string]$RevitVersion = "2023",

    [string]$RevitApiDir,

    [int]$Port = 8765,

    [string]$Token = "",

    [switch]$NoAutoStart
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

$addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$targetDir = Join-Path $addinsDir "McpRevit"

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
