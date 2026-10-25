// MxU Slides browser remote: the service, its slides, Next and Previous, the clears, timers,
// the stage message and alerts. Every action is one of the Local API's own endpoints (the same
// ones the Mac serves), so the remote can do nothing a key's scope doesn't allow: a View key's
// page shows everything but its buttons are switched off, and the app would refuse them anyway.
'use strict';

(() => {
  const { api, el } = MxU;
  const $ = id => document.getElementById(id);

  const state = {
    session: null,     // { name, scope }
    doc: null,         // { service: APIServiceSnapshot, other: PresentationRef }
    status: null,      // ShowStatus, pushed on every change
    timers: [],        // TimerStatus[], with the moment they arrived
    timersAt: 0,
    alerts: [],
    chosen: null,      // the service row (or 'other') whose slides are shown
    follow: true,      // …or whatever is live
  };

  // MARK: Start

  document.addEventListener('DOMContentLoaded', start);

  async function start() {
    if (!MxU.key()) return showPairing();
    try {
      state.session = await api('GET', '/remote/session');
    } catch (e) {
      if (e.status === 401) { MxU.forget(); return showPairing(); }
      $('pair').hidden = false;
      $('pair').replaceChildren(el('p', { class: 'pair-message' }, e.message), el('button', { onclick: start }, 'Try again'));
      return;
    }
    run();
  }

  function showPairing() {
    $('app').hidden = true;
    $('pair').hidden = false;
    MxU.pairing($('pair'), 'Pair this remote', session => { state.session = session; run(); });
  }

  function run() {
    $('pair').hidden = true;
    $('app').hidden = false;
    const viewOnly = state.session.scope === 'view';
    $('scope').hidden = !viewOnly;
    document.querySelectorAll('.control').forEach(b => { b.disabled = viewOnly; });
    wire();
    Promise.all([loadDoc(), loadAlerts(), api('GET', '/v1/status').then(s => { state.status = s; }), loadTimers()])
      .then(renderAll, error => toast(error.message));
    MxU.live(['show', 'timers', 'library'], onEvent, onLink);
    setInterval(tick, 500);
  }

  let wired = false;
  function wire() {
    if (wired) return;
    wired = true;
    document.querySelectorAll('.tabs button').forEach(tab => tab.addEventListener('click', () => showTab(tab.dataset.tab)));
    $('next').addEventListener('click', () => act(() => api('POST', '/v1/show/advance', { steps: 1 })));
    $('prev').addEventListener('click', () => act(() => api('POST', '/v1/show/advance', { steps: -1 })));
    document.querySelectorAll('[data-clear]').forEach(button => button.addEventListener('click', () => {
      const what = button.dataset.clear;
      act(() => (what === 'all' ? api('POST', '/v1/show/clear-all') : api('POST', '/v1/show/clear', { function: what })));
    }));
    $('follow').addEventListener('click', () => { state.follow = true; renderSlides(); });
    $('stage-form').addEventListener('submit', event => {
      event.preventDefault();
      const text = $('stage-text').value.trim();
      if (text) act(() => api('POST', '/remote/stage-message', { text }), 'Sent to the stage');
    });
    $('stage-clear').addEventListener('click', () => act(() => api('POST', '/remote/stage-message', { text: '' })));
    // Keyboard shortcuts on a laptop or a tablet with a keyboard (a presenter clicker sends these too).
    document.addEventListener('keydown', event => {
      if (event.target.closest('input, textarea') || state.session?.scope === 'view') return;
      if (['ArrowRight', 'PageDown', ' '].includes(event.key)) { event.preventDefault(); $('next').click(); }
      if (['ArrowLeft', 'PageUp'].includes(event.key)) { event.preventDefault(); $('prev').click(); }
    });
  }

  function showTab(name) {
    document.querySelectorAll('.tabs button').forEach(tab => tab.setAttribute('aria-selected', String(tab.dataset.tab === name)));
    for (const pane of ['service', 'slides', 'timers', 'messages']) $('tab-' + pane).hidden = pane !== name;
  }

  // MARK: Data

  async function loadDoc() { state.doc = await api('GET', '/remote/service'); }
  async function loadAlerts() { state.alerts = await api('GET', '/remote/alerts'); }
  async function loadTimers() { setTimers(await api('GET', '/v1/timers')); }

  function setTimers(timers) {
    state.timers = timers ?? [];
    state.timersAt = Date.now();
  }

  let reloading = null;
  /** The library changed (a service edited, a song added): fetch the service again, once, shortly. */
  function reloadSoon() {
    clearTimeout(reloading);
    reloading = setTimeout(() => Promise.all([loadDoc(), loadAlerts()]).then(renderAll, () => { }), 300);
  }

  function onEvent(topic, data) {
    if (topic === 'show') {
      const before = state.status;
      state.status = data;
      // Another service, or something live that isn't in the one we have: fetch it.
      if (data?.currentServiceId !== state.doc?.service?.service?.id || (data?.liveSlide && !findLive())) reloadSoon();
      if (before?.liveSlide?.slideId !== data?.liveSlide?.slideId || before?.liveSlide?.occurrence !== data?.liveSlide?.occurrence) {
        if (state.follow) renderSlides(); else markLive();
      }
      renderNow();
      renderItems();
      renderAlertLine();
    } else if (topic === 'timers') {
      setTimers(data);
      renderTimers();
    } else if (topic === 'library') {
      reloadSoon();
    }
  }

  function onLink(link) {
    $('link').className = 'link ' + (link === 'live' ? 'live' : link === 'offline' ? 'offline' : '');
    $('link').title = { live: 'Connected', offline: 'Reconnecting…', connecting: 'Connecting…' }[link] ?? link;
    // The key was revoked in Settings: start again from the pairing screen.
    if (link === 'unpaired') { MxU.forget(); location.reload(); }
    // Back after a drop: what changed meanwhile wasn't pushed to us.
    if (link === 'live' && state.doc) Promise.all([loadDoc(), loadTimers(), api('GET', '/v1/status').then(s => { state.status = s; })]).then(renderAll, () => { });
  }

  async function act(work, done) {
    try {
      await work();
      if (done) toast(done);
    } catch (e) {
      toast(e.status === 403 ? 'This device can only view. Pair it with Control to run the show.' : e.message);
    }
  }

  let toastTimer = null;
  function toast(text) {
    const box = $('toast');
    box.textContent = text;
    box.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { box.hidden = true; }, 3000);
  }

  // MARK: Drawing

  function renderAll() {
    renderNow();
    renderItems();
    renderSlides();
    renderTimers();
    renderAlerts();
    renderAlertLine();
  }

  function renderNow() {
    const live = state.status?.liveSlide;
    $('now-item').textContent = live
      ? [state.status.position?.currentItemName ?? live.presentationName, live.slideName].filter(Boolean).join(' · ')
      : 'Nothing live';
    $('now-text').textContent = live ? (live.text ?? '').replace(/\s*\n\s*/g, ' / ') : '';
  }

  /** The row (or the presentation outside the service) that's live now, and the slide's index and id in it. */
  function findLive() {
    const live = state.status?.liveSlide;
    if (!live) return null;
    const items = state.doc?.service?.items ?? [];
    const row = (live.serviceItemId && items.find(i => i.id === live.serviceItemId))
      ?? (!live.serviceItemId && items.find(i => i.presentation?.id === live.presentationId));
    if (row) return { key: row.id, index: live.occurrence ?? live.slideIndex, slide: live.slideId };
    if (state.doc?.other?.id === live.presentationId) return { key: 'other', index: live.slideIndex, slide: live.slideId };
    return null;
  }

  function renderItems() {
    const service = state.doc?.service;
    $('service-name').textContent = service?.service?.name ?? 'No service open on the computer';
    const live = findLive();
    const list = $('service-items');
    const rows = (service?.items ?? []).map(item => {
      if (item.kind === 'header') return el('li', { class: 'item header', style: item.colorHex ? { '--item': css(item.colorHex) } : null }, item.name);
      const detail = item.presentation ? `${item.presentation.slides.length} slide${item.presentation.slides.length === 1 ? '' : 's'}`
        : item.media ? 'Media' : ({ audio: 'Music', playlist: 'Playlist', info: 'Plan item' }[item.kind] ?? '');
      const goes = item.presentation?.slides.length > 0 || item.media;
      return el('li', {
        class: `item${live?.key === item.id ? ' live' : ''}${chosenKey() === item.id ? ' chosen' : ''}`,
        style: item.colorHex ? { '--item': css(item.colorHex) } : null,
        onclick: () => { if (item.presentation) { state.chosen = item.id; state.follow = false; renderSlides(); renderItems(); showTabIfNarrow('slides'); } },
      },
      el('div', { class: 'name' }, item.name, el('small', {}, detail)),
      goes ? el('button', {
        class: 'go control', disabled: state.session.scope === 'view', 'aria-label': `Go live: ${item.name}`,
        onclick: event => { event.stopPropagation(); goLive(item, 0); },
      }, 'Go') : null);
    });
    if (rows.length === 0) rows.push(el('li', { class: 'muted' }, 'The computer has no service open. Songs fired from its library still show under Slides.'));
    list.replaceChildren(...rows);
  }

  function showTabIfNarrow(name) { if (window.matchMedia('(max-width: 899px)').matches) showTab(name); }

  function chosenKey() {
    if (state.follow) return findLive()?.key ?? state.chosen ?? (state.doc?.other ? 'other' : null);
    return state.chosen;
  }

  /** The presentation shown under Slides: the chosen row's, or the one outside the service. */
  function chosenPresentation() {
    const key = chosenKey();
    if (key === 'other') return { presentation: state.doc?.other, item: null };
    const item = state.doc?.service?.items.find(i => i.id === key);
    return { presentation: item?.presentation ?? null, item };
  }

  function renderSlides() {
    const { presentation, item } = chosenPresentation();
    $('follow').setAttribute('aria-pressed', String(state.follow));
    $('slides-title').textContent = presentation ? (item?.name ?? presentation.name) + (presentation.arrangementName ? ` · ${presentation.arrangementName}` : '') : 'No slides';
    const grid = $('slides');
    if (!presentation) {
      grid.replaceChildren(el('p', { class: 'muted' }, 'Pick a song or presentation in Service.'));
      return;
    }
    grid.replaceChildren(...presentation.slides.map(slide => {
      const pic = el('div', { class: 'pic' }, (slide.text ?? '').slice(0, 80));
      const path = `/remote/thumbnails/${encodeURIComponent(presentation.id)}/${encodeURIComponent(slide.id)}?width=320&v=${slide.thumbnailChecksum ?? ''}`;
      MxU.image(path).then(url => pic.replaceChildren(el('img', { src: url, alt: slide.text ?? '' })), () => { });
      return el('button', {
        class: 'slide control', 'data-index': slide.index, 'data-slide': slide.id, disabled: state.session.scope === 'view',
        style: slide.groupColorHex ? { '--group': css(slide.groupColorHex) } : null,
        onclick: () => goLive(item, slide.index, presentation),
      }, pic, el('span', { class: 'label' }, el('b', {}, String(slide.index + 1)), slide.label ?? slide.group ?? ''));
    }));
    markLive();
  }

  function markLive() {
    const live = findLive();
    const tiles = [...document.querySelectorAll('#slides .slide')];
    // The live slide's place, or (a repeated chorus, say) the first tile showing that slide.
    const match = live && live.key === chosenKey()
      ? tiles.find(t => t.dataset.index === String(live.index) && t.dataset.slide === live.slide) ?? tiles.find(t => t.dataset.slide === live.slide)
      : null;
    tiles.forEach(tile => tile.classList.toggle('live', tile === match));
    match?.scrollIntoView({ block: 'nearest' });
  }

  function goLive(item, index, presentation) {
    if (item?.media && !item.presentation) return act(() => api('POST', `/v1/media/${encodeURIComponent(item.media.id)}/fire`));
    if (item) return act(() => api('POST', '/v1/show/slide', { serviceItemId: item.id, occurrence: index }));
    if (presentation) return act(() => api('POST', '/v1/show/slide', { presentationId: presentation.id, slideIndex: index }));
  }

  // MARK: Timers

  /** A timer's reading now: the pushed value moved on by the time since it arrived. */
  function reading(timer) {
    const gone = (Date.now() - state.timersAt) / 1000;
    const moving = timer.running || timer.mode === 'countdownToTime';
    if (!moving) return timer.displaySeconds;
    return timer.mode === 'countUp' ? timer.displaySeconds + gone : timer.displaySeconds - gone;
  }

  function clock(seconds) {
    const sign = seconds < 0 ? '−' : '';
    const s = Math.floor(Math.abs(seconds));
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
    const two = n => String(n).padStart(2, '0');
    return sign + (h > 0 ? `${h}:${two(m)}:${two(r)}` : `${m}:${two(r)}`);
  }

  function renderTimers() {
    const list = $('timers');
    if (state.timers.length === 0) {
      list.replaceChildren(el('li', { class: 'muted' }, 'No timers. Add them in the Timers panel on the computer.'));
      return;
    }
    const viewOnly = state.session.scope === 'view';
    // The church's own timers first; the automatic service-tracking ones after, without buttons (they run themselves).
    const tracking = timer => timer.id.startsWith('service_tracking.');
    const ordered = [...state.timers.filter(t => !tracking(t)), ...state.timers.filter(tracking)];
    list.replaceChildren(...ordered.map(timer => el('li', { class: 'timer', 'data-id': timer.id },
      el('span', { class: 'clock' }),
      tracking(timer) ? el('span', { class: 'muted' }, 'automatic') : el('div', { class: 'buttons' },
        el('button', { class: 'control', disabled: viewOnly, 'aria-label': timer.running ? 'Pause' : 'Start',
          onclick: () => act(() => api('POST', `/v1/timers/${encodeURIComponent(timer.id)}/${timer.running ? 'pause' : 'play'}`)) }, timer.running ? '❚❚' : '▶'),
        el('button', { class: 'control', disabled: viewOnly, 'aria-label': 'Reset',
          onclick: () => act(() => api('POST', `/v1/timers/${encodeURIComponent(timer.id)}/reset`)) }, '↺')),
      el('span', { class: 'who' }, timer.name))));
    tick();
  }

  /** Twice a second: the timers' readouts (only the digits, so a tap in progress isn't lost). */
  function tick() {
    for (const timer of state.timers) {
      const face = document.querySelector(`.timer[data-id="${CSS.escape(timer.id)}"] .clock`);
      if (!face) continue;
      const value = reading(timer);
      face.textContent = clock(value);
      face.classList.toggle('running', timer.running);
      face.classList.toggle('over', timer.mode !== 'countUp' && value < 0);
    }
  }

  // MARK: Messages

  function renderAlertLine() {
    const alert = state.status?.alert;
    const line = $('alert-live');
    line.hidden = !alert;
    if (!alert) return;
    const where = { confidence: 'On stage', audience: 'On the screens', both: 'On stage and screens' }[alert.target] ?? 'Live';
    line.replaceChildren(`${where}: ${alert.message} `, el('button', {
      class: 'control', disabled: state.session.scope === 'view', onclick: () => act(() => api('POST', '/v1/alerts/dismiss')),
    }, 'Turn off'));
  }

  function renderAlerts() {
    const list = $('alerts');
    if (state.alerts.length === 0) {
      list.replaceChildren(el('li', { class: 'muted' }, 'No saved alerts. Make them in the Alerts panel on the computer.'));
      return;
    }
    const viewOnly = state.session.scope === 'view';
    list.replaceChildren(...state.alerts.map(alert => {
      const boxes = alert.blanks.map(blank => el('input', { placeholder: blank, 'aria-label': blank, 'data-blank': blank }));
      return el('li', { class: 'alert' },
        el('b', {}, alert.name),
        el('span', { class: 'text' }, alert.message),
        ...boxes,
        el('button', {
          class: 'control', disabled: viewOnly,
          onclick: () => {
            const tokens = Object.fromEntries(boxes.map(box => [box.dataset.blank, box.value.trim()]));
            act(() => api('POST', '/v1/alerts/fire', { presetId: alert.id, tokens }), `Sent “${alert.name}”`);
          },
        }, 'Send'));
    }));
  }

  /** A colour from the library ("#RRGGBBAA") as CSS, or a neutral one if it isn't a colour. */
  function css(hex) {
    return /^#[0-9a-f]{6}([0-9a-f]{2})?$/i.test(hex) ? hex : 'var(--border)';
  }
})();
