param(
    [Parameter(Mandatory)][ValidateSet('default', 'xournal')][string]$Mode,
    [Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Path
)
$ErrorActionPreference = 'Stop'

if ($Mode -eq 'default') {
    foreach ($item in $Path) {
        $file = Get-Item -LiteralPath $item -Force
        Start-Process -FilePath $file.FullName
    }
    return
}

$file = Get-Item -LiteralPath $Path[0] -Force
$app = Get-Command xournalpp.exe -ErrorAction SilentlyContinue
$executable = if ($app) { $app.Source } else {
    Join-Path $env:ProgramFiles 'Xournal++\bin\xournalpp.exe'
}
if (-not (Test-Path -LiteralPath $executable)) {
    throw 'Xournal++ could not be found.'
}

if ($file.Extension -eq '.xopp' -and $file.Length -eq 0) {
    $template = Join-Path (Split-Path $PSScriptRoot) 'templates\default.xopp'
    $stream = [IO.File]::Open($file.FullName, 'Open', 'ReadWrite', 'None')
    try {
        if ($stream.Length -eq 0) {
            $content = [IO.File]::ReadAllBytes($template)
            $stream.Write($content, 0, $content.Length)
        }
    } finally {
        $stream.Dispose()
    }
}

& $executable $file.FullName
