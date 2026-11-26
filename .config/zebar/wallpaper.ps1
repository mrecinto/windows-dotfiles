param([string]$Wallpaper)

$ErrorActionPreference = 'Stop'
$cache = Join-Path $env:LOCALAPPDATA 'WallpaperZebar'
$active = Join-Path $cache 'active.json'
[void][IO.Directory]::CreateDirectory($cache)
$lock = [Threading.Mutex]::new($false, 'Local\WallpaperZebarPalette')
$locked = $false

function Write-CacheFile($Path, $Text) {
    $temp = "$Path.$PID.tmp"
    [IO.File]::WriteAllText($temp, $Text)
    if ([IO.File]::Exists($Path)) {
        [IO.File]::Replace($temp, $Path, [NullString]::Value)
    } else {
        [IO.File]::Move($temp, $Path)
    }
}

try {
    try { $locked = $lock.WaitOne(2000) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) {
        if (Test-Path -LiteralPath $active) {
            (Get-Content -LiteralPath $active -Raw | ConvertFrom-Json).palette | ConvertTo-Json -Compress
        }
        return
    }
    if (-not $Wallpaper) {
        $Wallpaper = (Get-ItemProperty 'HKCU:\Control Panel\Desktop').WallPaper
        if (-not $Wallpaper -or -not (Test-Path -LiteralPath $Wallpaper -PathType Leaf)) {
            $Wallpaper = Join-Path $env:APPDATA 'Microsoft\Windows\Themes\TranscodedWallpaper'
        }
    }

    $image = Get-Item -LiteralPath $Wallpaper
    $version = 'matugen-4.2.0-dark-512-v2'
    $signature = "$version|$($image.FullName)|$($image.LastWriteTimeUtc.Ticks)|$($image.Length)"
    if (Test-Path -LiteralPath $active) {
        $previous = Get-Content -LiteralPath $active -Raw | ConvertFrom-Json
        if ($previous.signature -ceq $signature) {
            $previous.palette | ConvertTo-Json -Compress
            return
        }
    }

    $hash = (Get-FileHash -LiteralPath $image.FullName -Algorithm SHA256).Hash
    $palettePath = Join-Path $cache "$version-$hash.json"
    if (-not (Test-Path -LiteralPath $palettePath)) {
        $normalized = Join-Path $cache 'wallpaper.png'
        $config = Join-Path $PSScriptRoot 'matugen.toml'
        $wslSource = & wsl.exe -d Ubuntu --exec wslpath -u $image.FullName.Replace('\', '/')
        if ($LASTEXITCODE -ne 0) { throw 'WSL could not resolve the wallpaper path.' }
        $wslImage = & wsl.exe -d Ubuntu --exec wslpath -u $normalized.Replace('\', '/')
        if ($LASTEXITCODE -ne 0) { throw 'WSL could not resolve the wallpaper cache path.' }
        $wslConfig = & wsl.exe -d Ubuntu --exec wslpath -u $config.Replace('\', '/')
        if ($LASTEXITCODE -ne 0) { throw 'WSL could not resolve the Matugen config path.' }
        & wsl.exe -d Ubuntu --exec /usr/bin/magick $wslSource -auto-orient -thumbnail '512x512>' -strip $wslImage
        if ($LASTEXITCODE -ne 0) { throw 'ImageMagick could not normalize the wallpaper.' }
        $output = & wsl.exe -d Ubuntu --exec /home/matthew/.local/bin/matugen -c $wslConfig image $wslImage --mode dark --source-color-index 0 --json hex --old-json-output
        if ($LASTEXITCODE -ne 0) { throw 'Matugen could not generate wallpaper colors.' }
        $colors = ($output | Out-String | ConvertFrom-Json).colors
        $palette = [ordered]@{}
        foreach ($name in 'background', 'on_background', 'primary', 'on_primary', 'surface_variant', 'surface_tint', 'error') {
            $color = $colors.$name.dark
            if ($color -notmatch '^#[0-9a-fA-F]{6}$') { throw "Missing Matugen color: $name" }
            $palette[$name] = $color
        }
        Write-CacheFile $palettePath ($palette | ConvertTo-Json -Compress)
    }

    $paletteJson = Get-Content -LiteralPath $palettePath -Raw
    Write-CacheFile $active (@{signature=$signature; palette=($paletteJson | ConvertFrom-Json)} | ConvertTo-Json -Compress)
    $paletteJson
} finally {
    if ($locked) { $lock.ReleaseMutex() }
    $lock.Dispose()
}
