[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Security

$secureKey = Read-Host 'Cole a chave da API do Suite360' -AsSecureString
$pointer = [IntPtr]::Zero
$plainKey = $null
$keyBytes = $null

try {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    $plainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer).Trim()
    if ($plainKey.Length -lt 20) {
        throw 'A chave informada nao parece valida.'
    }

    $keyBytes = [Text.Encoding]::UTF8.GetBytes($plainKey)
    $protectedKey = [Security.Cryptography.ProtectedData]::Protect(
        $keyBytes,
        $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $directory = Join-Path $env:LOCALAPPDATA 'AutoCronos'
    $settingsPath = Join-Path $directory 'suite360-integration.json'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [IO.File]::WriteAllText($settingsPath, (@{ ProtectedApiKey = [Convert]::ToBase64String($protectedKey) } | ConvertTo-Json -Compress))
    Write-Host 'Integracao Suite360 provisionada para este usuario do Windows.' -ForegroundColor Green
}
finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    if ($null -ne $keyBytes) {
        [Array]::Clear($keyBytes, 0, $keyBytes.Length)
    }
}
