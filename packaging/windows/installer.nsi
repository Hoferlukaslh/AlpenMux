; ======================================================================================================
; Fichier      :  installer.nsi
; Projet       :  AlpenMux
;
; Description  :
;     Installateur Windows (x64) d'AlpenMux, construit par build.sh :
;         makensis -DVERSION=1.2.0 -DSRCDIR=<dossier publié> -DOUTFILE=<fichier .exe> installer.nsi
;     - application dans Program Files, raccourcis menu Démarrer / bureau, inscription dans « Applications installées »
;     - WSL2 activé si besoin (`wsl --install --no-distribution`) : vLLM tourne dans WSL ; redémarrage proposé à la fin
;     - port 9000 réservé (http.sys) pour l'adresse API unique, utilisable sans droits administrateur
;     - ancienne version détectée : désinstallation proposée avant d'installer
;     - désinstallation : propose aussi de supprimer les réglages, l'historique et la distribution WSL « AlpenMux »
;     Français et anglais, choisis d'après la langue de Windows.
; ======================================================================================================

Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"

;--------------------------------
; Définitions (VERSION, SRCDIR, OUTFILE donnés par build.sh)
!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!ifndef SRCDIR
  !error "SRCDIR manquant : dossier de l'application publiée (dotnet publish -r win-x64)"
!endif
!ifndef OUTFILE
  !define OUTFILE "AlpenMux_${VERSION}_Setup.exe"
!endif

!define AppName "AlpenMux"
!define AppId "alpenmux"
!define AppExe "alpenmux.exe"
!define Publisher "Lukas Hofer"
!define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\${AppName}"
!define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
!define RouterUrl "http://+:9000/"

Name "${AppName} ${VERSION}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\${AppName}"
InstallDirRegKey HKLM "${UninstallKey}" "InstallLocation"
RequestExecutionLevel admin      ; Program Files, activation de WSL, réservation du port
SetCompressor /SOLID lzma
ManifestDPIAware true
BrandingText "${AppName} ${VERSION}"

;--------------------------------
; Métadonnées de l'exécutable d'installation
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=0 "ProductName" "${AppName}"
VIAddVersionKey /LANG=0 "CompanyName" "${Publisher}"
VIAddVersionKey /LANG=0 "LegalCopyright" "© 2026 ${Publisher}"
VIAddVersionKey /LANG=0 "FileDescription" "${AppName} ${VERSION} Setup"
VIAddVersionKey /LANG=0 "FileVersion" "${VERSION}.0"
VIAddVersionKey /LANG=0 "ProductVersion" "${VERSION}"

;--------------------------------
; Apparence : couleurs et logo d'AlpenMux
!define MUI_ICON "..\..\Assets\alpenmux.ico"
!define MUI_UNICON "..\..\Assets\alpenmux.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "welcome.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "welcome.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "header.bmp"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC

;--------------------------------
; Pages
!define MUI_WELCOMEPAGE_TITLE "$(WelcomeTitle)"
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeText)"
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_HEADER_TEXT "$(InfoHeader)"
!define MUI_PAGE_HEADER_SUBTEXT "$(InfoSubheader)"
!define MUI_LICENSEPAGE_TEXT_TOP "$(InfoTop)"
!define MUI_LICENSEPAGE_BUTTON "$(^NextBtn)"
!define MUI_LICENSEPAGE_TEXT_BOTTOM " "
!insertmacro MUI_PAGE_LICENSE "$(InfoFile)"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "$(FinishTitle)"
!define MUI_FINISHPAGE_TEXT "$(FinishText)"
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "$(FinishRun)"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchAsUser
!insertmacro MUI_PAGE_FINISH

!define MUI_UNWELCOMEPAGE_TITLE "$(UnWelcomeTitle)"
!define MUI_UNWELCOMEPAGE_TEXT "$(UnWelcomeText)"
!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

;--------------------------------
; Langues (la première correspondant à la langue de Windows est choisie, sinon le français)
!insertmacro MUI_LANGUAGE "French"
!insertmacro MUI_LANGUAGE "English"

LicenseLangString InfoFile ${LANG_FRENCH} "INFO_fr.txt"
LicenseLangString InfoFile ${LANG_ENGLISH} "INFO_en.txt"

