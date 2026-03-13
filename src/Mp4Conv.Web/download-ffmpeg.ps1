param(
    [Parameter(Mandatory)]
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$ffmpegExe = Join-Path $OutputDir 'ffmpeg.exe'
if (Test-Path $ffmpegExe) {
    Write-Host "FFmpeg already exists at $ffmpegExe - skipping download."
    exit 0
}

$zipUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip'
$tempZip = Join-Path ([System.IO.Path]::GetTempPath()) 'ffmpeg-download.zip'
$tempExtract = Join-Path ([System.IO.Path]::GetTempPath()) 'ffmpeg-extract'

try {
    Write-Host "Downloading FFmpeg from $zipUrl ..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $zipUrl -OutFile $tempZip -UseBasicParsing

    Write-Host "Extracting FFmpeg..."
    if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force }
    Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force

    # The zip contains a single top-level folder with bin/ inside
    $binDir = Get-ChildItem -Path $tempExtract -Directory | Select-Object -First 1
    $binPath = Join-Path $binDir.FullName 'bin'

    if (-not (Test-Path $binPath)) {
        throw "Expected bin directory not found inside the FFmpeg archive."
    }

    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }

    Copy-Item (Join-Path $binPath 'ffmpeg.exe')  -Destination $OutputDir -Force
    Copy-Item (Join-Path $binPath 'ffprobe.exe') -Destination $OutputDir -Force

    Write-Host "FFmpeg installed to $OutputDir"
}
finally {
    if (Test-Path $tempZip)     { Remove-Item $tempZip -Force -ErrorAction SilentlyContinue }
    if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue }
}
