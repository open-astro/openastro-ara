// Headless verification of the §36 planetarium engine + page globals.
// Serves assets/stellarium, loads index.html in headless Chrome, and asserts the
// page's globals mutate the engine: `stel` (set in onReady), `zoomBy(factor)`
// and `pointRaDec(ra, dec)` — the same entry points pollCmd's 'zoom' and
// 'goto' commands call. (`window.araStel` was removed with #610's rewrite;
// the harness checked it until #1198.) Run via the puppeteer Docker image —
// see the Dockerfile beside this file. Exits non-zero on failure; prints page
// console + a result JSON so a fix can be verified with no app and no human.
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

  // The observer site is seeded from the URL (lat/lon) and later moved by the
  // 'site' command; assert the seed reaches stel.core.observer.
  await page.goto(`http://localhost:${PORT}/index.html?lat=34.66&lon=-106.78`, { waitUntil: 'load', timeout: 40000 });
  // Poll engine state for up to 60s, dumping diagnostics so we can tell
  // "slow" from "broken" (headless WebGL, missing onReady, etc.).
  let ready = false;
  for (let i = 0; i < 20; i++) {
    const d = await page.evaluate(() => {
      return {
        stel: typeof window.stel,
        engineFn: typeof window.StelWebEngine,
        stelReady: !!(window.stel && window.stel.core),
        zoomBy: typeof window.zoomBy,
        pointRaDec: typeof window.pointRaDec,
      };
    });
    console.log('[poll ' + i + '] ' + JSON.stringify(d));
    if (d.stelReady && d.zoomBy === 'function' && d.pointRaDec === 'function') { ready = true; break; }
    await sleep(3000);
  }
  const res = { ready };
  if (ready) {
    res.latRad = await page.evaluate(() => window.stel.core.observer.latitude);
    // The FOV lives on stel.core.fov (radians); a bare stel.fov is undefined.
    res.fov0 = await page.evaluate(() => window.stel.core.fov);
    await page.evaluate(() => window.zoomBy(0.5));
    await sleep(1200);
    res.fov1 = await page.evaluate(() => window.stel.core.fov);
    res.yaw0 = await page.evaluate(() => window.stel.core.observer.yaw);
    // Point at Vega, then far from it: the view must turn.
    await page.evaluate(() => window.pointRaDec(279.23, 38.78));
    await sleep(600);
    await page.evaluate(() => window.pointRaDec(83.82, -5.39));
    await sleep(600);
    res.yaw1 = await page.evaluate(() => window.stel.core.observer.yaw);
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
