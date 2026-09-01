<#
.SYNOPSIS
    Строит стены по облаку точек, разделяя их на наружный контур и внутренние перегородки.

.DESCRIPTION
    Мост находит вертикальные плоскости одним вызовом, но не знает, какая из них
    несущая, а какая перегородка. Скрипт делит найденные следы по положению
    относительно габаритов квартиры: след, лежащий у края общего прямоугольника, —
    периметр, остальные — перегородки. Затем каждая группа строится своим типом стены.

    По умолчанию только показывает разбор. Запись в модель — с ключом -Build.

.EXAMPLE
    .\scripts\build-walls-from-cloud.ps1 -PointCloudId 1234 -LevelId 311
    .\scripts\build-walls-from-cloud.ps1 -PointCloudId 1234 -LevelId 311 `
        -PerimeterTypeId 352 -PartitionTypeId 353 -Build
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [long]$PointCloudId,

    [Parameter(Mandatory)]
    [long]$LevelId,

    # Типоразмеры стен из types.list: наружные и внутренние.
    [long]$PerimeterTypeId = -1,
    [long]$PartitionTypeId = -1,

    # Насколько близко к краю габарита должен лежать след, чтобы считаться периметром.
    [double]$PerimeterToleranceMm = 600,

    [double]$MinLengthMm = 800,
    [double]$SnapAngleDeg = 90,
    [double]$HeightMm = 0,

    [int]$MaxPoints = 60000,
    [double]$DistanceToleranceMm = 25,
    [int]$MaxPlanes = 40,

    [switch]$Build,

    [int]$Port = 8765,
    [string]$Token = ""
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "McpRevit.psm1") -Force

$connection = @{ Port = $Port; Token = $Token }

Write-Host "Поиск вертикальных плоскостей в облаке $PointCloudId..." -ForegroundColor Cyan

$detected = Invoke-McpRevit "pointcloud.detect_planes" @{
    id                    = $PointCloudId
    max_points            = $MaxPoints
    distance_tolerance_mm = $DistanceToleranceMm
    max_planes            = $MaxPlanes
    filter_kind           = "vertical"
} @connection

$walls = @($detected.planes | Where-Object { $_.trace -and $_.trace.length_mm -ge $MinLengthMm })

if (-not $walls) {
    throw @"
Подходящих вертикальных плоскостей не найдено.
Увеличьте -MaxPoints или -DistanceToleranceMm, либо уменьшите -MinLengthMm.
"@
}

# Габариты квартиры в плане — по концам всех найденных следов.
$xs = $walls | ForEach-Object { $_.trace.start.x; $_.trace.end.x }
$ys = $walls | ForEach-Object { $_.trace.start.y; $_.trace.end.y }

$minX = ($xs | Measure-Object -Minimum).Minimum
$maxX = ($xs | Measure-Object -Maximum).Maximum
$minY = ($ys | Measure-Object -Minimum).Minimum
$maxY = ($ys | Measure-Object -Maximum).Maximum

Write-Host ("Габариты в плане: {0:N0} x {1:N0} мм" -f ($maxX - $minX), ($maxY - $minY))

$perimeter = @()
$partitions = @()
$report = @()

foreach ($plane in $walls) {
    $midX = ($plane.trace.start.x + $plane.trace.end.x) / 2
    $midY = ($plane.trace.start.y + $plane.trace.end.y) / 2

    $atEdge =
        ($midX -le $minX + $PerimeterToleranceMm) -or ($midX -ge $maxX - $PerimeterToleranceMm) -or
        ($midY -le $minY + $PerimeterToleranceMm) -or ($midY -ge $maxY - $PerimeterToleranceMm)

    if ($atEdge) { $perimeter += $plane } else { $partitions += $plane }

    $report += [PSCustomObject]@{
        Группа   = if ($atEdge) { "периметр" } else { "перегородка" }
        ДлинаМм  = [math]::Round($plane.trace.length_mm)
        ВысотаМм = [math]::Round($plane.max_z_mm - $plane.min_z_mm)
        Азимут   = [math]::Round($plane.heading_deg, 1)
        Точек    = $plane.inlier_count
    }
}

$report |
    Sort-Object @{ Expression = "Группа" }, @{ Expression = "ДлинаМм"; Descending = $true } |
    Format-Table -AutoSize

Write-Host ("Периметр: {0}   перегородки: {1}" -f $perimeter.Count, $partitions.Count) -ForegroundColor Cyan

if (-not $Build) {
    Write-Host ""
    Write-Host "Это разбор без записи в модель. Проверьте деление на группы и азимуты," -ForegroundColor Yellow
    Write-Host "затем повторите с -Build и указанием -PerimeterTypeId / -PartitionTypeId." -ForegroundColor Yellow
    return
}

if ($PerimeterTypeId -lt 0 -or $PartitionTypeId -lt 0) {
    Write-Host "Доступные типы стен:" -ForegroundColor Yellow
    (Invoke-McpRevit "types.list" @{ kind = "wall" } @connection).types |
        Format-Table id, family, name -AutoSize
    throw "Укажите -PerimeterTypeId и -PartitionTypeId из списка выше."
}

function Build-Group($planes, [long]$typeId, [string]$title) {
    if (-not $planes) {
        Write-Warning "$title : нечего строить."
        return
    }

    $params = @{
        planes        = $planes
        level_id      = $LevelId
        wall_type_id  = $typeId
        min_length_mm = $MinLengthMm
        snap_angle_deg = $SnapAngleDeg
    }
    if ($HeightMm -gt 0) { $params["height_mm"] = $HeightMm }

    $result = Invoke-McpRevit "walls.from_planes" $params @connection
    Write-Host ("{0}: создано {1}, пропущено плоскостей {2}" -f `
        $title, $result.created_count, $result.skipped_planes) -ForegroundColor Green

    if ($result.failed) {
        foreach ($failure in $result.failed) {
            Write-Warning ("{0}, отрезок {1}: {2}" -f $title, $failure.index, $failure.reason)
        }
    }
}

Build-Group $perimeter $PerimeterTypeId "Наружные стены"
Build-Group $partitions $PartitionTypeId "Перегородки"

Write-Host ""
Write-Host "Проверьте модель и сохраните проект (Ctrl+S)." -ForegroundColor Cyan
