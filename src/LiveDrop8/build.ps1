param([string]$MSBuild = 'C:\Program Files (x86)\MSBuild\14.0\Bin\MSBuild.exe')
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
[xml]$manifest = Get-Content (Join-Path $PSScriptRoot 'LiveDrop8.WindowsPhone\Package.appxmanifest') -Raw
$version = $manifest.Package.Identity.Version
$destination = Join-Path $repoRoot ('builds\LiveDrop8\' + $version)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($target in @(@{Name='WindowsPhone';Architecture='ARM'}, @{Name='WindowsPhone';Architecture='x86'}, @{Name='Windows';Architecture='x86'})) {
    $project = Join-Path $PSScriptRoot ('LiveDrop8.' + $target.Name + '\LiveDrop8.' + $target.Name + '.csproj')
    & $MSBuild $project /t:Build /p:Configuration=Release (('/p:Platform=' + $target.Architecture)) /p:AppxBundle=Never /p:AppxPackageSigningEnabled=true /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($target.Name)" }
    $filename = 'LiveDrop8.' + $target.Name + '_' + $version + '_' + $target.Architecture + '.appx'
    $package = Join-Path (Split-Path $project) ('AppPackages\LiveDrop8.' + $target.Name + '_' + $version + '_' + $target.Architecture + '_Test\' + $filename)
    Copy-Item -LiteralPath $package -Destination (Join-Path $destination $filename) -Force
    if ($target.Name -eq 'WindowsPhone') {
        # WP8.1's Application Deployment tool expects a developer AppX,
        # not the test-signed package used by the desktop installer.
        $sdkDestination = Join-Path $destination 'SDKDeployment'
        New-Item -ItemType Directory -Path $sdkDestination -Force | Out-Null
        & $MSBuild $project /t:Build /p:Configuration=Release (('/p:Platform=' + $target.Architecture)) /p:AppxBundle=Never /p:AppxPackageSigningEnabled=false (('/p:AppxPackageDir=' + $sdkDestination + '\')) /v:minimal
        if ($LASTEXITCODE -ne 0) { throw "SDK deployment build failed: $($target.Architecture)" }
        $sdkPackage = Join-Path $sdkDestination ('LiveDrop8.WindowsPhone_' + $version + '_' + $target.Architecture + '_Test\' + $filename)
        Copy-Item -LiteralPath $sdkPackage -Destination (Join-Path $sdkDestination $filename) -Force
    }
}
$certificate = Get-Item 'Cert:\CurrentUser\My\B90DCB33BC1AB90DBF79B8BBF34A2592D25315C0'
[IO.File]::WriteAllBytes((Join-Path $destination 'RetiredLake.cer'), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
Get-ChildItem -LiteralPath $destination -Filter '*.appx' | Get-FileHash -Algorithm SHA256 | Format-Table -AutoSize