LangString WelcomeTitle ${LANG_FRENCH} "Bienvenue dans l'installation d'${AppName} ${VERSION}"
LangString WelcomeTitle ${LANG_ENGLISH} "Welcome to the ${AppName} ${VERSION} Setup"
LangString WelcomeText ${LANG_FRENCH} "${AppName} installe vLLM, télécharge des modèles d'IA et lance plusieurs serveurs compatibles OpenAI derrière une seule adresse.$\r$\n$\r$\nvLLM tourne dans WSL2 (Linux intégré à Windows) : l'installation l'active si nécessaire, puis l'application installe le reste en un clic au premier lancement.$\r$\n$\r$\nCliquez sur Suivant pour continuer."
LangString WelcomeText ${LANG_ENGLISH} "${AppName} installs vLLM, downloads AI models and runs several OpenAI-compatible servers behind a single address.$\r$\n$\r$\nvLLM runs in WSL2 (Linux built into Windows): Setup enables it if needed, then the application installs the rest with one click on first start.$\r$\n$\r$\nClick Next to continue."
LangString InfoHeader ${LANG_FRENCH} "Informations"
LangString InfoHeader ${LANG_ENGLISH} "Information"
LangString InfoSubheader ${LANG_FRENCH} "Ce qui va être installé et où sont vos données."
LangString InfoSubheader ${LANG_ENGLISH} "What will be installed and where your data is kept."
LangString InfoTop ${LANG_FRENCH} "À lire avant l'installation :"
LangString InfoTop ${LANG_ENGLISH} "Please read before installing:"
LangString FinishTitle ${LANG_FRENCH} "Installation terminée"
LangString FinishTitle ${LANG_ENGLISH} "Setup complete"
LangString FinishText ${LANG_FRENCH} "${AppName} est installé.$\r$\n$\r$\nAu premier lancement, la page Installation propose « Tout installer » : distribution Linux, outils et vLLM.$\r$\n$\r$\nSi WSL2 vient d'être activé, redémarrez d'abord Windows."
LangString FinishText ${LANG_ENGLISH} "${AppName} is installed.$\r$\n$\r$\nOn first start, the Setup page offers « Install everything »: Linux distribution, tools and vLLM.$\r$\n$\r$\nIf WSL2 was just enabled, restart Windows first."
LangString FinishRun ${LANG_FRENCH} "Lancer ${AppName}"
LangString FinishRun ${LANG_ENGLISH} "Start ${AppName}"
LangString UnWelcomeTitle ${LANG_FRENCH} "Désinstallation d'${AppName}"
LangString UnWelcomeTitle ${LANG_ENGLISH} "Uninstall ${AppName}"
LangString UnWelcomeText ${LANG_FRENCH} "${AppName} va être retiré de cet ordinateur. Les serveurs en cours seront arrêtés.$\r$\n$\r$\nLes modèles téléchargés ne sont jamais supprimés.$\r$\n$\r$\nCliquez sur Suivant pour continuer."
LangString UnWelcomeText ${LANG_ENGLISH} "${AppName} will be removed from this computer. Running servers will be stopped.$\r$\n$\r$\nDownloaded models are never deleted.$\r$\n$\r$\nClick Next to continue."

LangString SecMainName ${LANG_FRENCH} "${AppName} (requis)"
LangString SecMainName ${LANG_ENGLISH} "${AppName} (required)"
LangString SecMainDesc ${LANG_FRENCH} "L'application et le raccourci du menu Démarrer."
LangString SecMainDesc ${LANG_ENGLISH} "The application and its Start menu shortcut."
LangString SecDesktopName ${LANG_FRENCH} "Raccourci sur le bureau"
LangString SecDesktopName ${LANG_ENGLISH} "Desktop shortcut"
LangString SecDesktopDesc ${LANG_FRENCH} "Une icône ${AppName} sur le bureau."
LangString SecDesktopDesc ${LANG_ENGLISH} "An ${AppName} icon on the desktop."
LangString SecWslName ${LANG_FRENCH} "WSL2 (nécessaire pour vLLM)"
LangString SecWslName ${LANG_ENGLISH} "WSL2 (needed for vLLM)"
LangString SecWslDesc ${LANG_FRENCH} "Active le sous-système Windows pour Linux s'il ne l'est pas encore. Un redémarrage peut être demandé. Sans WSL2, seuls les modèles GGUF (llama.cpp) fonctionnent."
LangString SecWslDesc ${LANG_ENGLISH} "Enables the Windows Subsystem for Linux if it is not enabled yet. A restart may be requested. Without WSL2, only GGUF models (llama.cpp) work."
LangString SecAutostartName ${LANG_FRENCH} "Lancer à l'ouverture de session"
LangString SecAutostartName ${LANG_ENGLISH} "Start when signing in"
LangString SecAutostartDesc ${LANG_FRENCH} "${AppName} démarre en arrière-plan (icône près de l'horloge) : l'adresse unique est toujours disponible."
LangString SecAutostartDesc ${LANG_ENGLISH} "${AppName} starts in the background (icon near the clock): the single address is always available."

