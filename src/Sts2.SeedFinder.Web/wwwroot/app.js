'use strict';

const $ = s => document.querySelector(s);
const el = (tag, cls, text) => {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text != null) n.textContent = text;
  return n;
};

let catalog = null;
// `bosses` and `events` are free lists of rows rather than one slot per act. Bosses need it
// because Ascension 10 gives the final act two of them, so "exactly this pair" is two rows;
// events because wanting two out of one act is normal.
//   bosses: { act, boss, exclude }
//   events: { act, event, within }
// `cards` is one slot per player rather than a free list: the first fight is a single reward
// per player, so there is nothing to add rows for.
/**
 * Every criterion, at its default. A function rather than a literal because Clear needs a fresh
 * one: handing back the same object would share the arrays with the state being replaced.
 */
function defaultState() {
  return {
    players: 2, characters: ['Ironclad', 'Silent'], relic: '', act1: 'any', ascension: 0,
    require: 'any', ancients: [], bosses: [], events: [], cards: [], shops: [], chests: [],
    extraChests: 0,
  };
}

let state = defaultState();

/** Ascension 10 is the only level that changes generation. */
const DOUBLE_BOSS = 10;
const MAX_ASCENSION = 10;

/** How many bosses an act ends with under the current settings. */
function bossSlots(act) {
  const last = catalog.actContent.length;
  return act === last && state.ascension >= DOUBLE_BOSS ? 2 : 1;
}

// ---- What an act can actually contain, given the Act 1 map choice ---------------------------
//
// Act 1 is the only act with two maps, and they share neither bosses nor most events. Once a
// map is chosen the other one's content is unreachable, so offering it would let a user build
// a search with no answers. These three are the single place that filtering happens.

/** The maps this act could still draw. One entry unless Act 1 is left on "Either". */
function mapsFor(act) {
  const maps = catalog.actContent.find(c => c.act === act).maps;
  return act === 1 && state.act1 !== 'any' ? maps.filter(m => m === state.act1) : maps;
}

function reachable(list, act) {
  const maps = mapsFor(act);
  return list.filter(x => x.maps.some(m => maps.includes(m)));
}

function bossesFor(act) {
  return reachable(catalog.actContent.find(c => c.act === act).bosses, act);
}

function eventsFor(act) {
  return reachable(catalog.actContent.find(c => c.act === act).events, act);
}

/** Drops selections the current map choice has just made unreachable. */
function pruneForMap() {
  for (const b of state.bosses)
    if (b.boss && !bossesFor(b.act).some(x => x.slug === b.boss)) b.boss = '';
  for (const e of state.events)
    if (e.event && !eventsFor(e.act).some(x => x.slug === e.event)) e.event = '';
}

// ---- A themed replacement for the native <select> -------------------------------------------

/** The one open list, if any. Opening another closes it, as a native select would. */
let openList = null;

/**
 * A dropdown we draw ourselves.
 *
 * The native popup is painted by the browser rather than by the page: its highlight bar is the
 * system accent, a bright blue, and no stylesheet reaches it. `color-scheme: dark` fixes the
 * popup's background but not that bar. Owning the list is the only way to make it match the
 * panel it belongs to.
 *
 * The root is a <button> deliberately: every "disable the whole panel" pass in this file
 * already selects `button`, so `disabled` keeps working with no special case.
 *
 * @param items  [{label, value, disabled, icon}] or [{group, items:[...]}] — one level of
 *               grouping. `icon` is optional and is a zero-argument factory returning a node,
 *               so each place it appears gets its own element rather than one being moved
 *               between the closed button and the open list.
 * @param value  the selected value
 * @param onChange called with the new value; the caller re-renders, as everywhere else here
 */
function dropdown(items, value, onChange) {
  // Groups become inert header rows in the same flat list, so keyboard movement is one index.
  const rows = [];
  for (const item of items) {
    if (item.items) {
      rows.push({ group: item.group });
      for (const o of item.items) rows.push(o);
    } else {
      rows.push(item);
    }
  }
  const pickable = i => rows[i] && !rows[i].group && !rows[i].disabled;
  const chosen = rows.find(r => !r.group && r.value === value);

  const root = el('button', 'dd');
  root.type = 'button';
  root.setAttribute('aria-haspopup', 'listbox');
  root.setAttribute('aria-expanded', 'false');
  if (chosen?.icon) root.appendChild(chosen.icon());
  root.append(el('span', 'dd-value', chosen ? chosen.label : ''), el('span', 'dd-caret', '▾'));

  // The bit of the <select> API worth keeping, so callers and tests can read and set without
  // opening the list. Matching loosely means a caller need not care that act values are
  // numbers while most others are strings; the option's own value is what gets passed on.
  Object.defineProperty(root, 'value', { get: () => value });
  root.options = rows.filter(r => !r.group);
  root.pick = v => {
    const hit = root.options.find(o => String(o.value) === String(v));
    if (hit && !hit.disabled && hit.value !== value) onChange(hit.value);
  };

  let list = null;
  let active = rows.findIndex(r => !r.group && r.value === value);

  /**
   * Size and position the list so it always fits on screen.
   *
   * The height has to be computed, not capped by a constant: the page is exactly one viewport
   * tall and never scrolls, so anything the list pushes past the bottom edge is unreachable —
   * a long list like Ascension's eleven entries would simply appear to stop early.
   */
  function place() {
    const a = root.getBoundingClientRect();
    const margin = 8;
    const gap = 4;
    const below = window.innerHeight - a.bottom - gap - margin;
    const above = a.top - gap - margin;

    // Measure what it wants uncapped, so "does it fit" is asked of the real height.
    list.style.maxHeight = 'none';
    const wanted = list.offsetHeight;

    const down = wanted <= below || below >= above;
    list.style.maxHeight = Math.max(100, Math.min(wanted, down ? below : above)) + 'px';

    list.style.left = a.left + 'px';
    list.style.width = a.width + 'px';
    list.style.top = (down ? a.bottom + gap : a.top - gap - list.offsetHeight) + 'px';
  }

  function paint() {
    for (const node of list.children) {
      const i = Number(node.dataset.i);
      node.classList.toggle('is-active', i === active);
    }
    const on = list.querySelector('.is-active');
    if (on) {
      root.setAttribute('aria-activedescendant', on.id);
      on.scrollIntoView({ block: 'nearest' });
    }
  }

  function move(delta) {
    let i = active;
    for (let n = 0; n < rows.length; n++) {
      i = (i + delta + rows.length) % rows.length;
      if (pickable(i)) { active = i; paint(); return; }
    }
  }

  function close() {
    if (!list) return;
    list.remove();
    list = null;
    openList = null;
    root.setAttribute('aria-expanded', 'false');
    root.removeAttribute('aria-activedescendant');
  }

  function open() {
    if (list || root.disabled) return;
    openList?.();
    openList = close;

    list = el('div', 'dd-list');
    list.setAttribute('role', 'listbox');
    rows.forEach((r, i) => {
      const node = el('div', r.group ? 'dd-group' : 'dd-opt', r.group ?? r.label);
      // Prepended after construction so the label stays the node's text content, which the
      // type-ahead below reads.
      if (!r.group && r.icon) node.prepend(r.icon());
      node.dataset.i = i;
      node.id = `dd-${Math.random().toString(36).slice(2, 8)}-${i}`;
      if (!r.group) {
        node.setAttribute('role', 'option');
        node.setAttribute('aria-selected', String(r.value === value));
        if (r.value === value) node.classList.add('is-chosen');
        if (r.disabled) {
          node.classList.add('is-disabled');
          node.setAttribute('aria-disabled', 'true');
        } else {
          // mousedown, not click: the button would otherwise take focus back first and the
          // document-level "click outside" handler would close the list under the cursor.
          node.onmousedown = e => { e.preventDefault(); close(); onChange(r.value); };
          node.onmousemove = () => { if (active !== i) { active = i; paint(); } };
        }
      }
      list.appendChild(node);
    });

    document.body.appendChild(list);
    root.setAttribute('aria-expanded', 'true');
    if (!pickable(active)) { active = -1; move(1); }
    place();
    paint();
  }

  root.onclick = () => (list ? close() : open());

  root.onkeydown = e => {
    if (!list) {
      if (['ArrowDown', 'ArrowUp', 'Enter', ' '].includes(e.key)) { e.preventDefault(); open(); }
      return;
    }
    if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
    else if (e.key === 'Home') { e.preventDefault(); active = -1; move(1); }
    else if (e.key === 'End') { e.preventDefault(); active = rows.length; move(-1); }
    else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      if (pickable(active)) { const v = rows[active].value; close(); onChange(v); }
    } else if (e.key === 'Escape' || e.key === 'Tab') {
      close();
    } else if (e.key.length === 1) {
      // Type-ahead, the one native behaviour worth keeping.
      const from = active + 1;
      for (let n = 0; n < rows.length; n++) {
        const i = (from + n) % rows.length;
        if (pickable(i) && rows[i].label.toLowerCase().startsWith(e.key.toLowerCase())) {
          active = i;
          paint();
          return;
        }
      }
    }
  };

  root.onblur = close;
  return root;
}

