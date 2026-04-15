!include "MUI2.nsh"

; --- General ---
Name "Canvas Window Composer"
OutFile "CanvasWindowComposer-Setup.exe"
InstallDir "$PROGRAMFILES64\CanvasWindowComposer"
RequestExecutionLevel admin

; --- UI ---
!define MUI_ABORTWARNING
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
    File "publish-fd\DpiHook.dll"

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
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\CanvasWindowComposer" "Publisher" "CanvasWindowComposer"
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
    Delete "$INSTDIR\DpiHook.dll"
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
