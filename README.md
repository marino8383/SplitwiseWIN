# Splitwise Uploader (BPER / Satispay / Stamp → Splitwise)

App WinForms .NET 8 per importare spese da BPER/Satispay (testo, CSV, cartella CSV) e da
screenshot (OCR offline), gestirle in un ciclo di vita persistente (SQLite) e inviarle a
Splitwise via API, con deduplica e archivio.

## Requisiti
- Windows, .NET 8 SDK
- Consumer Key + Consumer Secret da https://secure.splitwise.com/apps
- Per la modalità STAMP: `tessdata/ita.traineddata` (vedi sotto)

## Setup
1. `appsettings.json`: inserisci `ConsumerKey` e `ConsumerSecret`.
2. Trova il GroupId:  `dotnet run -- groups`  → copia l'id in `GroupId`.
3. (Solo STAMP) crea `./tessdata` e scarica `ita.traineddata` da
   https://github.com/tesseract-ocr/tessdata_fast
4. Avvia:  `dotnet run`

## Le tre schede

### Da inviare
Le spese importate finiscono qui con stato **Pending**, in ordine di **data decrescente**
(le più recenti in alto). Ogni riga ha la checkbox "Invia", la divisione (Equal/Exact/Percent)
e il bottone "Imposta…" per le quote.

Modi per importare (tutte salvate subito nel DB locale):
- **Analizza testo** — incolli i movimenti. Fonte CSV.
- **Carica CSV…** — un singolo file. Fonte CSV.
- **Processa CSV (cartella)…** — legge tutti i `.csv` di una cartella (es. Google Drive
  sincronizzato) e li sposta in `processati`. Fonte CSV.
- **Processa STAMP (cartella)…** — OCR sugli screenshot. Fonte STAMP.
- **+ Riga manuale** — riga vuota da compilare. Fonte MANUALE.

Azioni:
- **Invia selezionate** — invia a Splitwise le righe spuntate.
- **Archivia selezionate** — sposta le righe spuntate in Archivio senza inviarle.

### Inviate
Tutte le spese inviate **con successo**, con **data/ora di invio** e **ID Splitwise**.
Sono spese chiuse: restano qui come traccia.

### Archivio
Spese messe da parte senza inviare. Puoi:
- **Dearchivia selezionate** (spunta la colonna Sel) — tornano in "Da inviare".
- **Dearchivia tutte** — riporta in blocco tutte le archiviate.

## Deduplica (data + importo)
Prima dell'invio l'app confronta ogni spesa con quelle **già inviate**: se trova **stessa
data e stesso importo** (la dicitura è ignorata), ti avvisa elencandole e chiede se inviare
comunque, saltare i duplicati o annullare.

## Flusso consigliato BPER → Drive → PC
1. Dal telefono esporti i movimenti BPER in CSV in una cartella Google Drive.
2. Sul PC con Google Drive per desktop quella cartella è locale (es. `G:\Il mio Drive\BPER`).
3. "Processa CSV (cartella)…" → i file vengono letti e archiviati in `BPER\processati`.

## Note tecniche
- Auth OAuth2 `client_credentials` (bastano consumer key/secret).
- `create_expense` può dare HTTP 200 anche con errore: il client controlla sempre `errors`.
- Stato spese in SQLite `history.db`: Pending / Inviata / Archiviata / EliminataLogicamente.
- Il "payer" è il primo membro del gruppo: se non sei tu, modifica `payer` in `MainForm.BuildShares`.
- Parser e OCR sono euristici: rivedi sempre i dati prima di inviare.
