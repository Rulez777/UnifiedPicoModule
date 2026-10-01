param([switch]$SkipBuild, [string]$SdkSource = $env:VRCFT_SDK_SOURCE)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release -SdkSource $SdkSource }
$metadata = Get-Content (Join-Path $root 'module.json') -Raw | ConvertFrom-Json
# VRCFT 5.4.5 binds this string directly to HyperlinkButton.NavigateUri.
# Both an empty string and null crashed the registry page during headset acceptance.
$modulePageUri = $null
if ([string]::IsNullOrWhiteSpace($metadata.ModulePageUrl) -or
    -not [Uri]::TryCreate($metadata.ModulePageUrl, [UriKind]::Absolute, [ref]$modulePageUri) -or
    $modulePageUri.Scheme -notin @('http', 'https', 'file')) {
    throw 'ModulePageUrl must be a non-empty absolute http/https/file URI; VRCFT 5.4.5 crashes on empty/null values. Use https://example.com/ only as a local preview placeholder.'
}
$dll = Join-Path $root 'bin/Release/net10.0/UnifiedPicoModule.dll'
if (-not (Test-Path $dll)) { throw 'Release module DLL missing. Build first.' }
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
# Never overwrite earlier packages or use a broad recursive deletion.
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$stage = Join-Path $artifacts "stage-$stamp"
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item $dll $stage
foreach ($file in @('module.json','README.md','README_zh.md','CONTRIBUTING.md','CONTRIBUTING_zh.md','THIRD_PARTY_NOTICES.md','THIRD_PARTY_NOTICES_zh.md','LICENSE')) {
    Copy-Item (Join-Path $root $file) $stage
}
# Do not recursively copy docs: agent-only records can contain personal paths/history.
$brandingStage = Join-Path $stage 'assets/branding'
New-Item -ItemType Directory -Path $brandingStage -Force | Out-Null
# Explicit release assets only; never package generated design drafts.
foreach ($asset in @('logo-dark.svg','logo-light.svg','module-icon.svg','logo-dark.png','logo-light.png','module-icon.png')) {
    Copy-Item (Join-Path $root "assets/branding/$asset") $brandingStage
}
$publicDocs = @('configuration.md','configuration_zh.md','postprocessing.md','postprocessing_zh.md','protocol.md','protocol_zh.md','development.md','development_zh.md','privacy.md','privacy_zh.md')
$docsStage = Join-Path $stage 'docs'
New-Item -ItemType Directory -Path $docsStage | Out-Null
foreach ($document in $publicDocs) { Copy-Item (Join-Path $root "docs/$document") $docsStage }
$toolDocs = Join-Path $stage 'tools/ConfigTool'
New-Item -ItemType Directory -Path $toolDocs | Out-Null
foreach ($toolDoc in @('README.md','README_zh.md')) { Copy-Item (Join-Path $PSScriptRoot "ConfigTool/$toolDoc") $toolDocs }
# A developer's runtime JSON can contain IPs and migration paths. Always export clean
# model defaults, never package that file or personal migration reports.
$configTool = Join-Path $PSScriptRoot 'ConfigTool/bin/Release/net10.0/UnifiedPicoConfigTool.dll'
if (-not (Test-Path $configTool)) { throw 'Release configuration tool missing. Build first.' }
& dotnet $configTool defaults (Join-Path $stage 'UnifiedPicoModule.json')
if ($LASTEXITCODE -ne 0) { throw 'Clean default configuration export failed.' }
$configStage = Join-Path $stage 'config'
New-Item -ItemType Directory -Path $configStage | Out-Null
Copy-Item (Join-Path $stage 'UnifiedPicoModule.json') (Join-Path $configStage 'UnifiedPicoModule.example.json')
Copy-Item (Join-Path $root '.sdk/sdk-provenance.json') $stage
$zip = Join-Path $artifacts "UnifiedPicoModule-$($metadata.Version)-$stamp.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content "$zip.sha256" -Encoding utf8
Write-Host "Package: $zip"
Write-Host "SHA256: $hash"
Write-Host 'Package contains no VRCFaceTracking SDK/Core DLLs. No module installed or published.'