// The list is positioned against the viewport, so anything that moves the button invalidates
// it. Closing is what a scroll should do anyway when the panel under it is scrolling away.
//
// Except when the scroll IS the list. This listener is on the capture phase to catch the
// scrolling criteria panel, which also means it sees a wheel over the list's own overflow —
// and closing there makes every entry below the fold unreachable, so a long list looks like
// it simply stops early.
window.addEventListener('scroll', e => {
  if (!e.target?.closest?.('.dd-list')) openList?.();
}, true);
window.addEventListener('resize', () => openList?.());
document.addEventListener('mousedown', e => {
  // `closest` is optional-chained because not every mousedown target is an Element: a press
  // on the document itself hands us one without it, and the throw would leave the list stuck
  // open with no way to dismiss it.
  if (openList && !e.target?.closest?.('.dd, .dd-list')) openList();
});

// ---- A themed replacement for the native number spinner -------------------------------------

/**
 * The browser's own spinner is a fixed widget that ignores the page's colours, so it lands as
 * a white box in the middle of a dark panel. This is the same control drawn by us.
 */
function stepper(value, min, max, onChange) {
  const wrap = el('div', 'stepper');
  const input = el('input');
  input.type = 'number';
  input.min = String(min);
  input.max = String(max);
  input.value = String(value);

  const clamp = n => Math.min(max, Math.max(min, n));

  const commit = n => {
    const v = clamp(Number.isFinite(n) ? n : min);
    input.value = String(v);
    sync();
    onChange(v);
  };

  const step = (label, delta) => {
    // type=button matters: these live inside the criteria <form>, and a bare <button>
    // submits it, which would fire a search on every click.
    const b = el('button', 'step', label);
    b.type = 'button';
    b.tabIndex = -1;
    b.onclick = () => commit(Number(input.value) + delta);
    return b;
  };

  const dec = step('−', -1);
  const inc = step('+', 1);
  function sync() {
    dec.disabled = Number(input.value) <= min;
    inc.disabled = Number(input.value) >= max;
  }

  input.onchange = () => commit(Number(input.value));
  sync();

  wrap.append(dec, input, inc);
  return wrap;
}
let stream = null;

// Whether the start index in the box was typed by the user. A random start is reported in the
// status line but never fed back into the field, so leaving it blank means "random every time".
let startIsUserSet = false;
let lastStart = null;

// ---- Art, with a generated monogram whenever there is none ----------------------------------
//
// Relics and cards are both drawn by everything below, and a card can share a slug with a
// relic, so every lookup is keyed by kind as well. `kind` is also the asset route, so a new
// kind needs nothing here beyond an endpoint that serves it.

const artIndex = new Map();   // "kind:slug" -> true when the server said it can serve art

const key = (kind, slug) => kind + ':' + slug;

/** Deterministic hue per relic so monograms stay distinguishable and stable. */
function hueOf(name) {
  let h = 0;
  for (const c of name) h = (h * 31 + c.charCodeAt(0)) | 0;
  return Math.abs(h) % 360;
}

function iconFor(slug, name, size, kind = 'relic') {
  if (artIndex.get(key(kind, slug))) {
    // Map-node art is a white silhouette the game tints itself, so drawing it as an image gives
    // a flat white blob. Mask with it and paint the colour here instead, one hue per Ancient.
    if (kind === 'ancient') {
      const badge = el('span', 'icon is-ancient-mask');
      badge.style.setProperty('--art', `url("/api/asset/ancient/${slug}")`);
      badge.style.setProperty('--tint', `hsl(${hueOf(name)} 52% 66%)`);
      if (size) { badge.style.width = badge.style.height = size + 'px'; }
      return badge;
    }
    const img = el('img', 'icon' + (kind === 'relic' ? '' : ' is-' + kind));
    // Tint the box while the icon is in flight so the grid never shows empty holes — the
    // first request for each one has to decode a compressed texture server-side.
    img.style.background = `hsl(${hueOf(name)} 26% 30%)`;
    img.src = `/api/asset/${kind}/${slug}`;
    img.alt = '';
    img.loading = 'lazy';
    img.onload = () => { img.style.background = ''; };
    // If art fails at request time, swap in the monogram rather than showing a broken image.
    img.onerror = () => img.replaceWith(monogram(name, size));
    if (size) { img.style.width = img.style.height = size + 'px'; }
    return img;
  }
  return monogram(name, size);
}

/** The "nothing chosen" tile. Neutral grey and a question mark, so it reads as a wildcard
 *  rather than as a relic whose art failed to load. */
function anyIcon(size) {
  const n = el('span', 'icon mono is-any', '?');
  if (size) { n.style.width = n.style.height = size + 'px'; }
  return n;
}

function monogram(name, size) {
  const n = el('span', 'icon mono', name.replace(/[^A-Za-z ]/g, '').split(' ')
    .filter(Boolean).slice(0, 2).map(w => w[0]).join(''));
  n.style.background = `hsl(${hueOf(name)} 32% 46%)`;
  if (size) { n.style.width = n.style.height = size + 'px'; }
  return n;
}

// ---- Descriptions -----------------------------------------------------------------------------

const textIndex = new Map();   // "kind:slug" -> { description, note }

/**
 * Renders the game's own markup into DOM nodes. Descriptions arrive as [gold]…[/gold] spans
 * around keywords and numbers; building elements rather than assigning innerHTML keeps the
 * text inert no matter what a future patch puts in there.
 */
// Event text uses a wider palette than relic text does, plus two animation tags. Anything not
// listed still parses — the tag is consumed and simply adds no class — so an unknown one costs
// styling rather than leaving "[jitter]" sitting in the middle of a sentence. The animations are
// deliberately not reproduced: a tooltip that shakes is harder to read, not more faithful.
const TAGS = new Set([
  'gold', 'blue', 'red', 'purple', 'green', 'shake', 'orange', 'aqua', 'rainbow', 'b',
]);

function renderMarkup(text, into) {
  const stack = [];                       // open tags, innermost last
  const re = /\[(\/?)([a-z]+)\]/g;
  let at = 0, m;

  const emit = s => {
    if (!s) return;
    const cls = stack.filter(t => TAGS.has(t)).map(t => 'mk-' + t).join(' ');
    into.appendChild(cls ? el('span', cls, s) : document.createTextNode(s));
  };

  while ((m = re.exec(text)) !== null) {
    emit(text.slice(at, m.index));
    if (m[1]) {
      // Close the matching tag. Unbalanced markup closes nothing rather than unwinding.
      const i = stack.lastIndexOf(m[2]);
      if (i >= 0) stack.splice(i, 1);
    } else {
      stack.push(m[2]);
    }
    at = re.lastIndex;
  }
  emit(text.slice(at));
}

// ---- Tooltip ----------------------------------------------------------------------------------

let tipFor = null;

function showTip(anchor, name, slug, kind = 'relic') {
  const info = textIndex.get(key(kind, slug));
  if (!info || (!info.description && !info.note)) return;

  const tip = $('#tip');
  tip.replaceChildren();
  tip.appendChild(el('b', null, name));
  if (info.description) {
    const body = el('div', 'tip-body');
    renderMarkup(info.description, body);
    tip.appendChild(body);
  }
  if (info.note) tip.appendChild(el('div', 'tip-note', info.note));

  // While the picker is open it owns the top layer, so the tooltip has to be inside it.
  const host = $('#picker').open ? $('#picker') : document.body;
  if (tip.parentElement !== host) host.appendChild(tip);

  tip.hidden = false;
  tipFor = anchor;

  // Place it above the anchor, or below when there is no room, clamped to the viewport.
  const a = anchor.getBoundingClientRect();
  const t = tip.getBoundingClientRect();
  const margin = 8;
  let top = a.top - t.height - margin;
  if (top < margin) top = a.bottom + margin;
  let left = a.left + a.width / 2 - t.width / 2;
  left = Math.max(margin, Math.min(left, window.innerWidth - t.width - margin));
  tip.style.top = top + 'px';
  tip.style.left = left + 'px';
}

function hideTip(anchor) {
  if (anchor && tipFor !== anchor) return;
  $('#tip').hidden = true;
  tipFor = null;
}

/** Makes a node explain itself on hover, if we have anything to say about it. */
function withTip(node, slug, name, kind = 'relic') {
  if (!textIndex.has(key(kind, slug))) return node;
  node.addEventListener('mouseenter', () => showTip(node, name, slug, kind));
  node.addEventListener('mouseleave', () => hideTip(node));
  return node;
}

// Any scroll invalidates the position, and the picker body scrolls under the cursor.
window.addEventListener('scroll', () => hideTip(), true);

// ---- Relic picker --------------------------------------------------------------------------

let pickerResolve = null;

