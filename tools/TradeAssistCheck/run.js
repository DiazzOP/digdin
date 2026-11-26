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
    // The real page of 26/11 (E062, built from trade-pagina.txt): late-answering cards, pictures that load by themselves,
    // held copies, the Fedora on page 2, and the request already holding the Small (pre=1: "Abrir de novo").
    { file: 'mock-real3.html', mode: 'real3', query: '&pre=1', want: { offer: ['Green Ultimate Dragon Face','Snow Leopard Fedora','Souvenir Egg'], request: ['Supersonic Egg'] },
      plan: { give: [{name:'Green Ultimate Dragon Face',serial:null},{name:'Snow Leopard Fedora',serial:null},{name:'Souvenir Egg',serial:null}], ask: [{name:'Supersonic Egg',serial:null}], self:['fonte_beta','Beta'], partner:['altconta73','sigma6'] } },
    { file: 'mock-real3.html', mode: 'real3', want: { offer: ['Green Ultimate Dragon Face','Snow Leopard Fedora','Souvenir Egg'], request: ['Supersonic Egg'] },
      plan: { give: [{name:'Green Ultimate Dragon Face',serial:null},{name:'Snow Leopard Fedora',serial:null},{name:'Souvenir Egg',serial:null}], ask: [{name:'Supersonic Egg',serial:null}], self:['fonte_beta','Beta'], partner:['altconta73','sigma6'] } },
    // "Abrir de novo" with a Limited already in the offer: it must not be clicked (that would take it out).
    { file: 'mock-real3.html', mode: 'real3', query: '&pre=2', want: { offer: ['Green Ultimate Dragon Face','Souvenir Egg'], request: ['Supersonic Egg'] },
      plan: { give: [{name:'Green Ultimate Dragon Face',serial:null},{name:'Souvenir Egg',serial:null}], ask: [{name:'Supersonic Egg',serial:null}], self:['fonte_beta','Beta'], partner:['altconta73','sigma6'] } },
    // Only held copies of an item ("Em retenção"): not clicked, reported as held.
    { file: 'mock-real3.html', mode: 'real3', want: { offer: ['Inkwell Egg'], request: [] },
      plan: { give: [{name:'Quadri-Skull Aura',serial:null},{name:'Inkwell Egg',serial:null}], ask: [], self:['fonte_beta','Beta'], partner:['altconta73','sigma6'] } },
    // A page that selects on mousedown only.
    { mode: 'mousedown', plan: { give: [{name:'Sword',serial:3}], ask: [], self:'fonte_beta', partner:'altconta18' } },
  ];
  for (const c of cases) {
    const page = await browser.newPage();
    await page.goto('file://' + process.cwd() + '/' + (c.file || 'mock.html') + '?mode=' + c.mode + (c.query || ''));
    await page.evaluate(src.replace('__PLAN__', JSON.stringify(c.plan)));
    await page.waitForFunction(() => window.__rbxswAssist && window.__rbxswAssist.state === 'done', null, { timeout: 260000 });
    const r = await page.evaluate(() => ({ ...window.__rbxswAssist, sent: !!window.sent, clicks: window.clicks, url: location.href,
      offer: [...document.querySelectorAll('#offer .item-card-name, #offer .name, #offer .item-name a')].map(e=>e.textContent + (e.nextSibling ? e.nextSibling.textContent : '')),
      request: [...document.querySelectorAll('#request .item-card-name, #request .name, #request .item-name a')].map(e=>e.textContent + (e.nextSibling ? e.nextSibling.textContent : '')) }));
    const { structure, ...rest } = r;
    console.log(c.mode, JSON.stringify(rest));
    if (!c.want && r.selected.length !== r.offer.length + r.request.length) console.log('  FALHA · relatou ' + r.selected.length + ' selecionados, a trade tem ' + (r.offer.length + r.request.length));
    if (c.want) {
      const clean = a => a.map(x => x.replace(/⏣.*$/, '').trim()).sort().join(', ');
      const okOffer = clean(r.offer) === [...c.want.offer].sort().join(', '), okReq = clean(r.request) === [...c.want.request].sort().join(', ');
      // What the script says it selected must be what is really there.
      const honest = r.selected.length === r.offer.length + r.request.length;
      console.log('  ' + (okOffer && okReq && honest && !r.sent ? 'OK' : 'FALHA') + ' · oferta: ' + clean(r.offer) + ' · pedido: ' + clean(r.request) + ' · relatou ' + r.selected.length + ' selecionados');
    }
    if (structure) console.log('  structure lines:', structure.split('\n').length, '| sample:', structure.split('\n').filter(l => l.includes('item do plano')).slice(0,2).join(' || '));
    await page.close();
  }
  await browser.close();
})();
