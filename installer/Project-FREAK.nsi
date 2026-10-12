; Project-FREAK Windows installer (64-bit, per-machine, requires admin)
;
; Build with installer\build-installer.ps1, or manually from the installer folder:
;   makensis Project-FREAK.nsi
;
; Inputs (paths are relative to this script):
;   ..\publish\win-x64                    self-contained publish output
;   redist\LabJackBasic_2025-02-12.exe    LabJack minimal LJM installer (run silently with /S)
;   redist\LabJacka64\                    LabJack 64-bit USB driver package (dpinst64.exe, LabJacka64.inf)
;
; Output:
;   output\Project-FREAK-Setup-<version>.exe

; This NSIS install has no amd64 stubs, so the default 32-bit stub is used. The installer still
; targets 64-bit Windows: it aborts on 32-bit OS, uses SetRegView 64, and turns off WOW64 file
; system redirection when it checks System32 for LabJackM.dll.

!define APP_NAME      "Project-FREAK"
!define APP_VERSION   "2.0.0"
!define APP_PUBLISHER "Missouri Space Program"
!define APP_EXE       "Project-FREAK.exe"
!define UNINST_KEY    "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"

!define PUBLISH_DIR   "..\publish\win-x64"
!define LJM_EXE       "LabJackBasic_2025-02-12.exe"
!define LJM_PATH      "redist\${LJM_EXE}"
!define LJ_DRIVER_DIR "redist\LabJacka64"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

Name "${APP_NAME} ${APP_VERSION}"
OutFile "output\${APP_NAME}-Setup-${APP_VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey "LegalCopyright" "${APP_PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP_NAME} Installer"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"

!define MUI_ABORTWARNING

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "${APP_NAME} requires 64-bit Windows."
    Abort
  ${EndIf}

  SetRegView 64
  ; Per-machine shortcuts (Start Menu and Public Desktop), not the admin user's profile
  SetShellVarContext all

  ; If an earlier version is registered, offer to remove it before installing
  ReadRegStr $R0 HKLM "${UNINST_KEY}" "UninstallString"
  ReadRegStr $R1 HKLM "${UNINST_KEY}" "InstallLocation"
  ${If} $R0 != ""
    MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "An earlier version of ${APP_NAME} is installed. Click OK to remove it and continue, or Cancel to stop." IDOK remove_old
    Abort
  remove_old:
    ; /S = silent. _?= runs the old uninstaller in place so it does not copy itself to TEMP.
    ExecWait '$R0 /S _?=$R1'
  ${EndIf}
FunctionEnd

Section "${APP_NAME} (required)" SecApp
  SectionIn RO

  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
SectionEnd

Function IsLJMInstalled
  ; Pushes 1 if LabJackM.dll is in the real 64-bit System32, otherwise 0.
  ; A 32-bit installer sees $SYSDIR as SysWOW64, where the 64-bit DLL is never placed.
  ; Redirection is turned off so the explicit System32 path reaches the real folder.
  Push $0
  StrCpy $0 0
  ${DisableX64FSRedirection}
  IfFileExists "$WINDIR\System32\LabJackM.dll" 0 +2
  StrCpy $0 1
  ${EnableX64FSRedirection}
  Exch $0
FunctionEnd

Section "LabJack LJM library (LabJackM.dll)" SecLJM
  ; Only run LabJack's installer when LabJackM.dll is missing, so an existing LJM install
  ; (possibly used by other software) is left alone.
  Call IsLJMInstalled
  Pop $0
  ${If} $0 == 1
    DetailPrint "LabJack LJM library already installed, skipping."
  ${Else}
    DetailPrint "Installing LabJack LJM library (silent)..."
    SetOutPath "$TEMP\${APP_NAME}-setup"
    File "${LJM_PATH}"
    ExecWait '"$TEMP\${APP_NAME}-setup\${LJM_EXE}" /S' $1
    DetailPrint "LabJack installer exit code: $1"
    Delete "$TEMP\${APP_NAME}-setup\${LJM_EXE}"
    RMDir "$TEMP\${APP_NAME}-setup"

    Call IsLJMInstalled
    Pop $0
    ${If} $0 == 0
      MessageBox MB_OK|MB_ICONEXCLAMATION "The LabJack LJM library did not install. LabJack devices will not connect until LJM is installed. Install LJM from labjack.com, then run this installer again."
    ${EndIf}
  ${EndIf}
SectionEnd

Section "LabJack USB driver" SecUSB
  ; Only needed for USB-connected LabJack devices. Ethernet devices do not use it.
  SetOutPath "$INSTDIR\Drivers\LabJacka64"
  File /r "${LJ_DRIVER_DIR}\*.*"

  DetailPrint "Installing LabJack USB driver (silent)..."
  ExecWait '"$INSTDIR\Drivers\LabJacka64\dpinst64.exe" /c /sa /f /lm /sw /PATH "$INSTDIR\Drivers\LabJacka64"' $0
  DetailPrint "Driver installer exit code: $0"

  ; dpinst returns 0 or 256 on success (same codes LabJack's example installer accepts)
  ${If} $0 != 0
  ${AndIf} $0 != 256
    MessageBox MB_OK|MB_ICONEXCLAMATION "The LabJack USB driver may not have installed (exit code $0). USB devices will not connect until it is installed. Ethernet devices are not affected."
  ${EndIf}
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecApp} "Project-FREAK application, Start Menu and desktop shortcuts, and the uninstaller."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecLJM} "LabJack LJM library. Installed only if LabJackM.dll is not already present."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecUSB} "LabJack 64-bit USB driver. Needed for USB devices. Not needed for Ethernet devices."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function un.onInit
  SetRegView 64
  SetShellVarContext all
FunctionEnd

Section "un.${APP_NAME}"
  ; Removed: program files, shortcuts, and the Add/Remove Programs entry.
  ; Kept on purpose: Documents\RocketTests (test data), %LOCALAPPDATA%\Project-FREAK (settings),
  ; and the LabJack LJM library and USB driver (shared with other LabJack software).
  Delete "$DESKTOP\${APP_NAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APP_NAME}"

  ; Guard: only delete the install folder if this uninstaller is really in one
  IfFileExists "$INSTDIR\${APP_EXE}" 0 skip_files
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR"
skip_files:

  DeleteRegKey HKLM "${UNINST_KEY}"
SectionEnd
