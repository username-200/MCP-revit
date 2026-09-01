<#
    Обёртка над HTTP-мостом: скрывает сборку JSON и достаёт причину отказа
    из тела ответа, которую Invoke-RestMethod прячет за WebException.

    Использование:
        Import-Module .\scripts\McpRevit.psm1 -Force
        Invoke-McpRevit levels.list
        Invoke-McpRevit levels.create @{ elevation_mm = 0; name = "Первый этаж" }
#>

function Invoke-McpRevit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)]
        [string]$Command,

        [Parameter(Position = 1)]
        [hashtable]$Params = @{},

        [int]$Port = 8765,

        [string]$Token = "",

        [int]$TimeoutSec = 900
    )

    $json = @{ command = $Command; params = $Params } | ConvertTo-Json -Depth 20 -Compress
    # Тело отправляем байтами: иначе PowerShell 5.1 кодирует кириллицу не в UTF-8.
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)

    $headers = @{}
    if ($Token) { $headers["X-Mcp-Token"] = $Token }

    try {
        $response = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$Port/command" `
            -Body $bytes -ContentType "application/json; charset=utf-8" `
            -Headers $headers -TimeoutSec $TimeoutSec
    }
    catch [System.Net.WebException] {
        $stream = $null
        if ($_.Exception.Response) { $stream = $_.Exception.Response.GetResponseStream() }

        if ($stream) {
            $text = (New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)).ReadToEnd()
            try { $message = (ConvertFrom-Json $text).error.message } catch { $message = $text }
            throw "[$Command] $message"
        }

        throw "Мост на 127.0.0.1:$Port недоступен: запущен ли Revit и включён ли мост на вкладке MCP?"
    }

    if (-not $response.ok) {
        throw "[$Command] $($response.error.message)"
    }

    return $response.result
}

Export-ModuleMember -Function Invoke-McpRevit
