<#
.SYNOPSIS
    Находит файл скана и подключает его к открытому в Revit проекту через мост.

.DESCRIPTION
    Без -Path ищет .rcp (при отсутствии — .rcs) в папке -SearchDir, поэтому путь
    не нужно переписывать вручную: скрытые расширения и пробелы в именах
    перестают быть проблемой.

.EXAMPLE
    .\scripts\link-pointcloud.ps1
    .\scripts\link-pointcloud.ps1 -SearchDir D:\Сканы
    .\scripts\link-pointcloud.ps1 -OffsetXMm 1200 -OffsetYMm -450 -RotationDeg 90
#>
[CmdletBinding()]
param(
    [string]$Path,

    [string]$SearchDir = "C:\Scan",

    [double]$OffsetXMm = 0,

    [double]$OffsetYMm = 0,

    [double]$OffsetZMm = 0,

    [double]$RotationDeg = 0,

    [int]$Port = 8765,

    [string]$Token = ""
)

$ErrorActionPreference = "Stop"

function Find-Scan([string]$dir) {
    if (-not (Test-Path $dir)) {
        throw "Папка '$dir' не найдена. Укажите другую: -SearchDir 'путь'"
    }

    $files = Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in ".rcp", ".rcs" }

    if (-not $files) {
        throw "В '$dir' нет ни одного .rcp или .rcs. Сконвертируйте скан в Autodesk ReCap."
    }

    # .rcp — сборка сканов с их взаимной привязкой, .rcs — один скан.
    $projects = @($files | Where-Object Extension -eq ".rcp")
    $candidates = if ($projects) { $projects } else { @($files) }

    if ($candidates.Count -gt 1) {
        Write-Host "Найдено несколько файлов:" -ForegroundColor Yellow
        $candidates | ForEach-Object { Write-Host "  $($_.FullName)" }
        throw "Выберите нужный и укажите его: -Path 'путь'"
    }

    if (-not $projects) {
        Write-Warning ".rcp не найден, подключается отдельный скан .rcs — объект может быть неполным."
    }

    return $candidates[0].FullName
}

if ($Path) {
    if (-not (Test-Path $Path)) {
        Write-Host "Файла '$Path' на диске нет. Что лежит рядом:" -ForegroundColor Yellow
        $parent = Split-Path -Parent $Path
        if ($parent -and (Test-Path $parent)) {
            Get-ChildItem $parent -File | ForEach-Object { Write-Host "  $($_.Name)" }
        }
        throw "Проверьте имя файла: проводник скрывает расширения, и '.rcp' может быть частью имени."
    }
}
else {
    $Path = Find-Scan $SearchDir
}

$Path = (Resolve-Path $Path).Path
Write-Host "Файл: $Path" -ForegroundColor Cyan

$extension = [System.IO.Path]::GetExtension($Path).TrimStart(".").ToLowerInvariant()
if ($extension -notin @("rcp", "rcs")) {
    throw "Revit принимает только .rcp и .rcs, а здесь '.$extension'. Сконвертируйте скан в ReCap."
}

$body = @{
    command = "pointcloud.link"
    params  = @{
        path         = $Path
        offset_x_mm  = $OffsetXMm
        offset_y_mm  = $OffsetYMm
        offset_z_mm  = $OffsetZMm
        rotation_deg = $RotationDeg
    }
} | ConvertTo-Json -Depth 5

$headers = @{}
if ($Token) { $headers["X-Mcp-Token"] = $Token }

Write-Host "Подключение к Revit... (крупный скан обрабатывается минутами)" -ForegroundColor Cyan

try {
    $response = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$Port/command" `
        -Body $body -ContentType "application/json" -Headers $headers -TimeoutSec 900
}
catch [System.Net.WebException] {
    # Мост отдаёт причину отказа в теле ответа, а PowerShell показывает только статус.
    $stream = $null
    if ($_.Exception.Response) { $stream = $_.Exception.Response.GetResponseStream() }
    if ($stream) {
        $text = (New-Object System.IO.StreamReader($stream)).ReadToEnd()
        try { $message = (ConvertFrom-Json $text).error.message } catch { $message = $text }
        throw "Мост отказал: $message"
    }
    throw "Мост на 127.0.0.1:$Port недоступен. Запущен ли Revit и включён ли мост на вкладке MCP?"
}

if (-not $response.ok) {
    throw "Мост отказал: $($response.error.message)"
}

$cloud = $response.result
Write-Host "Готово." -ForegroundColor Green
Write-Host "  id:       $($cloud.id)   <- понадобится для обзора, уровней и стен"
Write-Host "  название: $($cloud.name)"

if ($cloud.bounding_box) {
    $min = $cloud.bounding_box.min
    $max = $cloud.bounding_box.max
    Write-Host ("  габариты: {0:N0} x {1:N0} x {2:N0} мм" -f `
        ($max.x - $min.x), ($max.y - $min.y), ($max.z - $min.z))
}
