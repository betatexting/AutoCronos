[CmdletBinding()]
param(
    [switch]$OpenFolder
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'src\AutoCronos.Desktop\AutoCronos.Desktop.csproj'
$distPath = Join-Path $PSScriptRoot 'dist'
$executablePath = Join-Path $distPath 'AutoCronos.Desktop.exe'
$secretsDirectory = Join-Path $PSScriptRoot 'secret_Key'
$suiteApiKeyPath = Join-Path $secretsDirectory 'suite360-api-key.txt'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Projeto nao encontrado: $projectPath"
}

if (-not (Test-Path -LiteralPath $suiteApiKeyPath)) {
    Write-Host 'A chave do Suite360 ainda nao esta salva para publicacao.' -ForegroundColor Yellow
    $plainKey = $null
    $plainBytes = $null
    $protectedSettingsPath = Join-Path $env:LOCALAPPDATA 'AutoCronos\suite360-integration.json'
    try {
        if (Test-Path -LiteralPath $protectedSettingsPath) {
            try {
                Add-Type -AssemblyName System.Security
                $protectedSettings = Get-Content -Raw -LiteralPath $protectedSettingsPath | ConvertFrom-Json
                $encryptedBytes = [Convert]::FromBase64String($protectedSettings.ProtectedApiKey)
                $plainBytes = [Security.Cryptography.ProtectedData]::Unprotect(
                    $encryptedBytes,
                    $null,
                    [Security.Cryptography.DataProtectionScope]::CurrentUser)
                $plainKey = [Text.Encoding]::UTF8.GetString($plainBytes).Trim()
                Write-Host 'Chave recuperada da configuracao local protegida.' -ForegroundColor Green
            }
            catch {
                $plainKey = $null
                Write-Host 'A configuracao protegida pertence a outro usuario ou nao pode ser lida.' -ForegroundColor Yellow
            }
        }

        if ([string]::IsNullOrWhiteSpace($plainKey)) {
            Write-Host 'Copie a chave do Suite360 para a area de transferencia.' -ForegroundColor Cyan
            Read-Host 'Quando a chave estiver copiada, pressione Enter' | Out-Null
            $plainKey = ([string](Get-Clipboard -Raw)).Trim()
        }

        if ($plainKey.Length -lt 20) {
            throw 'A area de transferencia nao contem uma chave valida do Suite360.'
        }
        New-Item -ItemType Directory -Path $secretsDirectory -Force | Out-Null
        [IO.File]::WriteAllText($suiteApiKeyPath, $plainKey, [Text.UTF8Encoding]::new($false))
        Write-Host 'Chave salva para as proximas publicacoes.' -ForegroundColor Green
    }
    finally {
        if ($null -ne $plainBytes) {
            [Array]::Clear($plainBytes, 0, $plainBytes.Length)
        }
        $plainKey = $null
    }
}

New-Item -ItemType Directory -Path $distPath -Force | Out-Null

Write-Host 'Publicando AutoCronos para Windows x64...'
& dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $distPath `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Suite360ApiKeyFile="$suiteApiKeyPath"

if ($LASTEXITCODE -ne 0) {
    throw "A publicacao falhou (codigo $LASTEXITCODE)."
}

if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "Publicacao concluida, mas o executavel nao foi encontrado em: $executablePath"
}

$legacyConfigFiles = @(
    (Join-Path $distPath 'Configurar-Suite360.ps1'),
    (Join-Path $distPath 'Configurar-Suite360.bat')
)
foreach ($legacyConfigFile in $legacyConfigFiles) {
    if (Test-Path -LiteralPath $legacyConfigFile) {
        Remove-Item -LiteralPath $legacyConfigFile -Force
    }
}

Write-Host "Executavel atualizado: $executablePath" -ForegroundColor Green
Write-Host 'A chave do Suite360 foi incorporada ao executavel publicado.' -ForegroundColor Green

if ($OpenFolder) {
    Start-Process explorer.exe $distPath
}
