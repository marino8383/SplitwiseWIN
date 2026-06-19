# scarica-bper.ps1
# Scarico assistito BPER (conto + carta) e import nel programma - usando il TUO profilo Chrome.
#
# Flusso: apre BPER nel tuo Chrome (login/password salvati), tu fai login+2FA ed esporti;
# poi lo script RACCOGLIE i file scaricati di recente dalla cartella Download e li sposta nella inbox,
# infine lancia l'import. Login/2FA/export li fai tu.

$ErrorActionPreference = 'Stop'

# ---------------- CONFIG (modifica se serve) ----------------
$InboxFolder   = 'G:\Il mio Drive\Splitwise'
$BperUrl       = 'https://homebanking.bpergroup.net/auth/#/auth?bank=05387'
# Eseguibile che fa l'import: prima quello ACCANTO allo script (in SW usa l'app di SW e il suo DB),
# altrimenti il percorso di produzione fisso.
$Exe = 'G:\Il mio Drive\Splitwise\SW\SplitwiseUploader.exe'
$ExeLocal = Join-Path $PSScriptRoot 'SplitwiseUploader.exe'
if (Test-Path $ExeLocal) { $Exe = $ExeLocal }
$DownloadsDir  = Join-Path $env:USERPROFILE 'Downloads'   # cartella dove Chrome scarica i file
# ------------------------------------------------------------

Write-Host "=== Scarico assistito BPER ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InboxFolder | Out-Null

$since = Get-Date   # da qui in poi consideriamo "nuovi" i file scaricati

# apri BPER nel TUO Chrome (profilo normale, con i tuoi accessi salvati)
$chrome = @(
  "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
  "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
  "$env:LocalAppData\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($chrome) { Start-Process $chrome -ArgumentList @($BperUrl) }   # niente --user-data-dir: usa il tuo profilo
else { Start-Process $BperUrl }

Write-Host ""
Write-Host "PASSI:" -ForegroundColor Cyan
Write-Host "  1) Fai login e 2FA su BPER."
Write-Host "  2) CARTA: esporta i movimenti in .xls."
Write-Host "  3) CONTO: esporta in .xls (oppure usa il copia-testo qui sotto)."
Write-Host "  (consiglio: ultimi ~15 giorni; i doppioni vengono scartati)"
Write-Host ""

# CONTO alternativo: testo copiato dalla pagina salvato come .txt nella inbox
$contoMode = Read-Host "Hai COPIATO il testo del conto da salvare come .txt? (s = si / INVIO = no)"
if ($contoMode -match '^[sS]') {
    $contoText = Get-Clipboard -Raw
    if ([string]::IsNullOrWhiteSpace($contoText)) { Write-Host "Appunti vuoti: niente .txt." -ForegroundColor Yellow }
    else {
        $contoFile = Join-Path $InboxFolder ("conto_{0:yyyyMMdd_HHmmss}.txt" -f (Get-Date))
        Set-Content -Path $contoFile -Value $contoText -Encoding UTF8
        Write-Host "Testo conto salvato in: $contoFile" -ForegroundColor Green
    }
}

Write-Host ""
Read-Host "Premi INVIO quando hai finito di SCARICARE i file (.xls) dalla banca"

# raccogli i file scaricati di recente e spostali nella inbox
$nuovi = Get-ChildItem -LiteralPath $DownloadsDir -File -ErrorAction SilentlyContinue |
         Where-Object { $_.LastWriteTime -ge $since -and $_.Extension -match '^\.(xls|xlsx|csv)$' }
if ($nuovi) {
    foreach ($f in $nuovi) {
        $dest = Join-Path $InboxFolder $f.Name
        if (Test-Path $dest) { $dest = Join-Path $InboxFolder ("{0}_{1:HHmmss}{2}" -f $f.BaseName, (Get-Date), $f.Extension) }
        Move-Item -LiteralPath $f.FullName -Destination $dest -Force
        Write-Host "Spostato in inbox: $($f.Name)" -ForegroundColor Green
    }
} else {
    Write-Host "Nessun file .xls/.xlsx/.csv recente trovato in $DownloadsDir." -ForegroundColor Yellow
    Write-Host "Se hai scaricato altrove, sposta i file a mano in: $InboxFolder"
}

# lancia il batch di import sull'eseguibile di PRODUZIONE (-> DB di produzione)
if (Test-Path $Exe) {
    Write-Host "Importazione in corso (DB di: $Exe)..." -ForegroundColor Cyan
    & $Exe inbox
    Write-Host "Fatto. Log in: $(Join-Path (Split-Path $Exe) 'logs')" -ForegroundColor Green
}
else { Write-Host "Eseguibile non trovato: $Exe - aprilo e usa 'Processa inbox' (o correggi il percorso in cima allo script)." -ForegroundColor Yellow }

Read-Host "Premi INVIO per chiudere"
