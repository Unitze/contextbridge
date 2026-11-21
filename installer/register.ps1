<#
.SYNOPSIS
  Registers or unregisters the ContextBridge sparse package.

.DESCRIPTION
  The package carries no binaries; it gives ContextBridge.Shell.dll (in the install folder)
  the app identity Windows 11 requires for new-context-menu entries. It is unsigned, so
  registration needs an elevated session (per-user registration fails with 0x80073D2B).
  Called by the installer and by the settings app (Register / Register again).

.PARAMETER Action
  Register | Unregister

.PARAMETER InstallDir
  Folder that contains ContextBridge.msix and ContextBridge.Shell.dll. Defaults to this script's folder.

.PARAMETER LogPath
  Optional file that receives error details (read back by the settings app).

.PARAMETER Language
  Language of the messages: "ja" or "en". Defaults to the Windows display language.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Register', 'Unregister')][string]$Action,
    [string]$InstallDir = $PSScriptRoot,
    [string]$LogPath,
    [string]$Language = [Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName
)

$ErrorActionPreference = 'Stop'
$PackageName = 'ContextBridge'

# Messages: Japanese for a Japanese UI, English otherwise (same rule as the app and the shell handler).
function T([string]$Ja, [string]$En) { if ($Language -eq 'ja') { $Ja } else { $En } }

function Write-Log([string]$Message) {
    Write-Host $Message
    if ($LogPath) { Add-Content -LiteralPath $LogPath -Value $Message -Encoding UTF8 }
}

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw (T '管理者として実行してください（署名なしパッケージの登録には管理者権限が必要です）。' `
                 'Run as administrator (registering an unsigned package requires administrator rights).')
    }
}

function Assert-Windows11 {
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 22000) {
        throw (T "Windows 11 (ビルド 22000 以降) が必要です。現在のビルド: $build" `
                 "Windows 11 (build 22000 or later) is required. Current build: $build")
    }
}

try {
    Assert-Admin

    if ($Action -eq 'Register') {
        Assert-Windows11
        $InstallDir = (Resolve-Path -LiteralPath $InstallDir).Path
        if ($InstallDir.Length -gt 3) { $InstallDir = $InstallDir.TrimEnd('\') }
        $msix = Join-Path $InstallDir 'ContextBridge.msix'
        $dll = Join-Path $InstallDir 'ContextBridge.Shell.dll'
        foreach ($f in @($msix, $dll)) {
            if (-not (Test-Path -LiteralPath $f)) { throw ((T 'ファイルが見つかりません: ' 'File not found: ') + $f) }
        }

        # Replace a registration that points at another folder (e.g. a moved install).
        $existing = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue)
        foreach ($p in $existing) {
            if ($p) {
                Write-Log ((T '既存の登録を解除します: ' 'Removing the existing registration: ') + $p.PackageFullName)
                Remove-AppxPackage -Package $p.PackageFullName
            }
        }

        Write-Log ((T "登録します: $msix (外部の場所: $InstallDir)" "Registering: $msix (external location: $InstallDir)"))
        Add-AppxPackage -Path $msix -ExternalLocation $InstallDir -AllowUnsigned
        $now = @(Get-AppxPackage -Name $PackageName)
        if ($now.Count -eq 0) { throw (T '登録後にパッケージが見つかりません。' 'The package is missing after registration.') }
        Write-Log ((T '登録しました: ' 'Registered: ') + (($now | ForEach-Object { $_.PackageFullName }) -join ', '))
    }
    else {
        $existing = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue)
        if ($existing.Count -eq 0) {
            Write-Log (T '登録されていません。' 'Not registered.')
        }
        foreach ($p in $existing) {
            if ($p) {
                Write-Log ((T '登録を解除します: ' 'Unregistering: ') + $p.PackageFullName)
                Remove-AppxPackage -Package $p.PackageFullName
            }
        }
    }
    exit 0
}
catch {
    $msg = $_.Exception.Message
    if ($msg -match '0x80073D2B') {
        $msg += "`n" + (T '署名なしパッケージの登録には管理者権限が必要です。' 'Registering an unsigned package requires administrator rights.')
    }
    elseif ($msg -match '0x80073CFF|0x80073D01') {
        $msg += "`n" + (T 'パッケージ展開がポリシーで制限されている可能性があります（組織の管理下にある PC など）。' 'Package deployment may be restricted by policy (for example on an organization-managed PC).')
    }
    Write-Log "ERROR: $msg"
    exit 1
}
