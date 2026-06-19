// scarica-bper-auto.js  —  scarico BPER semi-automatico con clic guidati e date automatiche.
// Login e 2FA li fai TU a mano (non sono automatizzabili). Lo script fa il resto:
// imposta il periodo (dal 1° del mese a oggi), scarica CONTO e CARTA in .xls nella cartella inbox.
//
// Requisiti (una volta): npm i -D playwright  &&  npx playwright install chromium
// Avvio:                 node scarica-bper-auto.js
//
// NB: i selettori derivano da una registrazione del sito BPER: se BPER cambia la pagina,
//     vanno riregistrati (npx playwright codegen ...). Niente credenziali qui dentro.

const { chromium } = require('playwright');
const readline = require('readline');
const path = require('path');
const fs = require('fs');
const { spawnSync } = require('child_process');

// ---------------- CONFIG ----------------
const INBOX = 'G:\\Il mio Drive\\Splitwise';                 // dove salvare i file scaricati
const AUTH_URL = 'https://homebanking.bpergroup.net/auth/#/auth?bank=05387';
const HOME_URL = 'https://homebanking.bpergroup.net/swb/#/homepage';
const EXE = 'G:\\Il mio Drive\\Splitwise\\SW\\SplitwiseUploader.exe'; // batch da lanciare a fine (se esiste)
// ----------------------------------------

const ask = (q) => new Promise((res) => {
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
  rl.question(q, (a) => { rl.close(); res(a); });
});

(async () => {
  fs.mkdirSync(INBOX, { recursive: true });
  const now = new Date();
  const fromDay = '1';                       // dal 1° del mese corrente
  const toDay = String(now.getDate());       // a oggi (stesso mese → niente navigazione calendario)
  const stamp = now.toISOString().slice(0, 19).replace(/[:T-]/g, '');

  const browser = await chromium.launch({ headless: false });
  const ctx = await browser.newContext({ acceptDownloads: true });
  const page = await ctx.newPage();

  async function saveDownload(p, label) {
    const d = await p;
    const name = `bper_${label}_${stamp}_${d.suggestedFilename()}`;
    const dest = path.join(INBOX, name);
    await d.saveAs(dest);
    console.log('  scaricato:', dest);
  }

  // apre BPER e attende il login manuale + 2FA
  await page.goto(AUTH_URL);
  try { await page.getByRole('button', { name: 'Accetta' }).click({ timeout: 4000 }); } catch {}
  console.log('\n>>> Fai LOGIN e 2FA nella finestra del browser.');
  await ask('>>> Quando sei dentro (home banking aperto), premi INVIO qui per continuare...');

  try {
    // ---------- CONTO ----------
    console.log('CONTO: imposto periodo e scarico...');
    await page.goto(HOME_URL);
    await page.locator('.medium-icon').first().click();
    await page.locator('a').nth(3).click();
    await page.getByRole('button', { name: 'Filtra' }).click();
    await page.getByRole('button').filter({ hasText: /^$/ }).nth(1).click();   // apre "Dal"
    await page.getByRole('button', { name: fromDay, exact: true }).first().click();
    await page.getByRole('button').filter({ hasText: /^$/ }).nth(2).click();   // apre "Al"
    await page.getByRole('button', { name: toDay, exact: true }).first().click();
    await page.getByRole('button', { name: 'Applica filtri' }).click();
    await page.getByText('Scarica').click();
    const contoDl = page.waitForEvent('download');
    await page.getByText('XLS').click();
    await saveDownload(contoDl, 'conto');

    // ---------- CARTA ----------
    console.log('CARTA: imposto periodo e scarico...');
    await page.getByLabel('Vedi carta').first().click();
    await page.getByRole('button', { name: 'Filtra' }).click();
    await page.locator('bper-ds-input-calendar').filter({ hasText: 'Dal' }).getByRole('button').click();
    await page.getByRole('button', { name: fromDay, exact: true }).first().click();
    await page.locator('bper-ds-input-calendar').filter({ hasText: 'Al' }).getByRole('button').click();
    await page.getByRole('button', { name: toDay, exact: true }).first().click();
    await page.getByRole('button', { name: 'Applica filtri' }).click();
    const cartaDl = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Scarica XLS' }).click();
    await saveDownload(cartaDl, 'carta');

    console.log('\nDownload completati in:', INBOX);
  } catch (err) {
    console.error('\nERRORE durante i clic guidati (la pagina BPER potrebbe essere cambiata):', err.message);
    console.error('Puoi completare a mano export/scarico nella stessa finestra.');
    await ask('Premi INVIO quando hai finito (i file devono essere nella cartella inbox)...');
  }

  await browser.close();

  // lancia il batch di import se l'exe esiste
  if (fs.existsSync(EXE)) {
    console.log('Importazione (batch)...');
    spawnSync(EXE, ['inbox'], { stdio: 'inherit' });
  } else {
    console.log('Apri SplitwiseUploader per importare (oppure lancia: SplitwiseUploader.exe inbox).');
  }
  await ask('Fatto. Premi INVIO per chiudere...');
})();
