param(
    [string]$Subject = "CN=WinMux Local Dev",
    [string]$Configuration = "Release",
    [string]$TargetFramework = "net8.0-windows10.0.19041.0",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$PublishDirectory = "",
    [string]$InstallerPath = "",
    [string]$CertificateOutputPath = "",
    [switch]$Publish,
    [switch]$TrustCurrentUser = $false
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Resolve-PublishDirectory {
    param(
        [string]$RepoRoot,
        [string]$PreferredPath,
        [string]$BuildConfiguration,
        [string]$Framework,
        [string]$Rid
    )

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        return (Resolve-Path $PreferredPath).Path
    }

    $candidate = Join-Path $RepoRoot "bin\$BuildConfiguration\$Framework\$Rid\publish"
    if (Test-Path $candidate) {
        return (Resolve-Path $candidate).Path
    }

    throw "Publish directory '$candidate' was not found. Run with -Publish or pass -PublishDirectory."
}

function Resolve-SignToolPath {
    $candidates = [System.Collections.Generic.List[string]]::new()

    $windowsKitsRoots = @(
        "C:\Program Files (x86)\Windows Kits\10\bin",
        "C:\Program Files\Windows Kits\10\bin"
    )

    foreach ($root in $windowsKitsRoots) {
        if (-not (Test-Path $root)) {
            continue
        }

        foreach ($versionDirectory in Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending) {
            $candidate = Join-Path $versionDirectory.FullName "x64\signtool.exe"
            if (Test-Path $candidate) {
                $candidates.Add($candidate)
            }
        }
    }

    $nugetRoot = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools"
    if (Test-Path $nugetRoot) {
        foreach ($packageDirectory in Get-ChildItem -Path $nugetRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending) {
            foreach ($sdkBinDirectory in Get-ChildItem -Path (Join-Path $packageDirectory.FullName "bin") -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending) {
                $candidate = Join-Path $sdkBinDirectory.FullName "x64\signtool.exe"
                if (Test-Path $candidate) {
                    $candidates.Add($candidate)
                }
            }
        }
    }

    $resolved = $candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) } | Select-Object -First 1
    if ($null -eq $resolved) {
        throw "Could not find signtool.exe. Install the Windows SDK or Microsoft.Windows.SDK.BuildTools."
    }

    return (Resolve-Path $resolved).Path
}

function Get-OrCreateCodeSigningCertificate {
    param(
        [string]$CertificateSubject
    )

    $codeSigningOid = "1.3.6.1.5.5.7.3.3"
    $existing = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object {
            $_.HasPrivateKey -and
            $_.Subject -eq $CertificateSubject -and
            ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq $codeSigningOid })
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($null -ne $existing) {
        return $existing
    }

    return New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $CertificateSubject `
        -FriendlyName "WinMux Local Dev Code Signing" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(3)
}

function Ensure-CertificateTrusted {
    $stores = @(
        "Root",
        "TrustedPublisher"
    )

    foreach ($store in $stores) {
        $null = & certutil.exe -user -f -addstore $store $script:CertificateOutputPath
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to import '$script:CertificateOutputPath' into CurrentUser\\$store."
        }
    }
}

function Sign-File {
    param(
        [string]$SignToolPath,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$TargetPath
    )

    if (-not (Test-Path $TargetPath)) {
        throw "Cannot sign '$TargetPath' because the file does not exist."
    }

    & $SignToolPath sign `
        /sha1 $Certificate.Thumbprint `
        /fd SHA256 `
        /td SHA256 `
        /tr "http://timestamp.digicert.com" `
        /d "WinMux" `
        /v `
        $TargetPath
}

$repoRoot = Resolve-RepoRoot
Write-Host "Repo root: $repoRoot"

if ($Publish) {
    $dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
    if (-not (Test-Path $dotnetPath)) {
        throw "dotnet.exe was not found at '$dotnetPath'."
    }

    & $dotnetPath publish `
        (Join-Path $repoRoot "SelfContainedDeployment.csproj") `
        -c $Configuration `
        -p:Platform=x64 `
        -p:PublishProfile="Properties\PublishProfiles\win10-x64.pubxml"
}

$publishDirectory = Resolve-PublishDirectory `
    -RepoRoot $repoRoot `
    -PreferredPath $PublishDirectory `
    -BuildConfiguration $Configuration `
    -Framework $TargetFramework `
    -Rid $RuntimeIdentifier
Write-Host "Publish directory: $publishDirectory"

$winMuxExePath = Join-Path $publishDirectory "WinMux.exe"
if (-not (Test-Path $winMuxExePath)) {
    throw "Publish directory '$publishDirectory' does not contain WinMux.exe."
}

$artifactsDirectory = Join-Path $repoRoot "artifacts\signing"
New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($CertificateOutputPath)) {
    $CertificateOutputPath = Join-Path $artifactsDirectory "WinMux-Local-Dev-CodeSigning.cer"
}
else {
    $CertificateOutputPath = [System.IO.Path]::GetFullPath($CertificateOutputPath)
}

$script:CertificateOutputPath = $CertificateOutputPath
$signToolPath = Resolve-SignToolPath
Write-Host "SignTool: $signToolPath"
$certificate = Get-OrCreateCodeSigningCertificate -CertificateSubject $Subject
Write-Host "Certificate: $($certificate.Subject) [$($certificate.Thumbprint)]"

Export-Certificate -Cert $certificate -FilePath $CertificateOutputPath -Force | Out-Null
Write-Host "Exported certificate: $CertificateOutputPath"

if ($TrustCurrentUser) {
    Write-Host "Trusting certificate in CurrentUser stores"
    Ensure-CertificateTrusted
}

$signedFiles = [System.Collections.Generic.List[string]]::new()
Write-Host "Signing: $winMuxExePath"
Sign-File -SignToolPath $signToolPath -Certificate $certificate -TargetPath $winMuxExePath
$signedFiles.Add($winMuxExePath)

if (-not [string]::IsNullOrWhiteSpace($InstallerPath)) {
    $resolvedInstallerPath = (Resolve-Path $InstallerPath).Path
    Write-Host "Signing installer: $resolvedInstallerPath"
    Sign-File -SignToolPath $signToolPath -Certificate $certificate -TargetPath $resolvedInstallerPath
    $signedFiles.Add($resolvedInstallerPath)
}

Write-Host "Verifying signatures"
$signatures = foreach ($path in $signedFiles) {
    $signature = Get-AuthenticodeSignature -FilePath $path
    [pscustomobject]@{
        path = $path
        status = $signature.Status.ToString()
        statusMessage = $signature.StatusMessage
        signer = $signature.SignerCertificate.Subject
        thumbprint = $signature.SignerCertificate.Thumbprint
    }
}

[pscustomobject]@{
    ok = $true
    subject = $certificate.Subject
    thumbprint = $certificate.Thumbprint
    notAfter = $certificate.NotAfter
    signToolPath = $signToolPath
    certificateOutputPath = $CertificateOutputPath
    trustCurrentUser = [bool]$TrustCurrentUser
    publishDirectory = $publishDirectory
    signedFiles = $signatures
} | ConvertTo-Json -Depth 5