LangString OldVersion ${LANG_FRENCH} "Une version d'${AppName} est déjà installée.$\r$\n$\r$\n- Oui : la désinstaller d'abord (vos réglages sont gardés).$\r$\n- Non : installer par-dessus.$\r$\n- Annuler : quitter."
LangString OldVersion ${LANG_ENGLISH} "A version of ${AppName} is already installed.$\r$\n$\r$\n- Yes: uninstall it first (your settings are kept).$\r$\n- No: install over it.$\r$\n- Cancel: quit."
LangString Need64 ${LANG_FRENCH} "${AppName} demande Windows 10 ou 11 en 64 bits."
LangString Need64 ${LANG_ENGLISH} "${AppName} requires 64-bit Windows 10 or 11."
LangString WslChecking ${LANG_FRENCH} "Vérification de WSL2…"
LangString WslChecking ${LANG_ENGLISH} "Checking WSL2…"
LangString WslPresent ${LANG_FRENCH} "WSL2 est déjà installé."
LangString WslPresent ${LANG_ENGLISH} "WSL2 is already installed."
LangString WslInstalling ${LANG_FRENCH} "Activation de WSL2 (cela peut prendre quelques minutes)…"
LangString WslInstalling ${LANG_ENGLISH} "Enabling WSL2 (this may take a few minutes)…"
LangString WslReboot ${LANG_FRENCH} "WSL2 activé : Windows doit redémarrer avant le premier lancement d'${AppName}."
LangString WslReboot ${LANG_ENGLISH} "WSL2 enabled: Windows must restart before ${AppName} is first started."
LangString WslFailed ${LANG_FRENCH} "WSL2 n'a pas pu être activé (code $0). Vous pourrez réessayer depuis la page Installation d'${AppName}."
LangString WslFailed ${LANG_ENGLISH} "WSL2 could not be enabled (code $0). You can try again from ${AppName}'s Setup page."
LangString UnRemoveData ${LANG_FRENCH} "Supprimer aussi vos données ?$\r$\n$\r$\n- réglages, favoris et token Hugging Face$\r$\n- historique des serveurs, llama.cpp, audios et images générés$\r$\n- la distribution WSL « ${AppName} » (vLLM et ses paquets)$\r$\n$\r$\nLes modèles téléchargés sont gardés."
LangString UnRemoveData ${LANG_ENGLISH} "Also delete your data?$\r$\n$\r$\n- settings, favourites and Hugging Face token$\r$\n- server history, llama.cpp, generated audio and images$\r$\n- the « ${AppName} » WSL distribution (vLLM and its packages)$\r$\n$\r$\nDownloaded models are kept."

;--------------------------------
; Initialisation : 64 bits, ancienne version
Function .onInit
    ${IfNot} ${RunningX64}
        MessageBox MB_ICONSTOP "$(Need64)"
        Abort
    ${EndIf}
    SetRegView 64

    ReadRegStr $R0 HKLM "${UninstallKey}" "QuietUninstallString"
    StrCmp $R0 "" done
    ReadRegStr $R1 HKLM "${UninstallKey}" "InstallLocation"
    MessageBox MB_YESNOCANCEL|MB_ICONQUESTION "$(OldVersion)" /SD IDNO IDYES uninst IDNO done
    Abort
  uninst:
    ; Désinstallation silencieuse : les données de l'utilisateur sont gardées
    ExecWait '$R0 _?=$R1'
  done:
FunctionEnd

; Lancement sans les droits administrateur de l'installateur (l'application est celle de l'utilisateur)
Function LaunchAsUser
    Exec '"$WINDIR\explorer.exe" "$INSTDIR\${AppExe}"'
FunctionEnd