function openPicker(title, groups, kind = 'relic') {
  $('#pickerTitle').textContent = title;
  $('#pickerSearch').value = '';
  render('');

  // Must be wired before the return, not after it. `render` is a hoisted function declaration
  // so calling it above works either way, but a plain assignment below the return is simply
  // unreachable — which is what silently left the search box inert.
  $('#pickerSearch').oninput = e => render(e.target.value);

  $('#picker').showModal();
  $('#pickerSearch').focus();
  return new Promise(res => { pickerResolve = res; });

  function render(filter) {
    const body = $('#pickerBody');
    body.replaceChildren();
    const f = filter.trim().toLowerCase();

    for (const g of groups) {
      const items = g.items.filter(r => !f || r.name.toLowerCase().includes(f) || r.slug.includes(f));
      if (!items.length) continue;

      body.appendChild(el('div', 'group-title', g.title));
      const grid = el('div', 'grid');
      for (const r of items) {
        const tile = el('button', 'tile');
        tile.type = 'button';
        tile.appendChild(iconFor(r.slug, r.name, 26, kind));
        const label = el('div', 'label');
        label.appendChild(el('b', null, r.name));
        if (g.sub) label.appendChild(el('small', null, g.sub));
        tile.appendChild(label);
        tile.onclick = () => { $('#picker').close(); pickerResolve?.(r); pickerResolve = null; };
        grid.appendChild(withTip(tile, r.slug, r.name, kind));
      }
      body.appendChild(grid);
    }
    if (!body.children.length) body.appendChild(el('div', 'empty', 'Nothing matches that.'));
  }
}

$('#picker').addEventListener('close', () => {
  hideTip();
  // Hand the tooltip back to the page, or it goes with the dialog when that hides.
  document.body.appendChild($('#tip'));
  pickerResolve?.(null);
  pickerResolve = null;
});
$('#pickerClose').onclick = () => $('#picker').close();

// Clicking the backdrop closes too — the dialog element itself fills only the panel, so a
// click whose target is the dialog is a click outside the panel.
$('#picker').addEventListener('click', e => { if (e.target === $('#picker')) $('#picker').close(); });

// ---- Relic fields --------------------------------------------------------------------------

/**
 * Draws the current pick into a `.relic-field` button. Both Neow and every Ancient row use
 * this, so a relic looks and behaves the same wherever it is chosen.
 *
 * The hover listeners are bound once per button and read the pick through `_relic`, because
 * the contents are replaced on every change and re-adding them here would stack up a fresh
 * pair each time.
 */
function fillRelicField(field, relic, placeholder, kind = 'relic') {
  field._relic = relic;
  field._kind = kind;
  field.dataset.slug = relic ? relic.slug : '';

  field.replaceChildren();
  field.appendChild(relic ? iconFor(relic.slug, relic.name, 28, kind) : anyIcon(28));
  field.appendChild(el('span', 'name', relic ? relic.name : placeholder));
  const clear = el('span', 'clear', '×');
  clear.dataset.clear = '1';
  clear.title = 'Clear';
  field.appendChild(clear);

  if (!field._tipBound) {
    field._tipBound = true;
    field.addEventListener('mouseenter', () => {
      if (field._relic) showTip(field, field._relic.name, field._relic.slug, field._kind);
    });
    field.addEventListener('mouseleave', () => hideTip(field));
  }
  return field;
}

function newRelicField() {
  const b = el('button', 'relic-field');
  b.type = 'button';
  return b;
}

// ---- Criteria panel ------------------------------------------------------------------------

function renderCharacters() {
  const box = $('#characters');
  box.replaceChildren();
  for (let i = 0; i < state.players; i++) {
    const row = el('div', 'row');
    row.appendChild(el('label', null, `P${i + 1} plays`));
    row.appendChild(dropdown(
      [
        { label: 'Pick a character…', value: '' },
        ...catalog.characters.map(c => ({
          label: c.name,
          value: c.name,
          icon: () => iconFor(c.slug, c.name, 24, 'character'),
        })),
      ],
      state.characters[i] || '',
      v => { state.characters[i] = v; renderCharacters(); }));
    box.appendChild(row);
  }
  state.characters.length = state.players;
  // Card pools are per character, so this panel has to follow every character change. So does
  // the shop panel: a character contributes one relic to their own bag that nobody else can see.
  renderCards();
  renderShops();
  // Not for the characters — a chest draws from the shared bag, so who is playing changes
  // nothing — but for the player count, which sets how many relics one chest can be asked for.
  renderChests();
  syncActAvailability();
}

function charactersReady() {
  return state.characters.length === state.players && state.characters.every(Boolean);
}

const NEED_CHARACTERS = 'Pick a character for every player to enable these.';

/**
 * Bosses, events and Ancients all come out of the same run generation, which needs one
 * character per player before it can run at all. So they enable and disable together.
 */
function syncActAvailability() {
  const ok = charactersReady();

  $('#addAncient').disabled = !ok;
  $('#addEvent').disabled = !ok;
  $('#addBoss').disabled = !ok;
  for (const box of ['#ancients', '#bosses', '#events'])
    // Dropdowns are <button>s, which is half the reason they are: one selector disables
    // every control in the panel, including them, with no special case.
    for (const s of $(box).querySelectorAll('button, input')) s.disabled = !ok;

  $('#ancientHint').textContent = ok
    ? 'Which Ancient appears depends on player count. What it offers is rolled per player, like Neow.'
    : NEED_CHARACTERS;
  $('#bossHint').textContent = ok
    ? 'Bosses are the same for everybody in the lobby. Act 1\'s two maps have separate bosses, so choosing one also pins the map. At Ascension 10 the final act has two, so two rows on it pin the pair.'
    : NEED_CHARACTERS;
  $('#eventHint').textContent = ok
    ? 'Each act shuffles its whole event pool once and hands them out from the front, so the order is fixed by the seed. How far down it you get is not: a room takes the next event you currently qualify for and have not already seen. Read this as "near the front of the queue", not as a promise.'
    : NEED_CHARACTERS;
}

// ---- First fight card reward -----------------------------------------------------------------
//
// One row per player, because a card pool belongs to a character rather than to the lobby: P1's
// Ironclad and P2's Silent are choosing from entirely different lists. That also makes the
// per-player Rewards stream visible in the shape of the panel, which is the thing that lets a
// co-op search ask for two different cards at once.

/** The card list for whoever is in this slot, or an empty list until they pick a character. */
function cardPoolFor(slot) {
  const c = state.characters[slot];
  return (c && catalog.cardPools.find(p => p.character === c)?.cards) || [];
}

function renderCards() {
  const box = $('#cards');
  hideTip();
  box.replaceChildren();

  state.cards.length = state.players;

  for (let i = 0; i < state.players; i++) {
    const pool = cardPoolFor(i);
    const want = state.cards[i] || (state.cards[i] = { card: '', fight: 1 });
    // Changing character strands whatever was picked for the old one.
    if (want.card && !pool.some(c => c.slug === want.card)) want.card = '';

    const row = el('div', 'row');
    row.appendChild(el('label', null, `P${i + 1} is offered`));

    // A slot with no character has no pool to choose from, and saying so beats an empty
    // control that just refuses to open.
    const field = fillRelicField(
      newRelicField(), pool.find(c => c.slug === want.card) ?? null,
      pool.length ? 'Any card' : 'Pick a character first', 'card');
    field.disabled = pool.length === 0;
    field.onclick = async e => {
      if (e.target.dataset.clear) { want.card = ''; renderCards(); return; }
      const picked = await openPicker(`P${i + 1}'s ${state.characters[i]} is offered…`,
        // Grouped by rarity because that is what decides how likely the search is to land:
        // roughly two thirds of draws are Common, so an Uncommon takes noticeably longer.
        // Rares are listed but disabled on fight 1, which can never roll one — showing them
        // greyed says "not here" where hiding them would just look like a missing card.
        ['Common', 'Uncommon', 'Rare'].map(r => ({
          title: r === 'Rare' && want.fight < 2 ? 'Rare (not on fight 1)' : r,
          items: pool.filter(c => c.rarity === r)
                     .map(c => r === 'Rare' && want.fight < 2 ? { ...c, disabled: true } : c),
        })), 'card');
      if (picked) { want.card = picked.slug; renderCards(); }
    };
    row.appendChild(field);

    // Which fight. Hidden until a card is chosen, like the shop visit and chest tolerance.
    // Our own dropdown rather than a <select>, so the open list is themed like the rest of the
    // panel instead of the browser painting the system accent over it.
    const fight = dropdown(
      [{ label: 'Fight 1', value: 1 }, { label: 'Fight 2', value: 2 }],
      want.fight,
      v => {
        want.fight = v;
        // Dropping back to fight 1 strands a rare, which that fight can never offer.
        const picked = pool.find(c => c.slug === want.card);
        if (picked?.rarity === 'Rare' && want.fight < 2) want.card = '';
        renderCards();
      });
    fight.classList.add('visit');
    fight.title = 'Which fight, counting monster rooms from the start of the run';
    fight.hidden = !want.card;
    row.appendChild(fight);

    box.appendChild(row);
  }

  // Unlike the act panels this needs only the one player's character, so it comes alive a row
  // at a time rather than all at once.
  $('#cardHint').textContent = state.characters.some(Boolean)
    ? 'The first room of a run is always a fight, and each player rolls their own reward for it, '
      + 'so fight 1 needs no assumptions. Fight 2 assumes you walk straight into a second monster '
      + 'room, with no shop, elite, event or rest between. Fight 1 can never offer a Rare; fight 2 '
      + 'can. Taking Arcane Scroll, Hefty Tablet, Massive Scroll, Scroll Boxes or Neow’s Bones '
      + 'draws from the same stream first and shifts both.'
    : 'Pick a character to choose from their card pool.';
}

