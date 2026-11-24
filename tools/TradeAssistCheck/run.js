const { chromium } = require('playwright');
const fs = require('fs');
const src = fs.readFileSync(process.argv[2], 'utf8');
(async () => {
  const browser = await chromium.launch();
  const cases = [
    { mode: 'tabs', plan: { give: [{name:'Black Headphones',serial:12},{name:'Pink Eye',serial:3},{name:'Eggcient Woolly Mammoth',serial:44}], ask: [{name:'Deteggctive W. Wolf',serial:7}], self:'fonte_beta', partner:'altconta18' } },
    // Same item on both sides (Epic Egg #2 mine, #7 theirs), both inventories visible; plus one missing item and a serial that is not there.
    { mode: 'both', plan: { give: [{name:'Valkyrie Helm',serial:9},{name:'Epic Egg',serial:2},{name:'Ghost Item',serial:1}], ask: [{name:'Epic Egg',serial:7}], self:'fonte_beta', partner:'altconta18' } },
    { mode: 'both', plan: { give: [{name:'Sword',serial:99}], ask: [], self:'fonte_beta', partner:'altconta18' } },
    // Two copies without serials, an item whose name has a "never" word, partner tab behind a header profile link.
    { mode: 'tabs', plan: { give: [{name:'Twin Cap',serial:null},{name:'Twin Cap',serial:null},{name:'Counterfeit Hat',serial:8}], ask: [{name:'Rock',serial:1}], self:'fonte_beta', partner:'altconta18' } },
    // The real page of 19/11 (screenshot): the step E002 — Black Headphones + Eggcient Woolly Mammoth, ask Deteggctive W. Wolf;
    // the partner's inventory is titled with the display name. Then an item on page 2 (Old Hat).
    { file: 'mock-real.html', mode: 'real', plan: { give: [{name:'Black Headphones',serial:null},{name:'Eggcient Woolly Mammoth',serial:44}], ask: [{name:'Deteggctive W. Wolf',serial:null}], self:['fonte_beta','Beta'], partner:['altconta18','sigma2'] } },
    { file: 'mock-real.html', mode: 'real', plan: { give: [{name:'Old Hat',serial:null},{name:'Deteggctive W. Wolf',serial:null}], ask: [{name:'Verdant Crown',serial:1117834}], self:['fonte_beta','Beta'], partner:['altconta18','sigma2'] } },
    // A tab opened just now: the partner's inventory appears seconds later.
    { file: 'mock-real.html', mode: 'slow', plan: { give: [{name:'Black Headphones',serial:null},{name:'Eggcient Woolly Mammoth',serial:44}], ask: [{name:'Deteggctive W. Wolf',serial:null}], self:['fonte_beta','Beta'], partner:['altconta18','sigma2'] } },
    // The real page of 24/11: a ✓ overlay only, the offer appears 3 s later without the serial, a second click removes.
    { file: 'mock-real.html', mode: 'toggle', plan: { give: [{name:'Black Headphones',serial:null},{name:'Camo Commando',serial:2710},{name:'Old Hat',serial:null}], ask: [{name:'Bighead',serial:null}], self:['fonte_beta','Beta'], partner:['altconta18','sigma2'] } },
    // The real page of 24/11: page arrows are bare icons; the items are on page 2.
    { file: 'mock-real.html', mode: 'bare', plan: { give: [{name:'Old Hat',serial:null},{name:'Camo Commando',serial:2710},{name:'Black Headphones',serial:null}], ask: [], self:['fonte_beta','Beta'], partner:['altconta18','sigma2'] } },
    // The real page of 24/11 (trade-pagina.txt): "Página 2" shows at once, its cards 1.5 s later.
    { file: 'mock-real.html', mode: 'late', plan: { give: [{name:'Old Hat',serial:null},{name:'Camo Commando',serial:2710}], ask: [], self:['fonte_beta','Beta'], partner:['altconta18','sigma2'] } },
    // The same, with the inventory search (magnifier): it finds the item without paging.
    { file: 'mock-real.html', mode: 'search', plan: { give: [{name:'Camo Commando',serial:2710},{name:'Black Headphones',serial:null}], ask: [{name:'Bighead',serial:null}], self:['fonte_beta','Beta'], partner:['altconta18','sigma2'] } },
    // Page arrows that answer pointer events only.
    { file: 'mock-real.html', mode: 'pointer', plan: { give: [{name:'Old Hat',serial:null}], ask: [], self:['fonte_beta','Beta'], partner:['altconta18','sigma2'] } },
    // A page that selects on mousedown only.
    { mode: 'mousedown', plan: { give: [{name:'Sword',serial:3}], ask: [], self:'fonte_beta', partner:'altconta18' } },
  ];
  for (const c of cases) {
    const page = await browser.newPage();
    await page.goto('file://' + process.cwd() + '/' + (c.file || 'mock.html') + '?mode=' + c.mode);
    await page.evaluate(src.replace('__PLAN__', JSON.stringify(c.plan)));
    await page.waitForFunction(() => window.__rbxswAssist && window.__rbxswAssist.state === 'done', null, { timeout: 60000 });
    const r = await page.evaluate(() => ({ ...window.__rbxswAssist, sent: !!window.sent, clicks: window.clicks, url: location.href,
      offer: [...document.querySelectorAll('#offer .item-card-name, #offer .name')].map(e=>e.textContent + (e.nextSibling ? e.nextSibling.textContent : '')),
      request: [...document.querySelectorAll('#request .item-card-name, #request .name')].map(e=>e.textContent + (e.nextSibling ? e.nextSibling.textContent : '')) }));
    const { structure, ...rest } = r;
    console.log(c.mode, JSON.stringify(rest));
    if (structure) console.log('  structure lines:', structure.split('\n').length, '| sample:', structure.split('\n').filter(l => l.includes('item do plano')).slice(0,2).join(' || '));
    await page.close();
  }
  await browser.close();
})();
