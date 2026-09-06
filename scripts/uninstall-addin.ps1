<#
.SYNOPSIS
    Снимает аддин MCP Revit Bridge, установленный прежней версией этого проекта.

.DESCRIPTION
    Мост заменён плагином LuDattilo/revit-mcp-server, поэтому старый аддин нужно
    убрать: он занимает порт 8765 и добавляет лишнюю вкладку на ленту Revit.

.EXAMPLE
    .\scripts\uninstall-addin.ps1
    .\scripts\uninstall-addin.ps1 -RevitVersion 2026
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # По умолчанию чистятся все версии, где аддин мог остаться.
    [string[]]$RevitVersion = @("2023", "2024", "2025", "2026", "2027")
)

$ErrorActionPreference = "Stop"

if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    throw "Revit запущен и держит файлы аддина. Закройте Revit и повторите."
}

$removed = 0

foreach ($version in $RevitVersion) {
    $addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$version"
    if (-not (Test-Path $addinsDir)) { continue }

    foreach ($item in @((Join-Path $addinsDir "McpRevit.addin"), (Join-Path $addinsDir "McpRevit"))) {
        if (Test-Path $item) {
            if ($PSCmdlet.ShouldProcess($item, "Удалить")) {
                Remove-Item $item -Recurse -Force
                Write-Host "  удалено: $item" -ForegroundColor DarkGray
                $removed++
            }
        }
    }
}

if ($removed -eq 0) {
    Write-Host "Аддин MCP Revit Bridge не найден — вероятно, уже снят." -ForegroundColor Yellow
}
else {
    Write-Host "Готово: удалено объектов — $removed." -ForegroundColor Green
}

Write-Host ""
Write-Host "Дальше: установка плагина LuDattilo — см. docs/SETUP.md" -ForegroundColor Cyan
