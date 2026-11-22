; NSIS installer for ContextBridge. makensis runs natively on Linux, so the installer is
; built on Linux together with everything else.
;
;   makensis -DARCH=x64 -DVERSION=1.0.1 -DSTAGE=dist/stage-x64 -DOUTDIR=dist installer/ContextBridge.nsi
;
; STAGE must contain: ContextBridge.exe, ContextBridge.Shell.dll, ContextBridge.msix, register.ps1, LICENSE.txt

Unicode true
ManifestDPIAware true
ManifestSupportedOS Win10   ; Windows 10/11 share the same GUID

!ifndef ARCH
  !define ARCH "x64"
!endif
!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!ifndef WINVERSION
  !define WINVERSION "${VERSION}.0"
!endif
!ifndef STAGE
  !define STAGE "..\dist\stage-${ARCH}"
!endif
!ifndef OUTDIR
  !define OUTDIR "..\dist"
!endif

!define APPNAME "ContextBridge"
!define PUBLISHER "ContextBridge contributors"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ContextBridge"
!define MINBUILD 22000   ; Windows 11 21H2: first build with the new context menu

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

Name "${APPNAME}"
OutFile "${OUTDIR}\ContextBridge-${VERSION}-${ARCH}-setup.exe"
; Unsigned sparse packages can only be registered from an elevated session.
RequestExecutionLevel admin
InstallDir "$PROGRAMFILES64\ContextBridge"
InstallDirRegKey HKLM "${UNINSTKEY}" "InstallLocation"
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION}"

VIProductVersion "${WINVERSION}"
VIAddVersionKey /LANG=0 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=0 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=0 "FileDescription" "${APPNAME} Setup"
VIAddVersionKey /LANG=0 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "LegalCopyright" "Copyright (c) 2026 Unitze"

!define MUI_ICON "..\src\Shell\app.ico"
!define MUI_UNICON "..\src\Shell\app.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApp

!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Japanese"
!insertmacro MUI_LANGUAGE "English"

LangString LangTag ${LANG_JAPANESE} "ja"
LangString LangTag ${LANG_ENGLISH} "en"
LangString MsgOldWindows ${LANG_JAPANESE} "ContextBridge には Windows 11 (ビルド ${MINBUILD} 以降) が必要です。"
LangString MsgOldWindows ${LANG_ENGLISH} "ContextBridge requires Windows 11 (build ${MINBUILD} or later)."
LangString MsgWrongArch ${LANG_JAPANESE} "このインストーラーは ${ARCH} 用です。この PC ($0) 用のインストーラーを使ってください。"
LangString MsgWrongArch ${LANG_ENGLISH} "This installer is for ${ARCH}. Please use the installer for this PC ($0)."
LangString MsgRegistering ${LANG_JAPANESE} "新しいコンテキストメニューに登録しています..."
LangString MsgRegistering ${LANG_ENGLISH} "Registering with the new context menu..."
LangString MsgRegisterFailed ${LANG_JAPANESE} "新しいコンテキストメニューへの登録に失敗しました。$\r$\nインストール後に ContextBridge の「設定」タブから「登録する」を実行してください。"
LangString MsgRegisterFailed ${LANG_ENGLISH} "Registering with the new context menu failed.$\r$\nOpen ContextBridge and use Register on the Settings tab."
LangString MsgDeleteSettings ${LANG_JAPANESE} "コマンドの設定 ($0) も削除しますか？$\r$\n「いいえ」を選ぶと設定は残り、再インストール時にそのまま使えます。"
LangString MsgDeleteSettings ${LANG_ENGLISH} "Also delete your command settings ($0)?$\r$\nChoose No to keep them for a later reinstall."

