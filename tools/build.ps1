param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$SdkSource = $env:VRCFT_SDK_SOURCE
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdk = Join-Path $root '.sdk'
if ([string]::IsNullOrWhiteSpace($SdkSource)) { $SdkSource = Join-Path $sdk 'source/VRCFaceTracking' }
$source = [IO.Path]::GetFullPath($SdkSource)
$expectedCommit = '2cafd0e387386b54997797ee4709cad4befe9274'
$hashes = @{
    'Directory.Build.props' = 'E75F9B2A402A01E1CCBE188EE86FF55E5DE10B8A56C0796F577CD5584A7AC7A1'
    'Directory.Packages.props' = 'FA5D6A348628EF1A8354AC7CDEDEC5889F6E8E344A90BD1E3CD1AE49F2FCA707'
    'VRCFaceTracking.SDK/ExtTrackingModule.cs' = '9C8641A097DAA4D85BE578E4D8E5A83B922B5055A91C5468D69F2072D727021D'
    'VRCFaceTracking.Core/Params/Expressions/UnifiedExpressions.cs' = '1E87DB81075BA7C051AAE9811EB7D9B67C0B2F6F402E6388E7A81680BCBFE5B6'
}
if (-not (Test-Path (Join-Path $source 'VRCFaceTracking.Core/VRCFaceTracking.Core.csproj'))) {
    throw "Missing pinned SDK source: $source. Use -SdkSource, VRCFT_SDK_SOURCE, or .sdk/source/VRCFaceTracking. See docs/development.md. This script does not download or install a host."
}
$commit = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -ne $expectedCommit) { throw "Expected SDK commit $expectedCommit, found $commit" }
$dirty = & git -C $source status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Pinned VRCFT SDK source has tracked modifications; refusing to build a different API. No changes were discarded.' }
foreach ($entry in $hashes.GetEnumerator()) {
    # Git checkouts can differ in CRLF/LF and UTF-8 BOM, without changing the SDK API.
    $text = [IO.File]::ReadAllText((Join-Path $source $entry.Key)).Replace("`r`n", "`n")
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-', '') }
    finally { $algorithm.Dispose() }
    if ($fingerprint -ne $entry.Value) { throw "SDK fingerprint mismatch: $($entry.Key)" }
}
New-Item -ItemType Directory -Force -Path $sdk | Out-Null
$buildArtifacts = Join-Path $sdk 'build'
# All generated SDK bin/obj files remain under this independent project.
& dotnet build (Join-Path $source 'VRCFaceTracking.Core/VRCFaceTracking.Core.csproj') -c Release --artifacts-path $buildArtifacts --nologo
if ($LASTEXITCODE -ne 0) { throw 'SDK build failed.' }
foreach ($name in @('VRCFaceTracking.Core','VRCFaceTracking.SDK')) {
    $dll = Join-Path $buildArtifacts "bin/$name/release/$name.dll"
    if ([Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString() -ne '5.4.5.0') { throw "Wrong SDK assembly identity: $dll" }
    Copy-Item $dll (Join-Path $sdk "$name.dll") -Force
}
@{Commit=$commit; AssemblyVersion='5.4.5.0'; TargetFramework='net10.0'; FingerprintNormalization='UTF-8 without BOM; CRLF to LF'; SourceFingerprints=$hashes} |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $sdk 'sdk-provenance.json') -Encoding utf8
& dotnet build (Join-Path $root 'UnifiedPicoModule.csproj') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Module build failed.' }
& dotnet build (Join-Path $PSScriptRoot 'ConfigTool/ConfigTool.csproj') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Configuration tool build failed.' }
Write-Host "Built module and configuration tool ($Configuration). No installation performed."