// ---- Shop relics ---------------------------------------------------------------------------
//
// A merchant stocks three relics. Only the third is knowable from the seed: its rarity is
// hardcoded to Shop rather than rolled, and filling it draws no RNG at all, so it is simply the
// back of that player's Shop-rarity relic bag, shuffled once during generation. The other two
// roll against a pity counter that every reward taken so far has moved.
//
// Per player, because each player has their own bag and their own merchant. The visit number is
// a count of shops entered, not a floor, so walking past one shifts the rest along.

/** Shop relics this slot could see: the shared ones plus their own character's. */
function shopPoolFor(slot) {
  const mine = state.characters[slot];
  return catalog.shopRelics.filter(r => !r.character || r.character === mine);
}

function renderShops() {
  const box = $('#shops');
  hideTip();
  box.replaceChildren();

  state.shops.length = state.players;

  for (let i = 0; i < state.players; i++) {
    const pool = shopPoolFor(i);
    const want = state.shops[i] || (state.shops[i] = { relic: '', visit: 1 });
    // A character swap can strand a relic that belonged to the previous one.
    if (want.relic && !pool.some(r => r.slug === want.relic)) want.relic = '';

    const row = el('div', 'row');
    row.appendChild(el('label', null, `P${i + 1} shop`));

    const field = fillRelicField(
      newRelicField(), pool.find(r => r.slug === want.relic) ?? null, 'Any relic', 'relic');
    field.onclick = async e => {
      if (e.target.dataset.clear) { want.relic = ''; renderShops(); return; }
      const own = pool.filter(r => r.character);
      const picked = await openPicker(`P${i + 1}'s shop stocks…`, [
        { title: 'Shared pool', items: pool.filter(r => !r.character) },
        ...(own.length ? [{ title: `${state.characters[i]} only`, items: own }] : []),
      ], 'relic');
      if (picked) { want.relic = picked.slug; renderShops(); }
    };
    row.appendChild(field);

    // Which shop, counted by visits. Hidden until a relic is chosen, since on its own it
    // constrains nothing.
    // Our own dropdown, not a <select>, for the same reason as the fight picker above.
    // It re-renders on change because the closed button's label is built once, so nothing else
    // would update it.
    const visit = dropdown(
      [1, 2, 3, 4, 5].map(n => ({
        label: n === 1 ? '1st shop' : n === 2 ? '2nd shop' : n === 3 ? '3rd shop' : `${n}th shop`,
        value: n,
      })),
      want.visit,
      v => { want.visit = v; renderShops(); });
    visit.classList.add('visit');
    visit.title = 'Which shop this player walks into, counting from the first';
    visit.hidden = !want.relic;
    row.appendChild(visit);

    box.appendChild(row);
  }

  $('#shopHint').textContent =
    'Each shop takes one relic off the back of your bag, so this is a fixed order rather than a '
    + 'floor. Counting is by shops actually entered, and walking past one moves everything after '
    + 'it up. The other two relic slots roll against a counter your own run has already moved, '
    + 'so no seed decides them.';
}

// ---- Treasure chests -------------------------------------------------------------------------
//
// Run-level, so there is no player column. Every act has exactly one chest, on a map row that
// cannot be rerouted around, and it puts one relic per player on the table for the whole party
// to vote on — so the seed decides what is offered, not who walks away with it.
//
// The rarity roll is exact. The relic is the front of the SHARED bag, which every relic anyone
// picks up earlier in the run removes an entry from, hence the "allow n taken" stepper.

/** How many relics one act's chest can be asked for: one per player. */
function chestRowsFor(act) {
  return state.chests.filter(c => c.act === act && c.relic).length;
}

function renderChests() {
  const box = $('#chests');
  hideTip();
  box.replaceChildren();

  state.chests.forEach((crit, idx) => {
    const row = el('div', 'ancient-row');

    const where = dropdown(
      catalog.actContent.map(c => ({ label: `Act ${c.act}`, value: c.act })),
      crit.act,
      v => { crit.act = v; renderChests(); });

    const rm = el('button', 'icon-btn', '×');
    rm.type = 'button';
    rm.title = 'Remove';
    rm.onclick = () => { state.chests.splice(idx, 1); renderChests(); };

    row.append(where, rm);

    // Grouped by rarity, which is the thing the seed actually rolls — picking a Rare is a
    // different proposition from picking a Common, and the groups make the odds legible.
    const chosen = catalog.chestRelics.find(r => r.slug === crit.relic) ?? null;
    const field = fillRelicField(newRelicField(), chosen, 'Any relic', 'relic');
    field.classList.add('span');
    field.onclick = async e => {
      if (e.target.dataset.clear) { crit.relic = ''; renderChests(); return; }
      const byRarity = ['Common', 'Uncommon', 'Rare'].map(rarity => ({
        title: rarity,
        items: catalog.chestRelics.filter(r => r.rarity === rarity),
      }));
      const picked = await openPicker(`Act ${crit.act}'s chest holds…`, byRarity, 'relic');
      if (picked) { crit.relic = picked.slug; renderChests(); }
    };
    row.appendChild(field);

    // How much drain to forgive. Hidden until a relic is chosen, since alone it constrains
    // nothing — same rule the shop visit selector follows.
    if (crit.relic) {
      const wrap = el('div', 'row span within');
      wrap.appendChild(el('label', null, 'Allow taken earlier'));
      wrap.appendChild(stepper(crit.tolerance || 0, 0, 10, v => { crit.tolerance = v; }));
      row.appendChild(wrap);
    }

    box.appendChild(row);
  });

  // Asking one chest for more relics than it has slots can never match, so say so here rather
  // than letting the server reject the search.
  const over = catalog.actContent
    .map(c => c.act)
    .filter(act => chestRowsFor(act) > state.players);

  const hint = $('#chestHint');
  if (over.length) {
    hint.classList.add('warn');
    hint.textContent =
      `Act ${over.join(' and ')}'s chest holds one relic per player, so a ${state.players}-player `
      + 'lobby cannot have that many named in it. Remove one, or add a player.';
    return;
  }
  hint.classList.remove('warn');
  hint.textContent =
    'One chest per act, at co-op floors 9, 24 and 38, and no route skips it. It puts one relic '
    + 'per player on the table and the whole party votes, so this asks what is in the chest, not '
    + 'who gets it. The rarity is exact; the relic itself assumes nobody has pulled one out of '
    + 'the shared bag yet, since an elite reward, a merchant’s stock or a relic event each '
    + 'remove one. Raise "allow taken earlier" to accept the next relics of that rarity instead.';
}

$('#addChest').onclick = () => {
  state.chests.push({ act: 1, relic: '', tolerance: 0 });
  renderChests();
};

// ---- Bosses --------------------------------------------------------------------------------

/**
 * Does at least one of the act's maps still satisfy these rows? Mirrors
 * SeedSearcher.ValidateActCriteria, so the panel and the server never disagree about what is
 * possible — the panel just says it before you press Search.
 */
function actIsFeasible(act, rows) {
  const all = catalog.actContent.find(c => c.act === act).bosses;
  const slots = bossSlots(act);
  const include = [...new Set(rows.filter(r => r.boss && !r.exclude).map(r => r.boss))];
  const exclude = new Set(rows.filter(r => r.boss && r.exclude).map(r => r.boss));

  if (include.some(b => exclude.has(b))) return false;
  if (include.length > slots) return false;

  return mapsFor(act).some(map => {
    const pool = all.filter(b => b.maps.includes(map)).map(b => b.slug);
    return include.every(b => pool.includes(b))
      && pool.filter(b => !exclude.has(b)).length >= slots;
  });
}

/**
 * Whether this row could be an exclusion at all, i.e. some boss it can still choose would
 * leave the act satisfiable.
 *
 * The candidates have to exclude bosses other rows already hold, or this says yes on the
 * strength of re-excluding one that is already excluded: a duplicate collapses to a single
 * name and looks free, while the row's own dropdown will not offer it.
 */
function canExclude(act, row) {
  const others = state.bosses.filter(r => r !== row && r.act === act);
  const taken = new Set(others.filter(r => r.boss).map(r => r.boss));
  return bossesFor(act)
    .filter(b => !taken.has(b.slug))
    .some(b => actIsFeasible(act, [...others, { act, boss: b.slug, exclude: true }]));
}

