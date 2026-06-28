// Headless verification of the §36 planetarium engine + araStel bridge.
// Serves assets/stellarium, loads index.html in headless Chrome, and asserts the
// bridge mutates the engine (location/zoom/pan). Run via the puppeteer Docker
// image — see run.sh. Exits non-zero on failure; prints page console + a result
// JSON so a fix can be verified with no app and no human.
const http = require('http');
const fs = require('fs');
const path = require('path');
const puppeteer = require('puppeteer-core');

const ROOT = process.env.ASSET_ROOT;
const PORT = 8901;
const MIME = { '.html':'text/html','.js':'text/javascript','.wasm':'application/wasm',
  '.json':'application/json','.ttf':'font/ttf','.gz':'application/gzip',
  '.webp':'image/webp','.svg':'image/svg+xml','.png':'image/png' };

function serve() {
  return new Promise((resolve) => {
    const s = http.createServer((req, res) => {
      let p = decodeURIComponent(req.url.split('?')[0]);
      if (p === '/') p = '/index.html';
      fs.readFile(path.join(ROOT, p), (err, data) => {
        if (err) { res.statusCode = 404; return res.end('nf'); }
        res.setHeader('Content-Type', MIME[path.extname(p)] || 'application/octet-stream');
        res.end(data);
      });
    });
    s.listen(PORT, () => resolve(s));
  });
}
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

(async () => {
  const server = await serve();
  const browser = await puppeteer.launch({ headless: 'new', executablePath: process.env.CHROME || '/usr/bin/chromium', args: [ '--disable-dev-shm-usage',
    '--no-sandbox','--ignore-gpu-blocklist','--enable-webgl',
    '--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader' ] });
  const page = await browser.newPage();
  const logs = [];
  page.on('console', m => logs.push('[console] ' + m.text()));
  page.on('pageerror', e => logs.push('[pageerror] ' + e.message));
  page.on('requestfailed', r => logs.push('[reqfail] ' + r.url()));
  page.on('response', r => { if (r.status() >= 400) logs.push('[http ' + r.status() + '] ' + r.url()); });

  await page.goto(`http://localhost:${PORT}/index.html`, { waitUntil: 'load', timeout: 40000 });
  // Poll engine state for up to 60s, dumping diagnostics so we can tell
  // "slow" from "broken" (headless WebGL, missing onReady, etc.).
  let ready = false;
  for (let i = 0; i < 20; i++) {
    const d = await page.evaluate(() => {
      return {
        araStel: typeof window.araStel,
        engineFn: typeof window.StelWebEngine,
        stelReady: !!(window.araStel && window.araStel._stel),
        pending: window.araStel ? window.araStel._pending.length : -1,
      };
    });
    console.log('[poll ' + i + '] ' + JSON.stringify(d));
    if (d.stelReady) { ready = true; break; }
    await sleep(3000);
  }
  const res = { ready };
  if (ready) {
    res.fovType = await page.evaluate(() => typeof window.araStel._stel.fov);
    await page.evaluate(() => window.araStel.setLocation(34.66, -106.78, 1500));
    res.latRad = await page.evaluate(() => window.araStel._stel.core.observer.latitude);
    res.fov0 = await page.evaluate(() => window.araStel._stel.fov);
    await page.evaluate(() => window.araStel.zoomBy(0.5));
    await sleep(1200);
    res.fov1 = await page.evaluate(() => window.araStel._stel.fov);
    res.yaw0 = await page.evaluate(() => window.araStel._stel.core.observer.yaw);
    await page.evaluate(() => window.araStel.panBy(20, 0));
    await sleep(300);
    res.yaw1 = await page.evaluate(() => window.araStel._stel.core.observer.yaw);
  }
  console.log('=== RESULT ===\n' + JSON.stringify(res, null, 2));
  console.log('=== PAGE LOGS (' + logs.length + ') ===\n' + logs.slice(0, 50).join('\n'));
  await browser.close(); server.close();
  // Verdict
  const ok = res.ready && res.fov1 < res.fov0 && Math.abs(res.yaw1 - res.yaw0) > 1e-6 &&
             Math.abs(res.latRad - 34.66 * Math.PI / 180) < 1e-3;
  console.log('VERDICT=' + (ok ? 'PASS' : 'FAIL'));
  process.exit(ok ? 0 : 2);
})().catch(e => { console.error('HARNESS ERROR', e); process.exit(1); });
