param(
    [switch]$WithChr,
    [switch]$SkipLab
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Require($command, $hint) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "$command was not found. $hint"
    }
}

Require dotnet 'Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10'
Require docker 'Install Docker Desktop: winget install Docker.DockerDesktop'
Require node 'Install Node.js 22 LTS: winget install OpenJS.NodeJS.LTS'

$envFile = Join-Path $root 'deploy\.env'
if (-not (Test-Path $envFile)) {
    $lines = Get-Content (Join-Path $root 'deploy\.env.example') | ForEach-Object {
        if ($_ -match '^([A-Z0-9_]+)=$') {
            $bytes = New-Object byte[] 16
            [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
            "$($Matches[1])=$(([System.BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant())"
        } elseif ($_ -notmatch '^#') { $_ }
    }
    [System.IO.File]::WriteAllLines($envFile, $lines)
    Write-Host 'Created deploy\.env with new random secrets.'
} else {
    Write-Host 'Keeping the existing deploy\.env.'
}

$settings = @{}
Get-Content $envFile | Where-Object { $_ -match '^([A-Z0-9_]+)=(.*)$' } | ForEach-Object { $settings[$Matches[1]] = $Matches[2] }

$secrets = [ordered]@{
    'ConnectionStrings:Kibo' = "Host=127.0.0.1;Port=55432;Database=kibo;Username=kibo;Password=$($settings.KIBO_DB_PASSWORD)"
    'Seed:AdminPassword' = $settings.KIBO_DEV_ADMIN_PASSWORD
    'Seed:OwnerPassword' = $settings.KIBO_DEV_OWNER_PASSWORD
    'Payments:SimulatedMobileMoney:ApiKey' = $settings.KIBO_SIM_MOMO_API_KEY
    'Payments:SimulatedMobileMoney:CallbackSigningSecret' = $settings.KIBO_SIM_MOMO_CALLBACK_SECRET
    'Lab:SimApiPassword' = $settings.KIBO_SIM_API_PASSWORD
    'Lab:SimRadiusSecret1' = $settings.KIBO_SIM_RADIUS_SECRET_1
    'Lab:SimRadiusSecret2' = $settings.KIBO_SIM_RADIUS_SECRET_2
}
foreach ($key in $secrets.Keys) {
    dotnet user-secrets set $key $secrets[$key] --project src/Kibo.Web | Out-Null
}
Write-Host 'Stored development settings in dotnet user-secrets.'

dotnet tool restore
dotnet restore Kibo.slnx
Push-Location src/Kibo.Web
npm ci
Pop-Location
dotnet publish src/Kibo.Lab -c Release -o lab/sim-publish
dotnet build Kibo.slnx

if ($WithChr) {
    $images = Join-Path $root 'lab\chr\images'
    New-Item -ItemType Directory -Force $images | Out-Null
    $expected = @{}
    Get-Content (Join-Path $root 'lab\chr\CHECKSUMS.sha256') | ForEach-Object {
        $parts = $_ -split '\s+\*?'
        $expected[$parts[1]] = $parts[0]
    }
    foreach ($version in '7.25', '7.24.6') {
        $file = Join-Path $images "chr-$version.img.zip"
        if (-not (Test-Path $file)) {
            Invoke-WebRequest "https://download.mikrotik.com/routeros/$version/chr-$version.img.zip" -OutFile $file
        }
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $expected["chr-$version.img.zip"]) {
            throw "Checksum mismatch for chr-$version.img.zip"
        }
    }
    Write-Host 'RouterOS CHR images downloaded and verified.'
}

if (-not $SkipLab) {
    $compose = @('compose', '-f', 'deploy/docker-compose.yml')
    if ($WithChr) { $compose += @('--profile', 'chr') }
    docker @compose up -d --build postgres freeradius router-sim-1 router-sim-2
    Write-Host 'Lab started: PostgreSQL 127.0.0.1:55432, FreeRADIUS, two RouterOS simulators.'
}

Write-Host 'Done. Start the app with: dotnet run --project src/Kibo.Web --launch-profile http'