/** Why this act's rows can never match, or null. */
function bossProblem(act) {
  const rows = state.bosses.filter(r => r.act === act && r.boss);
  if (rows.length === 0 || actIsFeasible(act, rows)) return null;

  const slots = bossSlots(act);
  const include = [...new Set(rows.filter(r => !r.exclude).map(r => r.boss))];
  const exclude = rows.filter(r => r.exclude).map(r => r.boss);
  const name = slug => catalog.actContent.find(c => c.act === act)
    .bosses.find(b => b.slug === slug)?.name ?? slug;

  const both = include.find(b => exclude.includes(b));
  if (both) return `Act ${act} cannot both contain and not contain ${name(both)}.`;

  if (include.length > slots)
    return slots === 1
      ? `Act ${act} has one boss, so it cannot contain both ${include.map(name).join(' and ')}. Ascension 10 gives the final act two.`
      : `Act ${act} has ${slots} bosses at Ascension 10, so it cannot contain all ${include.length}.`;

  if (exclude.length)
    return `Act ${act} draws ${slots} boss${slots === 1 ? '' : 'es'} from ${mapsFor(act).length > 1 ? 3 : bossesFor(act).length}, so ruling out ${exclude.map(name).join(', ')} leaves too few.`;

  return `No Act ${act} map has all of that.`;
}

function renderBosses() {
  const box = $('#bosses');
  hideTip();
  box.replaceChildren();

  state.bosses.forEach((crit, idx) => {
    const row = el('div', 'ancient-row');

    const where = dropdown(
      catalog.actContent.map(c => ({ label: `Act ${c.act}`, value: c.act })),
      crit.act,
      v => { crit.act = v; crit.boss = ''; renderBosses(); });

    const rm = el('button', 'icon-btn', '×');
    rm.type = 'button';
    rm.title = 'Remove';
    rm.onclick = () => { state.bosses.splice(idx, 1); renderBosses(); };

    row.append(where, rm);

    // One exclusion is all an act can usually afford: ruling out a second would leave fewer
    // bosses than it draws. Blocked here rather than allowed and then rejected on Search.
    const mode = dropdown(
      [
        { label: 'Contains boss', value: '' },
        {
          label: "Doesn't contain boss",
          value: '!',
          disabled: !crit.exclude && !canExclude(crit.act, crit),
        },
      ],
      crit.exclude ? '!' : '',
      v => { crit.exclude = v === '!'; renderBosses(); });
    mode.classList.add('span');
    row.appendChild(mode);

    // Only bosses this act can actually produce, which the Act 1 map choice narrows, and
    // never one another row of the same act has already claimed.
    const taken = new Set(state.bosses.filter(r => r !== crit && r.act === crit.act && r.boss)
      .map(r => r.boss));
    const options = bossesFor(crit.act).filter(b => !taken.has(b.slug) || b.slug === crit.boss);
    const maps = mapsFor(crit.act);

    // Grouping by map is what tells the user that an Act 1 boss decides the map too. One map
    // left, whether by choice or because the act only has one, means a flat list.
    const items = [{ label: 'Any boss', value: '' }];
    if (maps.length > 1) {
      for (const map of maps)
        items.push({
          group: map,
          items: options.filter(x => x.maps.includes(map)).map(b => ({ label: b.name, value: b.slug })),
        });
    } else {
      items.push(...options.map(b => ({ label: b.name, value: b.slug })));
    }

    const which = dropdown(items, crit.boss, v => { crit.boss = v; renderBosses(); });
    which.classList.add('span');
    row.appendChild(which);

    box.appendChild(row);
  });

  // A combination no seed can satisfy, said beside the rows rather than leaving Search to be
  // the thing that explains. Mostly unreachable now that the controls block the common cases,
  // but it still catches map mismatches the dropdowns cannot express.
  const problems = catalog.actContent.map(c => bossProblem(c.act)).filter(Boolean);
  const warn = $('#bossWarn');
  warn.textContent = problems.join(' ');
  warn.hidden = problems.length === 0;

  syncActAvailability();
}

// ---- Events --------------------------------------------------------------------------------

function renderEvents() {
  const box = $('#events');
  box.replaceChildren();

  state.events.forEach((crit, idx) => {
    const row = el('div', 'ancient-row');

    const where = dropdown(
      catalog.actContent.map(c => ({ label: `Act ${c.act}`, value: c.act })),
      crit.act,
      v => { crit.act = v; crit.event = ''; renderEvents(); });

    const rm = el('button', 'icon-btn', '×');
    rm.type = 'button';
    rm.title = 'Remove';
    rm.onclick = () => { state.events.splice(idx, 1); renderEvents(); };

    row.append(where, rm);

    // Only events this act can hand out, which the Act 1 map choice narrows. A searchable
    // picker rather than a dropdown: an act's pool runs past twenty entries, which is more
    // than a list is comfortable to scan.
    const maps = mapsFor(crit.act);
    // With both Act 1 maps still in play, say which one a map-specific event needs. Once a map
    // is pinned there is nothing to disambiguate.
    const label = e => maps.length > 1 && e.maps.length === 1 ? `${e.name} (${e.maps[0]})` : e.name;
    const options = eventsFor(crit.act).map(e => ({ ...e, name: label(e) }));

    const which = fillRelicField(
      newRelicField(), options.find(e => e.slug === crit.event) ?? null, 'Any event', 'event');
    which.classList.add('span');
    which.onclick = async ev => {
      if (ev.target.dataset.clear) { crit.event = ''; renderEvents(); return; }
      const picked = await openPicker(
        `Act ${crit.act} hands out…`, [{ title: 'In this act', items: options }], 'event');
      if (picked) { crit.event = picked.slug; renderEvents(); }
    };
    row.appendChild(which);

    if (crit.event) {
      const wrap = el('div', 'row span within');
      wrap.appendChild(el('label', null, 'Within the first'));
      // Capped at the act's pool size: asking for an event within the first 40 of a 25-entry
      // order is just "somewhere", and the game would never look that far anyway. Pinning a
      // map shrinks that pool, so an existing choice can need pulling back down.
      const most = eventsFor(crit.act).length;
      crit.within = Math.min(crit.within, most);
      wrap.appendChild(stepper(crit.within, 1, most, v => { crit.within = v; }));
      row.appendChild(wrap);
    }

    box.appendChild(row);
  });
  syncActAvailability();
}

function renderAncients() {
  const box = $('#ancients');
  // Rows are rebuilt wholesale, so a tooltip anchored to one of them would be orphaned on
  // screen: the node it is following goes away without ever firing mouseleave.
  hideTip();
  box.replaceChildren();

  state.ancients.forEach((crit, idx) => {
    const row = el('div', 'ancient-row');

    const who = dropdown(
      catalog.ancients.map(a => ({
        label: `${a.name} (Act ${a.acts.join(' or ')})`,
        value: a.id,
        icon: () => iconFor(a.id.toLowerCase(), a.name, 20, 'ancient'),
      })),
      crit.ancient,
      v => { crit.ancient = v; crit.relic = ''; renderAncients(); });

    const def = catalog.ancients.find(a => a.id === crit.ancient);

    // Same field and same picker as Neow, so art and descriptions are available here too.
    const what = fillRelicField(
      newRelicField(), def.relics.find(r => r.slug === crit.relic) ?? null, 'Any relic');
    what.classList.add('span');
    what.onclick = async e => {
      if (e.target.dataset.clear) { crit.relic = ''; renderAncients(); return; }
      const picked = await openPicker(`${def.name} offers…`, [
        { title: `Everything ${def.name} can offer`, items: def.relics },
      ]);
      if (picked) { crit.relic = picked.slug; renderAncients(); }
    };

    const rm = el('button', 'icon-btn', '×');
    rm.type = 'button';
    rm.title = 'Remove';
    rm.onclick = () => { state.ancients.splice(idx, 1); renderAncients(); };

    row.append(who, rm, what);

    // Each row gets its own "who must get it" — inheriting Neow's would make
    // "Silken Tress for everyone, but Fiddle for P1" unexpressible.
    if (crit.relic) {
      const choices = [
        { label: 'for any player', value: 'any' },
        { label: 'for every player', value: 'all' },
        ...Array.from({ length: state.players }, (_, i) => ({ label: `for P${i + 1} only`, value: `p${i + 1}` })),
      ];
      // Shrinking the lobby can strand a selection on a player who no longer exists.
      if (!choices.some(o => o.value === crit.require)) crit.require = 'any';

      const req = dropdown(choices, crit.require, v => { crit.require = v; renderAncients(); });
      req.classList.add('span');
      row.appendChild(req);
    }

    const note = def.seedDetermined
      ? 'Fully determined by the seed.'
      : def.deckNote;
    if (note) {
      const n = el('div', 'hint note', note);
      row.appendChild(n);
    }
    box.appendChild(row);
  });
  syncActAvailability();
}

// ---- Results -------------------------------------------------------------------------------

/**
 * The game's own slug rule (StringHelper.Slugify): whitespace becomes an underscore and every
 * other non-alphanumeric is DROPPED, not replaced. The difference is not cosmetic — replacing
 * them turned "Neow's Sacrifice" into neow_s_sacrifice, which matches no art or description, so
 * it silently fell back to a monogram in results while the picker, which uses the slug the
 * server sent, showed the icon correctly. Same for every Pael's and Neow's relic.
 */
function slugOf(name) {
  return name.toLowerCase()
    .replace(/[^a-z0-9\s]+/g, '')
    .trim()
    .replace(/\s+/g, '_');
}

function pill(name, kind) {
  const slug = slugOf(name);
  // Highlight whatever the search was actually asking for, so it is findable at a glance.
  const wanted = slug === state.relic || state.ancients.some(a => a.relic === slug);
  const p = el('span', 'pill' + (kind ? ' is-' + kind : '') + (wanted ? ' is-match' : ''));
  p.appendChild(iconFor(slug, name, 20));
  p.appendChild(el('span', null, name));
  return withTip(p, slug, name);
}

