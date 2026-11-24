// RbxSwitcher — pre-selects a planned trade's items on roblox.com's trade page, in the source account's own tab.
// It only clicks item cards (and inventory tabs / page arrows to find them). It never clicks anything that sends,
// accepts, counters or declines a trade: the person reviews and sends. Reads no cookies or storage.
// Progress and result go to window.__rbxswAssist, read back by the app.
(async () => {
  // select: { give: [{name, serial}], ask: [{name, serial}], self: ["username", "display name"], partner: [...] }
  // open:   { mode: "open", tradeId: 123, give: [], ask: [], self: [destination names], partner: [source names] }
  const plan = __PLAN__;
  const selfNames = [].concat(plan.self || []).filter(Boolean), partnerNames = [].concat(plan.partner || []).filter(Boolean);
  const R = window.__rbxswAssist = { state: 'running', selected: [], missing: [], ambiguous: [], unconfirmed: [], notes: [], structure: null };
  // Controls the script must never press (checked on every tab/arrow it clicks, and on a card's own buttons).
  const NEVER = /(enviar|send|make offer|fazer oferta|fazer proposta|propor|confirm|aceitar|accept|counter|contrapropo|decline|recusar|cancel)/i;
  const DEADLINE = Date.now() + 150000; // the app waits a little longer than this
  const late = () => Date.now() > DEADLINE;
  const esc = t => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const label = it => it.name + (it.serial != null ? ' #' + it.serial : '');
  const vis = el => {
    if (!el || !el.getClientRects().length) return false;
    const s = getComputedStyle(el);
    return s.visibility !== 'hidden' && s.display !== 'none';
  };
  const disabled = el => el.disabled || el.getAttribute('aria-disabled') === 'true' || /\bdisabled\b/i.test(el.className || '');
  const safeText = el => norm(el.textContent) + ' ' + norm(el.getAttribute('aria-label')) + ' ' + norm(el.getAttribute('title'));

  const realLink = a => a && !/^(#|javascript:|$)/i.test((a.getAttribute('href') || '').trim());

  // Every click: links never navigate (the trade page must stay open); the page's own handlers still run.
  // `full` adds the pointer/mouse sequence, used only when a plain click changed nothing.
  // `allowNav`: only when opening a trade from the trades list, where the row may be a link to that trade.
  function press(el, full, allowNav) {
    const stopNav = e => { if (allowNav) return; const a = e.target && e.target.closest ? e.target.closest('a[href]') : null; if (realLink(a)) e.preventDefault(); };
    window.addEventListener('click', stopNav, true);
    try {
      el.scrollIntoView({ block: 'center' });
      if (full) for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup'])
        el.dispatchEvent(new (type.startsWith('pointer') && window.PointerEvent ? PointerEvent : MouseEvent)(type, { bubbles: true, cancelable: true, view: window }));
      if (typeof el.click === 'function') el.click(); // an <svg> icon has no click(): send the event
      else el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
    } finally { window.removeEventListener('click', stopNav, true); }
  }

  // Tabs and page arrows: never a send/accept/decline control. `full`: the pointer/mouse sequence too (the page's
  // newer buttons answer pointer events, not a plain click).
  function clickControl(el, full) {
    if (!el || NEVER.test(safeText(el))) return false;
    press(el, !!full);
    return true;
  }

  // The names of the cards on screen (to see a new page arrive, not only its label).
  const cardNames = () => [...document.querySelectorAll('[title], [class*="item-card-name" i], [class*="name" i]')]
    .filter(el => vis(el) && !el.children.length).map(el => norm(el.getAttribute('title') || el.textContent)).join('|');

  // Waits for `pred` up to `ms`.
  async function until(pred, ms) {
    for (const end = Date.now() + ms; Date.now() < end; ) { if (pred()) return true; await sleep(200); }
    return pred();
  }

  // The inventory's own search (magnifier, "Pesquisar"): type the item's name and let the page filter every page.
  const setInput = (input, value) => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  };
  const searched = new Set();
  function searchInput(want) {
    return [...document.querySelectorAll('input')].find(i => vis(i) && ['text', 'search', ''].includes((i.getAttribute('type') || '').toLowerCase()) && zoneOf(i) === want);
  }
  async function viaSearch(it, side, taken) {
    const want = side === 'give' ? 'mine' : 'partner';
    let input = searchInput(want);
    if (!input) {
      const btn = [...document.querySelectorAll('button, [role="button"]')]
        .find(b => vis(b) && /(pesquisar|search|buscar|procurar)/i.test(safeText(b)) && zoneOf(b) === want);
      if (!btn || !clickControl(btn)) return null;
      if (!await until(() => searchInput(want), 2000)) { clickControl(btn, true); await until(() => searchInput(want), 2000); }
      input = searchInput(want);
    }
    if (!input) return null;
    searched.add(input);
    input.focus();
    setInput(input, it.name);
    // The filtered list comes from the server: wait for the card (or for the list to settle).
    let r = null;
    await until(() => { r = pick(it, side, taken); return !!(r.card || r.count > 1); }, 6000);
    if (!(r && (r.card || r.count > 1))) { await settle(3000); r = pick(it, side, taken); }
    return r;
  }

  // A card is clicked on its picture, so the click bubbles up to whatever handler the page put on the card. The guard
  // looks at the card's own buttons, not at the item's name (an item may be called "Counterfeit Hat").
  function clickCard(card, full) {
    if ([...card.querySelectorAll('button, [role="button"]')].some(b => NEVER.test(safeText(b)))) return false;
    press(card.querySelector('img, canvas, [class*="thumb" i]') || card, full);
    return true;
  }

  // Elements showing exactly the item's name (text, title or alt), innermost only.
  function nameNodes(name) {
    const want = norm(name), out = [];
    for (const el of document.body.querySelectorAll('*')) {
      if (el.children.length > 3 || !vis(el)) continue;
      if (norm(el.textContent) === want || norm(el.getAttribute('title')) === want || norm(el.getAttribute('alt')) === want) out.push(el);
    }
    return out.filter(e => !out.some(o => o !== e && e.contains(o)));
  }

  // The card around a name: the nearest small ancestor that also holds a picture.
  function cardOf(el) {
    for (let n = el, i = 0; n && n !== document.body && i < 8; n = n.parentElement, i++) {
      const r = n.getBoundingClientRect();
      if (r.width > 460 || r.height > 560) break;
      if (n.querySelector('img, canvas, [class*="thumb" i]') || /thumb/i.test(n.className || '')) return n;
    }
    return null;
  }

  function cardsFor(it) {
    const cards = [];
    for (const n of nameNodes(it.name)) { const c = cardOf(n); if (c && !cards.includes(c)) cards.push(c); }
    if (it.serial == null) return cards;
    const mine = new RegExp('#\\s*' + it.serial + '(?!\\d)');
    const exact = cards.filter(c => mine.test(c.textContent));
    // Cards that show another serial are other copies; cards with no serial at all may still be this one.
    return exact.length ? exact : cards.filter(c => !/#\s*\d+/.test(c.textContent));
  }

  // Which part of the page a card is in, from the section headings around it: "Seu inventário" (mine),
  // "Inventário de sigma2" / the partner's names (partner), "Sua oferta" / "Seu pedido" (offer: never clicked).
  const ZONES = {
    offer: /(sua oferta|seu pedido|your offer|your request|tu oferta|tu solicitud|você oferece|você recebe|you give|you receive)/i,
    mine: /(seu invent[aá]rio|meu invent[aá]rio|your inventory|my inventory|tu inventario)/i,
    partner: new RegExp('(invent[aá]rio de|inventory of|\'s inventory|inventario de' +
                        partnerNames.map(n => '|' + esc(n)).join('') + ')', 'i'),
  };
  function zoneOf(el) {
    for (let n = el.parentElement; n && n !== document.body; n = n.parentElement) {
      const t = n.innerText || '';
      const hit = Object.keys(ZONES).filter(z => ZONES[z].test(t));
      if (hit.length === 1) return hit[0];
      if (hit.length > 1) return 'unknown'; // a container holding several sections
    }
    return 'unknown';
  }
  // Without headings: how close a card is to a place that names the partner (smaller = their side).
  function partnerDistance(card) {
    for (let n = card, d = 0; n; n = n.parentElement, d++)
      if (partnerNames.some(p => norm(n.textContent).includes(norm(p)))) return d;
    return 999;
  }

  // The card to click for an item on one side. Copies that cannot be told apart (same item, no serial shown) are
  // interchangeable: the first one goes. Ambiguous only when the side itself cannot be told.
  function pick(it, side, taken) {
    const want = side === 'give' ? 'mine' : 'partner';
    const cards = cardsFor(it).filter(c => !taken.has(c)).map(c => ({ c, z: zoneOf(c) })).filter(x => x.z !== 'offer');
    const usable = x => !disabled(x.c) && !/(\bselected\b|\bchecked\b)/i.test(x.c.className || '') && x.c.getAttribute('aria-checked') !== 'true';
    const onSide = cards.filter(x => x.z === want);
    if (onSide.length) { const first = onSide.find(usable) || onSide[0]; return { card: first.c, count: 1 }; }
    const unknown = cards.filter(x => x.z === 'unknown');
    if (unknown.length <= 1) return { card: unknown[0] ? unknown[0].c : null, count: unknown.length };
    const d = unknown.map(x => partnerDistance(x.c));
    if (d.every(v => v === d[0])) return { card: null, count: unknown.length }; // nothing tells the sides apart
    const best = side === 'ask' ? Math.min(...d) : Math.max(...d);
    const on = unknown.filter((_, i) => d[i] === best);
    return { card: (on.find(usable) || on[0]).c, count: 1 };
  }

  // Inventory tabs ("Seu inventário", "@partner") and page arrows, to bring a card into view.
  function tabFor(side) {
    const want = side === 'ask' ? ZONES.partner : /(seu|your|meu|my)\s+(invent|itens|items)/i;
    // Not a real link (the partner's name in the header links to their profile).
    return [...document.querySelectorAll('button, [role="tab"], li, [role="button"], a')]
      .find(el => vis(el) && !disabled(el) && !realLink(el.closest('a[href]')) && el.textContent.trim().length <= 40
                  && want.test(el.textContent) && !NEVER.test(safeText(el)));
  }
  // Page arrows of one side's inventory (when both inventories are on screen, the partner's sits nearer their name).
  const ARROWS = {
    next: '[aria-label*="next" i], [aria-label*="próxim" i], [title*="next" i], [title*="próxim" i], [class*="next" i], ' +
          '[class*="pager" i] [class*="right" i], [class*="chevron-right" i], [class*="arrow-right" i]',
    prev: '[aria-label*="prev" i], [aria-label*="anterior" i], [title*="prev" i], [title*="anterior" i], [class*="prev" i], ' +
          '[class*="pager" i] [class*="left" i], [class*="chevron-left" i], [class*="arrow-left" i]',
  };
  // The page arrows next to a "Página 2" / "Page 2" label (the real page draws them as bare icons: no class, label or
  // text that says next/previous). The side is the pager's section ("Seu inventário" / "Inventário de …").
  const PAGE_LABEL = /^\s*(p[aá]gina|page)\s*\d+(\s*(de|of|\/)\s*\d+)?\s*$/i;
  function pagerArrows(dir, side) {
    const want = side === 'give' ? 'mine' : 'partner', found = [];
    for (const lab of document.body.querySelectorAll('*')) {
      if (lab.children.length || !vis(lab) || !PAGE_LABEL.test(lab.textContent || '')) continue;
      for (let box = lab.parentElement, i = 0; box && box !== document.body && i < 3; box = box.parentElement, i++) {
        const clickable = [...box.querySelectorAll('button, [role="button"], a, svg')]
          .map(el => el.closest('button, [role="button"], a') || el)
          .filter((el, k, list) => list.indexOf(el) === k && !el.contains(lab) && vis(el));
        const before = clickable.filter(el => lab.compareDocumentPosition(el) & Node.DOCUMENT_POSITION_PRECEDING);
        const after = clickable.filter(el => lab.compareDocumentPosition(el) & Node.DOCUMENT_POSITION_FOLLOWING);
        if (!before.length && !after.length) continue;
        const el = dir === 'next' ? after[0] : before[before.length - 1];
        if (el) found.push({ el, z: zoneOf(lab) });
        break;
      }
    }
    const usable = found.filter(f => !disabled(f.el) && !realLink(f.el.closest && f.el.closest('a[href]')) && !NEVER.test(safeText(f.el)));
    const zoned = usable.filter(f => f.z === want);
    if (zoned.length) return zoned.map(f => f.el);
    if (usable.length <= 1) return usable.map(f => f.el);
    const d = usable.map(f => partnerDistance(f.el)), best = side === 'ask' ? Math.min(...d) : Math.max(...d);
    return usable.filter((_, i) => d[i] === best).map(f => f.el);
  }

  function arrows(dir, side) {
    const pager = pagerArrows(dir, side);
    if (pager.length) return pager;
    const all = [...document.querySelectorAll(ARROWS[dir])]
      .map(el => el.closest('button, a, [role="button"], li') || el)
      .filter((el, i, list) => list.indexOf(el) === i && vis(el) && !disabled(el) && !realLink(el.closest('a[href]')) && !NEVER.test(safeText(el)));
    if (all.length <= 1) return all;
    const want = side === 'give' ? 'mine' : 'partner', zoned = all.filter(el => zoneOf(el) === want);
    if (zoned.length) return zoned;
    const d = all.map(partnerDistance), best = side === 'ask' ? Math.min(...d) : Math.max(...d);
    return all.filter((_, i) => d[i] === best);
  }

  const pageText = () => document.body.innerText;

  // Waits until the page stops changing for 1 s (at most `max` ms): the inventories render in parts, slower in a
  // tab that was just opened.
  async function settle(max) {
    const until = Date.now() + max;
    let last = pageText(), still = 0;
    while (Date.now() < until && still < 1000) {
      await sleep(250);
      const now = pageText();
      still = now === last ? still + 250 : 0;
      last = now;
    }
  }

  async function find(it, side, taken) {
    let r = pick(it, side, taken);
    if (r.card || r.count > 1) return r;
    // Not on screen yet: give a page that is still filling in a moment before changing tabs or pages.
    await settle(6000);
    r = pick(it, side, taken);
    if (r.card || r.count > 1) return r;
    const tab = tabFor(side);
    if (tab && clickControl(tab)) { await sleep(1200); r = pick(it, side, taken); if (r.card || r.count > 1) return r; }
    // The inventory's search first: it looks at every page at once.
    const s = await viaSearch(it, side, taken);
    if (s && (s.card || s.count > 1)) return s;
    // Forward to the last page, then back to the first: every page of that inventory is looked at once.
    for (const dir of ['next', 'prev']) {
      for (let page = 0; page < 1000 && !late(); page++) {
        const b = arrows(dir, side)[0], before = pageText(), cards = cardNames();
        if (!b || !clickControl(b)) break;
        // Wait for the next page (up to 4 s); a plain click that did nothing gets the pointer sequence once.
        let moved = await until(() => pageText() !== before, 4000);
        if (!moved && clickControl(b, true)) moved = await until(() => pageText() !== before, 4000);
        if (!moved) break; // first or last page
        // The label can change before the cards: wait for the cards themselves, then for the page to settle.
        await until(() => cardNames() !== cards, 5000);
        await settle(3000);
        r = pick(it, side, taken);
        if (r.card || r.count > 1) return r;
      }
    }
    return r;
  }

  function shape(t) {
    let s = t || '';
    for (const n of [...selfNames, ...partnerNames]) s = s.replace(new RegExp(esc(n), 'gi'), '@conta');
    return s.replace(/\d/g, '9').replace(/\s+/g, ' ').trim().slice(0, 40);
  }
  // A value-free outline of the page (tags, classes, roles; digits and account names masked), to adjust this script.
  function structure() {
    const lines = [], marks = new Set([...plan.give, ...plan.ask].flatMap(it => nameNodes(it.name)));
    (function walk(el, d) {
      if (lines.length > 3000 || d > 20 || !vis(el) || /^(script|style|svg|path|noscript)$/i.test(el.tagName)) return;
      const cls = (typeof el.className === 'string' ? el.className : '').trim().split(/\s+/).filter(Boolean).slice(0, 4).join('.');
      const attrs = ['role', 'aria-label', 'title', 'type', 'disabled', 'aria-disabled', 'aria-selected']
        .filter(a => el.hasAttribute(a)).map(a => a + '=' + shape(el.getAttribute(a))).join(' ');
      const own = [...el.childNodes].filter(n => n.nodeType === 3).map(n => n.textContent.trim()).join(' ').trim();
      lines.push('  '.repeat(d) + el.tagName.toLowerCase() + (cls ? '.' + cls : '') + (attrs ? ' [' + attrs + ']' : '') +
                 (own ? ' "' + shape(own) + '"' : '') + (marks.has(el) ? '  <== item do plano' : ''));
      for (const c of el.children) walk(c, d + 1);
    })(document.body, 0);
    return 'endereço: ' + location.pathname.replace(/\d+/g, '9') + '\n' + lines.join('\n');
  }

  // ── "open" mode: on the destination's trades page, open the trade that came from the source. Clicks the trade's
  // name or its link only (never a button inside the row: accept/decline/counter stay with the person).
  async function openTrade() {
    for (let t = 0; t < 30 && !partnerNames.some(n => nameNodes(n).length || nameNodes('@' + n).length); t++) await sleep(500);
    await settle(6000);
    // A link straight to this trade, if the page has one.
    const direct = plan.tradeId && [...document.querySelectorAll('a[href]')].find(a => vis(a) && a.getAttribute('href').includes(String(plan.tradeId)));
    if (direct && /(^|\.)roblox\.com$/i.test(new URL(direct.href, location.href).hostname)) { R.navigate = new URL(direct.href, location.href).href; R.opened = true; return; }
    // The "received" list.
    const inbound = [...document.querySelectorAll('button, [role="tab"], li, [role="button"], a')]
      .find(el => vis(el) && !disabled(el) && !realLink(el.closest('a[href]')) && el.textContent.trim().length <= 30
                  && /(recebid|inbound|entrada|received)/i.test(el.textContent) && !NEVER.test(safeText(el)));
    if (inbound) { press(inbound, false, false); await settle(5000); }
    // The newest trade from the source: the first place its name shows (lists are newest first).
    const names = partnerNames.flatMap(n => [...nameNodes(n), ...nameNodes('@' + n)])
      .filter(el => !(el.closest('button, [role="button"]') && NEVER.test(safeText(el.closest('button, [role="button"]')))));
    names.sort((a, b) => (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING) ? -1 : 1);
    const target = names[0];
    if (!target) { R.opened = false; R.structure = structure(); return; }
    R.opened = true;
    R.state = 'done'; // before the click: opening may load another page and end this script
    press(target, false, true);
  }

  if (plan.mode === 'open') {
    try { await openTrade(); }
    catch (e) { R.notes.push('erro no script: ' + (e && e.message ? e.message : e)); try { R.structure = structure(); } catch (_) { } }
    R.state = 'done';
    return;
  }

  try {
    // The page fills in after loading: wait for a planned item, or (after 3 s) for item pictures on the page.
    const all = [...plan.give.map(it => [it, 'give']), ...plan.ask.map(it => [it, 'ask'])];
    const pictures = () => [...document.querySelectorAll('img')].filter(vis).length;
    for (let t = 0; t < 40 && !all.some(([it]) => nameNodes(it.name).length) && !(t >= 6 && pictures() >= 4); t++) await sleep(500);
    await settle(6000);

    // Cards already used: the inventory cards clicked and the copies that then appeared in the offer/request (clicking
    // one of those would take the item back out).
    const taken = new Set();
    for (const [it, side] of all) {
      if (late()) { R.missing.push(label(it)); R.notes.push('tempo esgotado'); continue; }
      const r = await find(it, side, taken);
      if (!r.card) { (r.count > 1 ? R.ambiguous : R.missing).push(label(it)); continue; }
      const card = r.card, beforeCards = cardsFor(it), cls = card.className, html = card.innerHTML;
      // The item in "Sua oferta" / "Seu pedido", by name (the page shows it there without the serial).
      const placed = () => nameNodes(it.name).filter(n => zoneOf(n) === 'offer').length; // both columns are the "offer" zone
      const placedBefore = placed();
      // Selected: the card changed (class, a ✓ drawn on it, disabled, gone), or one more of the item is in the offer.
      const changed = () => placed() > placedBefore || cardsFor(it).length !== beforeCards.length || card.className !== cls
                            || card.innerHTML !== html || !document.contains(card) || disabled(card);
      // Waits up to `ms` for the click to show (the page can be slow): a second click too early would take the item back out.
      const shows = async ms => { for (const until = Date.now() + ms; Date.now() < until; await sleep(150)) if (changed()) return true; return changed(); };
      if (!clickCard(card, false)) { R.blocked = (R.blocked || []).concat(label(it)); R.missing.push(label(it)); continue; }
      let ok = await shows(4000);
      // A second (pointer/mouse) click only when nothing at all changed: on the real page a second click takes the item
      // back out of the offer.
      if (!ok && !changed()) { clickCard(card, true); ok = await shows(4000); }
      taken.add(card);
      for (const c of cardsFor(it)) if (!beforeCards.includes(c)) taken.add(c);
      (ok ? R.selected : R.unconfirmed).push(label(it));
    }
    for (const input of searched) if (document.contains(input) && input.value) setInput(input, '');
    R.notes = [...new Set(R.notes)];
    if (R.missing.length || R.ambiguous.length || R.unconfirmed.length) R.structure = structure();
  } catch (e) {
    R.notes.push('erro no script: ' + (e && e.message ? e.message : e));
    try { R.structure = structure(); } catch (_) { }
  }
  R.state = 'done';
})();
