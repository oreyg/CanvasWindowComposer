!include "MUI2.nsh"

!define VERSION "1.12.1"

; --- General ---
Name "Canvas Window Composer"
OutFile "CanvasWindowComposer-Setup.exe"
InstallDir "$PROGRAMFILES64\CanvasWindowComposer"
RequestExecutionLevel admin

; --- Version info (shown in file properties) ---
VIProductVersion "${VERSION}.0"
VIFileVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "Canvas Window Composer"
VIAddVersionKey "CompanyName" "Devnova"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 Devnova"
VIAddVersionKey "FileDescription" "Canvas Window Composer Installer"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"

; --- UI ---
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\CanvasDesktop.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Run Canvas Window Composer"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; --- Install ---
Section "Install"
    SetOutPath "$INSTDIR"

    ; Application files
    File "publish-fd\CanvasDesktop.exe"
    File "publish-fd\CanvasDesktop.dll"
    File "publish-fd\CanvasDesktop.deps.json"
    File "publish-fd\CanvasDesktop.runtimeconfig.json"
    File "publish-fd\SharpGen.Runtime.dll"
    File "publish-fd\SharpGen.Runtime.COM.dll"
    File "publish-fd\Vortice.D3DCompiler.dll"
    File "publish-fd\Vortice.Direct3D11.dll"
    File "publish-fd\Vortice.DirectX.dll"
    File "publish-fd\Vortice.DXGI.dll"
    File "publish-fd\Vortice.Mathematics.dll"
    File "publish-fd\System.IO.Pipelines.dll"
    File "publish-fd\System.Text.Encodings.Web.dll"
    File "publish-fd\System.Text.Json.dll"

    ; Create uninstaller
    WriteUninstaller "$INSTDIR\Uninstall.exe"

    ; Start menu shortcut
    CreateDirectory "$SMPROGRAMS\Canvas Window Composer"
    CreateShortcut "$SMPROGRAMS\Canvas Window Composer\Canvas Window Composer.lnk" "$INSTDIR\CanvasDesktop.exe"
    CreateShortcut "$SMPROGRAMS\Canvas Window Composer\Uninstall.lnk" "$INSTDIR\Uninstall.exe"

    ; Add to startup with Task Scheduler (runs elevated on login)
    nsExec::ExecToLog 'schtasks /create /tn "CanvasWindowComposer" /tr "\"$INSTDIR\CanvasDesktop.exe\"" /sc onlogon /rl highest /f'

    ; Registry for Add/Remove Programs
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\CanvasWindowComposer" "DisplayName" "Canvas Window Composer"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\CanvasWindowComposer" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\CanvasWindowComposer" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\CanvasWindowComposer" "Publisher" "Devnova"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\CanvasWindowComposer" "DisplayVersion" "${VERSION}"
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\CanvasWindowComposer" "NoModify" 1
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\CanvasWindowComposer" "NoRepair" 1
SectionEnd

; --- Uninstall ---
Section "Uninstall"
    ; Kill running instance
    nsExec::ExecToLog 'taskkill /f /im CanvasDesktop.exe'

    ; Remove scheduled task
    nsExec::ExecToLog 'schtasks /delete /tn "CanvasWindowComposer" /f'

    ; Remove files
    Delete "$INSTDIR\CanvasDesktop.exe"
    Delete "$INSTDIR\CanvasDesktop.dll"
    Delete "$INSTDIR\CanvasDesktop.deps.json"
    Delete "$INSTDIR\CanvasDesktop.runtimeconfig.json"
    Delete "$INSTDIR\SharpGen.Runtime.dll"
    Delete "$INSTDIR\SharpGen.Runtime.COM.dll"
    Delete "$INSTDIR\Vortice.D3DCompiler.dll"
    Delete "$INSTDIR\Vortice.Direct3D11.dll"
    Delete "$INSTDIR\Vortice.DirectX.dll"
    Delete "$INSTDIR\Vortice.DXGI.dll"
    Delete "$INSTDIR\Vortice.Mathematics.dll"
    Delete "$INSTDIR\System.IO.Pipelines.dll"
    Delete "$INSTDIR\System.Text.Encodings.Web.dll"
    Delete "$INSTDIR\System.Text.Json.dll"
    Delete "$INSTDIR\CanvasDesktop.pdb"
    Delete "$INSTDIR\canvas_debug.log"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"

    ; Remove shortcuts
    Delete "$SMPROGRAMS\Canvas Window Composer\Canvas Window Composer.lnk"
    Delete "$SMPROGRAMS\Canvas Window Composer\Uninstall.lnk"
    RMDir "$SMPROGRAMS\Canvas Window Composer"

    ; Remove registry
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\CanvasWindowComposer"
SectionEnd