/**
 * "Act 2 · Hive · boss Xolotl · Pael", dropping whatever this seed cannot tell us yet.
 * At Ascension 10 the final act reads "bosses Queen + Aeonglass".
 */
/**
 * The act's header line, with the Ancient's own icon in front of its name.
 *
 * Returns a node rather than a string, which the callers used as text before — the icon has to
 * be an element, and building the line here keeps the two call sites identical.
 */
function actHead(n, act, ancient) {
  const bosses = act?.bosses ?? [];
  const label = bosses.length === 0 ? null
    : bosses.length === 1 ? 'boss ' + bosses[0]
    : 'bosses ' + bosses.join(' + ');

  const head = el('span', 'act-head-line');
  head.appendChild(el('span', null, ['Act ' + n, act?.name, label].filter(Boolean).join(' · ')));
  if (ancient) {
    head.appendChild(el('span', null, ' · '));
    head.appendChild(iconFor(ancient.toLowerCase(), ancient, 18, 'ancient'));
    head.appendChild(el('span', null, ancient));
  }
  return head;
}

const cardNames = new Map();   // slug -> display name, across every character's pool
const relicNames = new Map();  // slug -> display name, for shop relics shown in results

/** The three cards the first fight offers each player, and whether a potion comes with them. */
function firstFightBlock(rewards) {
  if (!rewards?.length) return null;

  const box = el('div', 'sub');
  box.appendChild(el('div', 'sub-head', 'Card rewards'));

  // Ordered by fight, then player, so each fight reads as one block. Fight 2 is labelled
  // because it is the one carrying the hallway assumption.
  for (const r of [...rewards].sort((a, b) => (a.fight || 1) - (b.fight || 1) || a.slot - b.slot)) {
    // Wider label column than the other rows: these name a fight as well as a player.
    const slot = el('div', 'slot is-fight');
    slot.appendChild(el('div', 'slot-label', `P${r.slot + 1} Fight ${r.fight || 1}`));

    const offer = el('div', 'offer');
    for (const slug of r.cards) {
      const name = cardNames.get(slug) ?? slug;
      const hit = state.cards[r.slot]?.card === slug
        && (state.cards[r.slot]?.fight || 1) === (r.fight || 1);
      const p = el('span', 'pill is-card' + (hit ? ' is-match' : ''));
      p.appendChild(iconFor(slug, name, 20, 'card'));
      p.appendChild(el('span', null, name));
      offer.appendChild(withTip(p, slug, name, 'card'));
    }
    // Worth showing because it is the same roll: a potion costs two extra draws and so decides
    // which cards come out at all.
    if (r.potion) offer.appendChild(el('span', 'pill is-potion', '+ potion'));

    slot.appendChild(offer);
    box.appendChild(slot);
  }
  return box;
}

/**
 * The third relic each of a player's shops will stock, in the order they will see them.
 * Collapsed, because it spans the whole run rather than belonging to any one act, and most
 * searches are not about it.
 */
function shopBlock(shops) {
  if (!shops?.length || !shops.some(s => s.relics.length)) return null;

  const d = el('details', 'branches is-order');
  d.appendChild(el('summary', null, 'Shop relics, third slot'));

  for (const s of shops) {
    const slot = el('div', 'slot');
    slot.appendChild(el('div', 'slot-label', `P${s.slot + 1}`));

    const want = state.shops[s.slot];
    const offer = el('div', 'offer');
    s.relics.forEach((slug, i) => {
      const name = relicNames.get(slug) ?? slug;
      const hit = want?.relic === slug && (want.visit || 1) === i + 1;
      const p = el('span', 'pill' + (hit ? ' is-match' : ''));
      p.appendChild(el('span', 'ord', String(i + 1)));
      p.appendChild(iconFor(slug, name, 20, 'relic'));
      p.appendChild(el('span', null, name));
      offer.appendChild(withTip(p, slug, name, 'relic'));
    });

    slot.appendChild(offer);
    d.appendChild(slot);
  }
  return d;
}

/**
 * What an act's treasure chest puts on the table.
 *
 * Not per player, unlike everything above it: the chest rolls one relic per player and the whole
 * party votes on the set, so the seed fixes the contents and the table decides the owners.
 *
 * Each relic carries its rarity, which is the exact part. The alternates behind it are what
 * arrives instead once earlier picks have drained the shared bag, so they are shown folded away
 * rather than as a prediction in their own right.
 */
function chestBlock(chest) {
  if (!chest?.slots?.length) return null;

  const wanted = new Set(state.chests.filter(c => c.act === chest.act && c.relic).map(c => c.relic));

  const d = el('details', 'branches is-order');
  d.appendChild(el('summary', null, `Treasure chest, floor ${chest.floor}`));

  const offer = el('div', 'offer');
  for (const slot of chest.slots) {
    if (!slot.relic) continue;
    const name = relicNames.get(slot.relic) ?? slot.relic;
    const p = el('span', 'pill' + (wanted.has(slot.relic) ? ' is-match' : ''));
    p.appendChild(el('span', 'ord', slot.rarity[0]));
    p.appendChild(iconFor(slot.relic, name, 20, 'relic'));
    p.appendChild(el('span', null, name));
    offer.appendChild(withTip(p, slot.relic, name, 'relic'));
  }
  d.appendChild(offer);

  // The fallbacks, one line per slot. A tolerance the user set makes them part of the answer
  // rather than trivia, so highlight any that their search actually accepted.
  for (const slot of chest.slots) {
    if (!slot.alternates?.length) continue;
    const tol = Math.max(0, ...state.chests
      .filter(c => c.act === chest.act && slot.alternates.includes(c.relic))
      .map(c => c.tolerance || 0));

    const line = el('div', 'offer');
    line.appendChild(el('div', 'slot-label', `then (${slot.rarity})`));
    slot.alternates.forEach((slug, i) => {
      const name = relicNames.get(slug) ?? slug;
      const p = el('span', 'pill' + (wanted.has(slug) && i < tol ? ' is-match' : ''));
      p.appendChild(el('span', 'ord', String(i + 1)));
      p.appendChild(iconFor(slug, name, 20, 'relic'));
      p.appendChild(el('span', null, name));
      line.appendChild(withTip(p, slug, name, 'relic'));
    });
    d.appendChild(line);
  }
  return d;
}

/**
 * The head of an act's event queue. Collapsed by default: it is the order events are handed
 * out, not a list of what you will see, so it is context rather than a headline.
 */
function eventList(act) {
  if (!act?.events?.length) return null;

  const d = el('details', 'branches is-order');
  d.appendChild(el('summary', null, `Event order, first ${act.events.length}`));
  const wanted = new Set(state.events.filter(e => e.act === act.act).map(e => e.event));

  const list = el('div', 'offer');
  act.events.forEach((name, i) => {
    const p = el('span', 'pill is-event' + (wanted.has(slugOf(name)) ? ' is-match' : ''));
    p.appendChild(el('span', 'ord', String(i + 1)));
    p.appendChild(el('span', null, name));
    list.appendChild(p);
  });
  d.appendChild(list);
  return d;
}

function renderHit(hit) {
  const card = el('div', 'hit');

  const head = el('div', 'hit-head');
  head.appendChild(el('span', 'seed', hit.seed));
  const copy = el('button', 'icon-btn copy', 'Copy');
  copy.type = 'button';
  copy.onclick = async () => {
    await navigator.clipboard.writeText(hit.seed);
    copy.textContent = 'Copied';
    setTimeout(() => (copy.textContent = 'Copy'), 1200);
  };
  head.appendChild(copy);
  card.appendChild(head);

  // Act 1 — Neow
  const act1 = el('div', 'act');
  const act1Head = el('div', 'act-head');
  act1Head.appendChild(actHead(1, hit.acts[0], 'Neow'));
  act1.appendChild(act1Head);
  for (const o of hit.neow) {
    const slot = el('div', 'slot');
    slot.appendChild(el('div', 'slot-label', `P${o.slot + 1}`));
    const offer = el('div', 'offer');
    for (const p of o.positives) offer.appendChild(pill(p));
    offer.appendChild(pill(o.curse, 'curse'));
    slot.appendChild(offer);
    act1.appendChild(slot);
  }
  // The first fight sits in Act 1 because that is where it happens, and after Neow because the
  // Neow pick is what can shift it.
  const first = firstFightBlock(hit.firstFight);
  if (first) act1.appendChild(first);

  // Act 1 is built here rather than in the loop below, because its Ancient is Neow and has its
  // own panel — so its chest needs attaching here too, or it would never be rendered at all.
  const act1Chest = chestBlock((hit.chests ?? []).find(c => c.act === 1));
  if (act1Chest) act1.appendChild(act1Chest);

  const act1Events = eventList(hit.acts[0]);
  if (act1Events) act1.appendChild(act1Events);
  card.appendChild(act1);

  // Acts 2 and 3
  for (const ao of hit.ancientOffers) {
    const act = hit.acts[ao.act - 1];
    const box = el('div', 'act');
    const head = el('div', 'act-head');
    head.appendChild(actHead(ao.act, act, ao.ancient));
    box.appendChild(head);

    for (const s of ao.slots) {
      const slot = el('div', 'slot');
      slot.appendChild(el('div', 'slot-label', `P${s.slot + 1}`));

      if (s.branches.length === 1) {
        const offer = el('div', 'offer');
        for (const r of s.branches[0].relics) offer.appendChild(pill(r));
        slot.appendChild(offer);
      } else {
        // More than one branch means the deck decides, not the seed. Never collapse this.
        const d = el('details', 'branches');
        d.appendChild(el('summary', null, `${s.branches.length} possibilities, depending on your deck`));
        for (const b of s.branches) {
          const wrap = el('div', 'branch');
          wrap.appendChild(el('div', 'cond', b.condition));
          const offer = el('div', 'offer');
          for (const r of b.relics) offer.appendChild(pill(r));
          wrap.appendChild(offer);
          d.appendChild(wrap);
        }
        slot.appendChild(d);
      }
      box.appendChild(slot);
    }
    const chest = chestBlock((hit.chests ?? []).find(c => c.act === ao.act));
    if (chest) box.appendChild(chest);
    const events = eventList(act);
    if (events) box.appendChild(events);
    card.appendChild(box);
  }

  // Shops last, and outside the acts: a player's shop order runs across the whole run, and
  // which act a given visit lands in depends on the route they take.
  const shops = shopBlock(hit.shops);
  if (shops) card.appendChild(shops);

  return card;
}