Function .onInit
  SetRegView 64
  ; Windows build check
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  ${If} $0 < ${MINBUILD}
    MessageBox MB_ICONSTOP $(MsgOldWindows) /SD IDOK
    Abort
  ${EndIf}
  ; Architecture check: Explorer only loads a handler DLL built for its own architecture.
  ReadRegStr $0 HKLM "SYSTEM\CurrentControlSet\Control\Session Manager\Environment" "PROCESSOR_ARCHITECTURE"
  !if ${ARCH} == "arm64"
    StrCmp $0 "ARM64" archok
  !else
    StrCmp $0 "AMD64" archok
  !endif
  MessageBox MB_ICONSTOP $(MsgWrongArch) /SD IDOK
  Abort
archok:
FunctionEnd

Function LaunchApp
  ; The installer is elevated; start the settings app as the normal user via Explorer.
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\ContextBridge.exe"'
FunctionEnd

; Explorer keeps the handler DLL loaded. A loaded DLL can be renamed but not overwritten, so
; move the old one aside and delete it now or at the next reboot.
!macro RetireLockedFile path
  ${If} ${FileExists} "${path}"
    Delete "${path}"
    ${If} ${FileExists} "${path}"
      GetTempFileName $1 "$INSTDIR"
      Delete "$1"
      Rename "${path}" "$1"
      Delete /REBOOTOK "$1"
    ${EndIf}
  ${EndIf}
!macroend

Section "ContextBridge" SecMain
  SectionIn RO
  SetShellVarContext all
  SetOutPath "$INSTDIR"

  !insertmacro RetireLockedFile "$INSTDIR\ContextBridge.Shell.dll"
  !insertmacro RetireLockedFile "$INSTDIR\ContextBridge.exe"
  File "${STAGE}\ContextBridge.Shell.dll"
  File "${STAGE}\ContextBridge.exe"
  File "${STAGE}\ContextBridge.msix"
  File "${STAGE}\register.ps1"
  File "${STAGE}\LICENSE.txt"
  File /oname=README.md "..\README.md"

  WriteUninstaller "$INSTDIR\uninstall.exe"
  CreateShortcut "$SMPROGRAMS\ContextBridge.lnk" "$INSTDIR\ContextBridge.exe"

  ; Apps & features entry
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINSTKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\ContextBridge.exe"
  WriteRegStr HKLM "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTKEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKLM "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINSTKEY}" "EstimatedSize" "$0"

  ; Register the sparse package (gives the handler its app identity).
  ; NSIS is 32-bit, so disable WOW64 FS redirection to run native 64-bit PowerShell.
  DetailPrint $(MsgRegistering)
  ${DisableX64FSRedirection}
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\register.ps1" -Action Register -InstallDir "$INSTDIR" -Language $(LangTag)'
  Pop $0
  ${EnableX64FSRedirection}
  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION $(MsgRegisterFailed) /SD IDOK
  ${EndIf}
SectionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext all
  ${DisableX64FSRedirection}
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\register.ps1" -Action Unregister -InstallDir "$INSTDIR" -Language $(LangTag)'
  Pop $0
  ${EnableX64FSRedirection}

  Delete "$SMPROGRAMS\ContextBridge.lnk"
  Delete /REBOOTOK "$INSTDIR\ContextBridge.exe"
  Delete "$INSTDIR\ContextBridge.msix"
  Delete "$INSTDIR\register.ps1"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\LICENSE.txt"
  Delete /REBOOTOK "$INSTDIR\ContextBridge.Shell.dll"
  Delete /REBOOTOK "$INSTDIR\*.tmp"
  Delete "$INSTDIR\uninstall.exe"
  RMDir /REBOOTOK "$INSTDIR"
  DeleteRegKey HKLM "${UNINSTKEY}"

  ; Let the user decide whether to keep their command definitions.
  SetShellVarContext current
  StrCpy $0 "$LOCALAPPDATA\ContextBridge"
  ${If} ${FileExists} "$0\*.*"
    MessageBox MB_YESNO|MB_DEFBUTTON2|MB_ICONQUESTION $(MsgDeleteSettings) /SD IDNO IDNO keep
    RMDir /r "$0"
  keep:
  ${EndIf}
SectionEnd

Function un.onInit
  SetRegView 64
FunctionEnd
