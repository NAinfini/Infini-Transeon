[CmdletBinding()]
param(
    [string] $Repository = 'NAinfini/Infini-Transeon',
    [string] $Environment = 'release',
    [string] $CredentialTarget = 'InfiniTranseon/ReleaseSigning/Ed25519',
    [switch] $ValidateOnly,
    [switch] $UploadSecrets,
    [string] $TrustRootSource = (Join-Path $PSScriptRoot `
        '..\src\InfiniTranseon.App\Presentation\Services\ProductionReleaseTrustRoot.cs')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Repository must use owner/name form.'
}
if ([string]::IsNullOrWhiteSpace($Environment)) {
    throw 'Environment must not be empty.'
}
if ($ValidateOnly -and $UploadSecrets) {
    throw 'ValidateOnly and UploadSecrets cannot be used together.'
}
$trustRootFullPath = (Resolve-Path -LiteralPath $TrustRootSource).Path

if (-not ('InfiniTranseon.ReleaseCredentialReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace InfiniTranseon
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StoredReleaseCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }

    public static class ReleaseCredentialReader
    {
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
        public static extern void CredFree(IntPtr credential);

        public static StoredReleaseCredential Read(IntPtr credential)
        {
            return Marshal.PtrToStructure<StoredReleaseCredential>(credential);
        }
    }
}
'@
}

$credentialPointer = [IntPtr]::Zero
$privateBytes = $null
$temporaryRoot = Join-Path $env:TEMP (
    'infini-release-config-' + [Guid]::NewGuid().ToString('N'))
try {
    if (-not [InfiniTranseon.ReleaseCredentialReader]::CredRead(
            $CredentialTarget,
            1,
            0,
            [ref]$credentialPointer)) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "Release signing credential was not found (Win32 $errorCode). Run new-release-signing-key.ps1 first."
    }
    $credential = [InfiniTranseon.ReleaseCredentialReader]::Read($credentialPointer)
    $privateBytes = [byte[]]::new($credential.CredentialBlobSize)
    [Runtime.InteropServices.Marshal]::Copy(
        $credential.CredentialBlob,
        $privateBytes,
        0,
        $privateBytes.Length)
    $source = Get-Content -LiteralPath $trustRootFullPath -Raw
    $sourceKeyId = [regex]::Match(
        $source,
        'CurrentKeyId\s*=\s*"(?<value>[a-z0-9._-]+)"').Groups['value'].Value
    $sourcePublicKey = [regex]::Match(
        $source,
        'Convert\.FromHexString\(\s*"(?<value>[0-9a-f]{64})"\s*\)').Groups['value'].Value
    if ([string]::IsNullOrWhiteSpace($sourceKeyId) -or
        [string]::IsNullOrWhiteSpace($sourcePublicKey) -or
        $credential.UserName -ne $sourceKeyId) {
        throw 'The stored release key identity does not match the embedded trust root.'
    }
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $privateKeyPath = Join-Path $temporaryRoot 'release-ed25519.pem'
    $publicKeyPath = Join-Path $temporaryRoot 'release-ed25519-public.der'
    [IO.File]::WriteAllBytes($privateKeyPath, $privateBytes)
    $openSsl = @(
        (Join-Path $env:ProgramFiles 'Git\usr\bin\openssl.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\usr\bin\openssl.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($openSsl)) {
        throw 'OpenSSL from Git for Windows was not found.'
    }
    & $openSsl pkey -in $privateKeyPath -pubout -outform DER -out $publicKeyPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not derive the stored release public key.' }
    $publicDer = [IO.File]::ReadAllBytes($publicKeyPath)
    if ($publicDer.Length -ne 44) {
        throw 'The stored release public key has an unexpected encoding.'
    }
    [byte[]] $rawPublicKey = $publicDer[12..43]
    if ([Convert]::ToHexString($rawPublicKey).ToLowerInvariant() -ne $sourcePublicKey) {
        throw 'The stored release private key does not match the application trust root.'
    }

    if ($ValidateOnly -or -not $UploadSecrets) {
        return [pscustomobject]@{
            Repository = $Repository
            Environment = $Environment
            ReleaseKeyId = $credential.UserName
            WindowsCodeSigning = 'unsigned'
            GitHubSecretChanged = $false
            Validated = $true
        }
    }

    $privateKeyBase64 = [Convert]::ToBase64String($privateBytes)

    & gh auth status
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub CLI is not authenticated. Run gh auth login, then retry.'
    }
    & gh api --method PUT "repos/$Repository/environments/$Environment" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create or update GitHub environment '$Environment'." }

    $privateKeyBase64 | & gh secret set RELEASE_ED25519_PRIVATE_KEY `
        --repo $Repository --env $Environment
    if ($LASTEXITCODE -ne 0) { throw 'Could not set RELEASE_ED25519_PRIVATE_KEY.' }
    $credential.UserName | & gh secret set RELEASE_ED25519_KEY_ID `
        --repo $Repository --env $Environment
    if ($LASTEXITCODE -ne 0) { throw 'Could not set RELEASE_ED25519_KEY_ID.' }

    [pscustomobject]@{
        Repository = $Repository
        Environment = $Environment
        ReleaseKeyId = $credential.UserName
        WindowsCodeSigning = 'unsigned'
        GitHubSecretChanged = $true
        Validated = $true
    }
}
finally {
    if ($credentialPointer -ne [IntPtr]::Zero) {
        [InfiniTranseon.ReleaseCredentialReader]::CredFree($credentialPointer)
    }
    if ($null -ne $privateBytes) {
        [Array]::Clear($privateBytes, 0, $privateBytes.Length)
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