// ---- Search --------------------------------------------------------------------------------

function buildQuery() {
  const q = new URLSearchParams();
  q.set('players', state.players);
  if (charactersReady()) q.set('characters', state.characters.join(','));
  if (state.relic) q.set('relic', state.relic);
  if (state.act1 !== 'any') q.set('act1', state.act1);
  // `where` is deliberately not sent. The API still accepts it, for the CLI and for older
  // links, but a relic's branch is decided by the relic, so the server's default is always
  // the right answer.
  q.set('require', state.require);
  q.set('results', $('#results').value);
  q.set('count', $('#count').value);
  // Only send a start index the user actually typed. Otherwise every search after the first
  // would silently rescan the same range, because the previous run's index is still on screen.
  const start = $('#start').value.trim();
  if (startIsUserSet && /^\d+$/.test(start)) q.set('start', start);

  for (const a of state.ancients) {
    if (!a.relic) { q.append('ancient', a.ancient); continue; }
    q.append('ancient', `${a.ancient}:${a.relic}:${a.require || 'any'}`);
  }

  // A row with nothing chosen yet is a half-filled form, not a wildcard — skip it rather
  // than sending something the server would reject.
  for (const b of state.bosses)
    if (b.boss) q.append('boss', `${b.act}:${b.exclude ? '!' : ''}${b.boss}`);
  for (const e of state.events) if (e.event) q.append('event', `${e.act}:${e.event}:${e.within}`);

  // Slots go out 1-based, matching the P1..P4 labels rather than the internal index.
  state.cards.forEach((want, i) => {
    if (want?.card) q.append('card', `${i + 1}:${want.card}:${want.fight || 1}`);
  });
  state.shops.forEach((want, i) => {
    if (want?.relic) q.append('shop', `${i + 1}:${want.relic}:${want.visit || 1}`);
  });

  // No player index: a chest is a shared pick, so the seed fixes what is on the table and the
  // party decides who takes it.
  for (const c of state.chests)
    if (c.relic) q.append('chest', `${c.act}:${c.relic}:${c.tolerance || 0}`);
  if (state.extraChests > 0) q.set('extraChests', state.extraChests);

  // Only A10 changes anything, but send whatever is set so the answer matches the lobby.
  if (state.ascension > 0) q.set('ascension', state.ascension);

  return q;
}

function setBusy(busy, text) {
  const bar = $('#status');
  bar.replaceChildren();
  if (busy) bar.appendChild(el('div', 'spinner'));
  if (text) bar.appendChild(el('span', null, text));
  $('#go').textContent = busy ? 'Cancel' : 'Search';
  $('#go').classList.toggle('danger', busy);
}

function startSearch() {
  const out = $('#out');
  out.replaceChildren();
  let found = 0;

  const q = buildQuery();
  stream = new EventSource('/api/search?' + q);
  setBusy(true, 'Searching…');

  stream.addEventListener('start', e => {
    const d = JSON.parse(e.data);
    lastStart = d.start;
    // Deliberately not written back into the field — see buildQuery.
    setBusy(true, `Scanning ${Number(d.count).toLocaleString()} seeds from ${Number(d.start).toLocaleString()}…`);
  });

  stream.addEventListener('hit', e => {
    found++;
    out.appendChild(renderHit(JSON.parse(e.data)));
    setBusy(true, `${found} found…`);
  });

  stream.addEventListener('done', e => {
    const d = JSON.parse(e.data);
    stopSearch(`${d.found} seed${d.found === 1 ? '' : 's'} in ${d.seconds.toFixed(2)}s` +
      (d.found === 0 ? ', so try a larger scan or loosen a requirement' : '') +
      (lastStart != null ? ` · scanned from ${Number(lastStart).toLocaleString()}` : ''));
  });

  stream.addEventListener('error', e => {
    let msg = 'Connection lost.';
    try { msg = JSON.parse(e.data).error; } catch { /* transport error, not ours */ }
    out.prepend(el('div', 'err', msg));
    stopSearch('');
  });
}

function stopSearch(text) {
  stream?.close();
  stream = null;
  setBusy(false, text);
}

// ---- Loading the run you are currently playing ----------------------------------------------
//
// One job: put the seed of your in-progress run into the inspect box and show the breakdown.
// Useful after a random run — you are already playing it, and you want to know what is coming.
//
// It also copies the party and ascension, not as a feature but as plumbing: /api/explain needs
// characters in lobby order before it can say anything past Neow, since bosses, Ancients, card
// rewards and shop relics all come out of a generation that depends on who is playing.

async function loadCurrentRun() {
  const btn = $('#fromRun');
  btn.disabled = true;

  let p;
  try {
    p = await (await fetch('/api/profile')).json();
  } catch {
    setBusy(false, 'Could not reach the local server.');
    btn.disabled = false;
    return;
  }
  btn.disabled = false;

  const lobby = p.lobby;
  if (!lobby?.seed) {
    setBusy(false, p.found
      ? 'No run in progress. Start one in game, then try again.'
      : 'No save file found, so there is no run to read.');
    return;
  }

  // Copy the lobby BEFORE the seed: changing the player count rebuilds every per-player
  // control, and doing it afterwards would discard what we just set.
  if (lobby.characters?.length >= 2) {
    const n = Math.min(lobby.characters.length, 4);
    state.players = n;
    for (const x of $('#players').children) x.setAttribute('aria-pressed', String(+x.dataset.n === n));
    state.characters = lobby.characters.slice(0, n);
    state.ascension = lobby.ascension || 0;
    rebuildRequire();
    renderCharacters();
    renderAncients();
    renderAscension();
  }

  $('#inspectSeed').value = lobby.seed;
  await inspect();

  // A solo run generates differently — one more room per act, and singleplayer force-picks
  // undiscovered acts before the roll. So the acts below are the CO-OP reading of that seed and
  // will not match what that player is actually seeing. Said after inspect(), which sets its own
  // status line.
  if (!lobby.isMultiplayer)
    setBusy(false, 'That run is singleplayer. Acts and bosses here are the co-op reading of the '
      + 'seed and will not match it.');
}

$('#fromRun').onclick = loadCurrentRun;

// ---- Wiring --------------------------------------------------------------------------------

$('#players').onclick = e => {
  const b = e.target.closest('button[data-n]');
  if (!b) return;
  state.players = +b.dataset.n;
  for (const x of $('#players').children) x.setAttribute('aria-pressed', String(x === b));
  // Every per-player control has to be rebuilt, not just the character list — the Neow
  // requirement and each Ancient row also enumerate player slots.
  rebuildRequire();
  renderCharacters();
  renderAncients();
};

/** Fills one of the fixed hosts in index.html with a dropdown. */
function mount(hostId, items, value, onChange) {
  const host = $(hostId);
  host.replaceChildren(dropdown(items, value, onChange));
}

function rebuildRequire() {
  const choices = [
    { label: 'Any player', value: 'any' },
    { label: 'Every player', value: 'all' },
    ...Array.from({ length: state.players }, (_, i) => ({ label: `Only P${i + 1}`, value: `p${i + 1}` })),
  ];
  // Shrinking the lobby can strand the choice on a player who no longer exists.
  if (!choices.some(o => o.value === state.require)) state.require = 'any';
  mount('#require', choices, state.require, v => { state.require = v; rebuildRequire(); });
}

/**
 * Says which of Neow's three options the chosen relic arrives as.
 *
 * This replaced a dropdown. Neow always offers exactly one curse relic and two positives, and
 * no relic appears in both pools, so naming a relic already decides its branch: the control
 * could only restate that or contradict it, and contradicting it failed the search. Stating it
 * is the useful half, because "this one costs you a curse" is a real thing to know before
 * searching for it.
 */