;--------------------------------
; Sections
Section "$(SecMainName)" SecMain
    SectionIn RO
    SetRegView 64
    ; Application en cours : fermée pour remplacer ses fichiers
    nsExec::Exec '"$SYSDIR\taskkill.exe" /IM ${AppExe} /F'
    Sleep 500

    SetOutPath "$INSTDIR"
    File /r "${SRCDIR}\*.*"
    WriteUninstaller "$INSTDIR\uninstall.exe"

    ; Raccourci du menu Démarrer (tous les utilisateurs)
    SetShellVarContext all
    CreateShortCut "$SMPROGRAMS\${AppName}.lnk" "$INSTDIR\${AppExe}" "" "$INSTDIR\${AppExe}" 0
    SetShellVarContext current

    ; Adresse API unique : port 9000 réservé pour tous les utilisateurs (sinon seul « localhost » est permis)
    nsExec::Exec '"$SYSDIR\netsh.exe" http delete urlacl url=${RouterUrl}'
    nsExec::Exec '"$SYSDIR\netsh.exe" http add urlacl url=${RouterUrl} sddl=D:(A;;GX;;;WD)'

    ; Applications installées
    ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
    IntFmt $0 "0x%08X" $0
    WriteRegStr HKLM "${UninstallKey}" "DisplayName" "${AppName}"
    WriteRegStr HKLM "${UninstallKey}" "DisplayVersion" "${VERSION}"
    WriteRegStr HKLM "${UninstallKey}" "Publisher" "${Publisher}"
    WriteRegStr HKLM "${UninstallKey}" "DisplayIcon" "$\"$INSTDIR\${AppExe}$\",0"
    WriteRegStr HKLM "${UninstallKey}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${UninstallKey}" "UninstallString" "$\"$INSTDIR\uninstall.exe$\""
    WriteRegStr HKLM "${UninstallKey}" "QuietUninstallString" "$\"$INSTDIR\uninstall.exe$\" /S"
    WriteRegStr HKLM "${UninstallKey}" "URLInfoAbout" "https://docs.vllm.ai"
    WriteRegDWORD HKLM "${UninstallKey}" "NoModify" 1
    WriteRegDWORD HKLM "${UninstallKey}" "NoRepair" 1
    WriteRegDWORD HKLM "${UninstallKey}" "EstimatedSize" $0
SectionEnd

Section "$(SecDesktopName)" SecDesktop
    SetShellVarContext current
    CreateShortCut "$DESKTOP\${AppName}.lnk" "$INSTDIR\${AppExe}" "" "$INSTDIR\${AppExe}" 0
SectionEnd

Section "$(SecWslName)" SecWsl
    ; L'installateur est en 32 bits : wsl.exe n'existe que dans le System32 « 64 bits »
    ${DisableX64FSRedirection}
    DetailPrint "$(WslChecking)"
    nsExec::ExecToLog '"$SYSDIR\wsl.exe" --status'
    Pop $0
    ${If} $0 == 0
        DetailPrint "$(WslPresent)"
    ${Else}
        DetailPrint "$(WslInstalling)"
        nsExec::ExecToLog '"$SYSDIR\wsl.exe" --install --no-distribution'
        Pop $0
        ${If} $0 == 0
            DetailPrint "$(WslReboot)"
            SetRebootFlag true
        ${Else}
            DetailPrint "$(WslFailed)"
            MessageBox MB_ICONEXCLAMATION "$(WslFailed)" /SD IDOK
        ${EndIf}
    ${EndIf}
    ${EnableX64FSRedirection}
SectionEnd

Section /o "$(SecAutostartName)" SecAutostart
    WriteRegStr HKCU "${RunKey}" "${AppName}" '"$INSTDIR\${AppExe}" --minimized'
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
    !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} "$(SecMainDesc)"
    !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "$(SecDesktopDesc)"
    !insertmacro MUI_DESCRIPTION_TEXT ${SecWsl} "$(SecWslDesc)"
    !insertmacro MUI_DESCRIPTION_TEXT ${SecAutostart} "$(SecAutostartDesc)"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

;--------------------------------
; Désinstallation
Function un.onInit
    SetRegView 64
FunctionEnd

Section "Uninstall"
    ; Application fermée (les serveurs qu'elle a lancés sont arrêtés par elle-même à la fermeture normale)
    nsExec::Exec '"$SYSDIR\taskkill.exe" /IM ${AppExe} /F'
    nsExec::Exec '"$SYSDIR\taskkill.exe" /IM llama-server.exe /F'
    Sleep 1000

    RMDir /r "$INSTDIR"
    SetShellVarContext all
    Delete "$SMPROGRAMS\${AppName}.lnk"
    SetShellVarContext current
    Delete "$DESKTOP\${AppName}.lnk"
    DeleteRegValue HKCU "${RunKey}" "${AppName}"
    nsExec::Exec '"$SYSDIR\netsh.exe" http delete urlacl url=${RouterUrl}'
    DeleteRegKey HKLM "${UninstallKey}"

    ; Données de l'utilisateur : seulement s'il le demande (jamais en désinstallation silencieuse, ex. mise à jour)
    MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "$(UnRemoveData)" /SD IDNO IDNO keep
        ${DisableX64FSRedirection}
        nsExec::ExecToLog '"$SYSDIR\wsl.exe" --unregister ${AppName}'
        ${EnableX64FSRedirection}
        RMDir /r "$APPDATA\${AppId}"
        RMDir /r "$LOCALAPPDATA\${AppId}"
  keep:
SectionEnd
