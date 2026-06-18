# scarica-bper.ps1
# Scarico assistito dei movimenti BPER (conto + carta) e import nel programma.
#
# Cosa fa:
#  1) apre BPER in un profilo Chrome DEDICATO configurato per scaricare automaticamente
#     i file in $InboxFolder (la cartella Drive letta dal programma);
#  2) aspetta che TU faccia login + 2FA ed esporti conto e carta in .xls;
#  3) lancia l'import batch (SplitwiseUploader.exe inbox), che importa e sposta i file.
#
# Login e 2FA li fai tu: lo script non tocca credenziali.

$ErrorActionPreference = 'Stop'

# ---------------- CONFIG (modifica se serve) ----------------
$InboxFolder = 'G:\Il mio Drive\Splitwise'
$BperUrl     = 'https://homebanking.bpergroup.net/auth/#/auth?bank=05387'   # login home banking BPER
$AppDir      = 'C:\GIT2\splitwise'            # cartella del progetto (per trovare l'exe)
# ------------------------------------------------------------

Write-Host "=== Scarico assistito BPER ===" -ForegroundColor Cyan

# 1) assicura la cartella di destinazione
New-Item -ItemType Directory -Force -Path $InboxFolder | Out-Null

# 2) trova Chrome
$chrome = @(
  "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
  "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
  "$env:LocalAppData\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

# 3) profilo Chrome dedicato che scarica automaticamente nella inbox (niente "salva con nome")
if ($chrome) {
  $profileDir = Join-Path $env:LocalAppData 'SplitwiseBperProfile'
  $defaultDir = Join-Path $profileDir 'Default'
  New-Item -ItemType Directory -Force -Path $defaultDir | Out-Null
  $prefs = @{
    download = @{ default_directory = $InboxFolder; prompt_for_download = $false }
    savefile = @{ default_directory = $InboxFolder }
  } | ConvertTo-Json -Depth 5
  Set-Content -Path (Join-Path $defaultDir 'Preferences') -Value $prefs -Encoding UTF8

  Start-Process $chrome -ArgumentList @("--user-data-dir=`"$profileDir`"", $BperUrl)
  Write-Host "Chrome aperto su BPER (i download vanno in: $InboxFolder)" -ForegroundColor Green
}
else {
  Write-Host "Chrome non trovato: apro il sito nel browser predefinito." -ForegroundColor Yellow
  Write-Host "Imposta a mano la cartella di download su: $InboxFolder" -ForegroundColor Yellow
  Start-Process $BperUrl
}

Write-Host ""
Write-Host "PASSI:" -ForegroundColor Cyan
Write-Host "  1) Fai login e 2FA su BPER."
Write-Host "  (consiglio: scarica/copia sempre gli ultimi ~15 giorni; i doppioni vengono scartati)"
Write-Host ""

# --- CARTA: export .xls (si scarica da solo nella inbox) ---
Write-Host "  CARTA: esporta i movimenti in .xls." -ForegroundColor Cyan
Read-Host "Premi INVIO quando hai scaricato il file CARTA (.xls)"

# --- CONTO: tre modalita' disponibili, scegli quella che preferisci ---
Write-Host ""
Write-Host "  CONTO: puoi usare una di queste (tutte e 3 sono supportate):" -ForegroundColor Cyan
Write-Host "     A) esporta il .xls del conto (si scarica da solo nella inbox)"
Write-Host "     B) copia il testo dei movimenti dalla pagina (Ctrl+A / Ctrl+C)"
Write-Host ""
$contoMode = Read-Host "Hai COPIATO il testo del conto da salvare come .txt? (s = si / INVIO = no, ho usato l'.xls)"
if ($contoMode -match '^[sS]') {
  $contoText = Get-Clipboard -Raw
  if ([string]::IsNullOrWhiteSpace($contoText)) {
    Write-Host "Appunti vuoti: niente .txt salvato." -ForegroundColor Yellow
  }
  else {
    $contoFile = Join-Path $InboxFolder ("conto_{0:yyyyMMdd_HHmmss}.txt" -f (Get-Date))
    Set-Content -Path $contoFile -Value $contoText -Encoding UTF8
    Write-Host "Testo conto salvato in: $contoFile" -ForegroundColor Green
  }
}

Write-Host ""
Read-Host "Premi INVIO per avviare l'importazione"

# 4) trova l'eseguibile piu' recente e lancia il batch
$exe = Get-ChildItem -Path $AppDir -Recurse -Filter 'SplitwiseUploader.exe' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($exe) {
  Write-Host "Importazione in corso..." -ForegroundColor Cyan
  & $exe.FullName inbox
  $logDir = Join-Path (Split-Path $exe.FullName) 'logs'
  Write-Host "Fatto. Log in: $logDir" -ForegroundColor Green
}
else {
  Write-Host "SplitwiseUploader.exe non trovato sotto $AppDir." -ForegroundColor Yellow
  Write-Host "Compila il progetto, poi lancia:  SplitwiseUploader.exe inbox"
}

Read-Host "Premi INVIO per chiudere"
