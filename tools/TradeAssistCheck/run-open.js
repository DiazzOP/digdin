// Checks the "open" mode of TradeAssist.js on mock-trades.html: switch to "Recebidas", open the newest trade from Beta
// by its name (never Aceitar/Recusar, never the chat link); with mode=link, return the direct link to the trade.
const { chromium } = require('playwright'); const fs = require('fs');
const src = fs.readFileSync(process.argv[2], 'utf8');
(async () => { const b = await chromium.launch();
  for (const mode of ['rows', 'link']) {
    const p = await b.newPage(); await p.goto('file://' + process.cwd() + '/mock-trades.html?mode=' + mode);
    await p.evaluate(src.replace('__PLAN__', JSON.stringify({ mode: 'open', tradeId: 987654321, give: [], ask: [], self: ['altconta18', 'sigma2'], partner: ['fonte_beta', 'Beta'] })));
    await p.waitForFunction(() => window.__rbxswAssist && window.__rbxswAssist.state === 'done', null, { timeout: 60000 });
    const r = await p.evaluate(() => ({ opened: window.__rbxswAssist.opened, navigate: window.__rbxswAssist.navigate, clicks: window.clicks,
      detail: document.getElementById('detail').textContent, accepted: window.accepted, declined: window.declined, url: location.href }));
    console.log(mode, JSON.stringify(r)); await p.close(); }
  await b.close(); })();