const BRANCH_NOTE = {
  curse: ['Curse branch', 'one curse relic is always offered, and taking it means taking the curse'],
  positive: ['Positive pool', 'offered as one of the two positive options'],
  coinflip: ['Coin-flip pair', 'a coin flip decides which of its pair joins the positive pool'],
};

function renderBranch(relic) {
  const box = $('#neowBranch');
  const note = relic && BRANCH_NOTE[relic.group];
  box.hidden = !note;
  if (!note) return;

  box.replaceChildren();
  box.classList.toggle('is-curse', relic.group === 'curse');
  const tag = el('span', 'branch-tag', note[0]);
  box.append(tag, el('span', null, note[1]));
}

function renderAct1() {
  mount('#act1',
    [{ label: 'Either', value: 'any' }, ...catalog.act1Maps.map(m => ({ label: m, value: m }))],
    state.act1,
    v => {
      state.act1 = v;
      renderAct1();
      // Act 1's two maps share neither bosses nor most events, so pinning one makes the
      // other's content unreachable. Drop what just became impossible and redraw both panels.
      pruneForMap();
      renderBosses();
      renderEvents();
    });
}

$('#neowRelic').onclick = async e => {
  if (e.target.dataset.clear) { setNeowRelic(null); return; }
  const picked = await openPicker('Neow offers…', [
    { title: 'Curse branch: take a curse to get it', items: catalog.neowCurses },
    { title: 'Positive pool', items: catalog.neowPositives },
    { title: 'Coin-flip pairs: one of each pair joins the pool', items: catalog.neowCoinFlip },
  ]);
  if (picked) setNeowRelic(picked);
};

function setNeowRelic(r) {
  state.relic = r ? r.slug : '';
  fillRelicField($('#neowRelic'), r, 'Any relic');
  renderBranch(r);
}

$('#addAncient').onclick = () => {
  state.ancients.push({ ancient: catalog.ancients[0].id, relic: '', require: 'any' });
  renderAncients();
};

$('#addEvent').onclick = () => {
  state.events.push({ act: 1, event: '', within: 3 });
  renderEvents();
};

$('#addBoss').onclick = () => {
  state.bosses.push({ act: catalog.actContent.length, boss: '', exclude: false });
  renderBosses();
};

/**
 * Ascension only ever touches one draw, but that draw is a whole extra boss, so the control
 * lives with the lobby rather than beside the bosses: it describes the run you are setting up.
 */
function renderAscension() {
  mount('#ascension',
    Array.from({ length: MAX_ASCENSION + 1 }, (_, i) => ({ label: i === 0 ? 'None' : `A${i}`, value: i })),
    state.ascension,
    v => {
      state.ascension = v;
      renderAscension();
      // The number of boss slots on the final act just changed, so a row that was blocked
      // from excluding may now be free to, or the other way round.
      renderBosses();
      syncAscensionHint();
    });
  syncAscensionHint();
}

function syncAscensionHint() {
  $('#ascensionHint').textContent = state.ascension >= DOUBLE_BOSS
    ? 'Double Boss: the final act has two. It is the last draw generation makes, so nothing else about the run changes.'
    : 'Only Ascension 10 changes what a seed generates, by giving the final act a second boss.';
}

$('#start').oninput = () => { startIsUserSet = $('#start').value.trim() !== ''; };

$('#form').onsubmit = e => {
  e.preventDefault();
  if (stream) { stopSearch('Cancelled.'); return; }
  startSearch();
};

async function inspect() {
  const seed = $('#inspectSeed').value.trim();
  if (!seed) return;
  stopSearch('');

  const q = new URLSearchParams({ seed, players: state.players });
  if (charactersReady()) q.set('characters', state.characters.join(','));
  if (state.ascension > 0) q.set('ascension', state.ascension);

  const out = $('#out');
  out.replaceChildren();
  setBusy(true, 'Looking up…');

  const res = await fetch('/api/explain?' + q);
  const body = await res.json();
  setBusy(false, '');

  if (!res.ok) { out.appendChild(el('div', 'err', body.error)); return; }
  out.appendChild(renderHit(body));
  if (!charactersReady())
    out.appendChild(el('div', 'hint', 'Pick a character for every player to see Acts 2 and 3.'));
}

$('#inspectGo').onclick = inspect;
$('#clear').onclick = resetAll;
$('#inspectSeed').onkeydown = e => { if (e.key === 'Enter') { e.preventDefault(); inspect(); } };

// ---- Boot ----------------------------------------------------------------------------------

(async function boot() {
  catalog = await (await fetch('/api/catalog')).json();

  const note = (r, kind = 'relic') => {
    if (r.hasArt) artIndex.set(key(kind, r.slug), true);
    if (r.description || r.note)
      textIndex.set(key(kind, r.slug), { description: r.description, note: r.note });
  };
  // Not `list.forEach(note)`: forEach hands the callback the index as a second argument, which
  // would land in `kind` and file every relic under "0:", "1:", … where nothing looks for it.
  for (const list of [catalog.neowCurses, catalog.neowPositives, catalog.neowCoinFlip])
    for (const r of list) note(r);
  for (const a of catalog.ancients) for (const r of a.relics) note(r);

  // Card pools overlap heavily between characters, so these all write the same entries more
  // than once. Cheaper than de-duplicating, and the last write is identical to the first.
  for (const pool of catalog.cardPools)
    for (const c of pool.cards) { note(c, 'card'); cardNames.set(c.slug, c.name); }

  for (const c of catalog.characters) note(c, 'character');

  // The Ancients themselves, as opposed to the relics they offer (filed above). Comes as its
  // own list rather than off catalog.ancients because it also covers Neow, which opens Act 1
  // but has no searchable offer and so is not in that list.
  for (const slug of catalog.ancientArtSlugs ?? []) artIndex.set(key('ancient', slug), true);

  // Events, likewise a flat list: an event that both Act 1 maps carry is one illustration.
  for (const slug of catalog.eventArtSlugs ?? []) artIndex.set(key('event', slug), true);

  // Their arrival text, which arrives on the events themselves rather than in a list of its own.
  // An event in two acts is filed twice with the same value, as the card pools are.
  for (const a of catalog.actContent)
    for (const e of a.events) note(e, 'event');

  // Shop and chest relics live in the same art and text tables as Neow's, so they file under
  // 'relic'. The two pools are disjoint by rarity, so neither overwrites the other.
  for (const r of [...catalog.shopRelics, ...catalog.chestRelics]) {
    note(r);
    relicNames.set(r.slug, r.name);
  }

  $('#meta').textContent = `game ${catalog.gameVersion} · art: ${catalog.assetStatus}`;

  // A patched or modded game produces output that still looks plausible and is no longer true.
  // It goes at the top of the results rather than in a tooltip, because it is the one failure
  // mode a user has no way of noticing on their own.
  if (catalog.driftWarning) {
    const box = $('#drift');
    box.hidden = false;
    for (const line of catalog.driftWarning.split('\n')) box.appendChild(el('div', null, line));
  }

  // The server already generates against whatever the local profile says, so this is only to
  // put that on screen. A partial unlock is worth stating unprompted: it changes the answers,
  // and a user who does not know we read it has no reason to trust them.
  fetch('/api/profile').then(r => r.json()).then(p => {
    const meta = $('#meta');
    if (!p.found) { meta.textContent += ' · no save found, assuming all unlocked'; return; }
    meta.textContent += p.fullyUnlocked
      ? ' · save: all unlocked'
      : ` · save: ${p.revealedEpochs}/${p.totalEpochs} epochs`;
  }).catch(() => {});

  renderCriteria();
})();

/**
 * Redraws every panel from `state`. Boot and Clear both go through here, so a criterion added
 * later cannot end up rendered on load but missed on reset.
 */
function renderCriteria() {
  renderAct1();
  rebuildRequire();
  renderCharacters();
  renderAscension();
  renderBosses();
  renderEvents();
  renderAncients();
  renderShops();
  renderChests();
  // No relic is preselected. Silken Tress used to be, as a leftover from when the Neow relic
  // was the only criterion there was; it now reads as a filter the user did not ask for.
  setNeowRelic(state.relic
    ? [...catalog.neowCurses, ...catalog.neowPositives, ...catalog.neowCoinFlip]
        .find(r => r.slug === state.relic) ?? null
    : null);
}

/**
 * Back to a blank search. Also clears the scan settings and the results, because a Clear that
 * left a previous run's hits on screen next to empty criteria would read as the criteria having
 * produced them.
 */
function resetAll() {
  stopSearch('');
  state = defaultState();

  $('#results').value = '25';
  $('#count').value = '5000000';
  $('#start').value = '';
  startIsUserSet = false;
  $('#inspectSeed').value = '';

  renderCriteria();

  const out = $('#out');
  out.replaceChildren();
  const empty = el('div', 'empty');
  empty.appendChild(el('span', null, 'Set what you want, then hit '));
  empty.appendChild(el('b', null, 'Search'));
  empty.appendChild(el('span', null, '.'));
  out.appendChild(empty);
}
