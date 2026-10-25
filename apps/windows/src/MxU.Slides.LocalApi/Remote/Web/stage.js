// MxU Slides stage view: the current and next slide, the clock, the timers and the stage message,
// for a spare TV by the stage or the pastor's tablet. It only reads, so a View key is enough.
// Everything arrives over the WebSocket the moment it changes; the clock and running timers tick
// here, twice a second, from the last values pushed.
'use strict';

(() => {
  const { api, el } = MxU;
  const $ = id => document.getElementById(id);
  const state = { status: null, timers: [], timersAt: 0 };

  document.addEventListener('DOMContentLoaded', start);

  async function start() {
    if (!MxU.key()) return pair();
    try {
      await api('GET', '/remote/session');
    } catch (e) {
      if (e.status === 401) { MxU.forget(); return pair(); }
      $('pair').hidden = false;
      $('pair').replaceChildren(el('p', { class: 'pair-message' }, e.message), el('button', { onclick: start }, 'Try again'));
      return;
    }
    run();
  }

  function pair() {
    $('pair').hidden = false;
    MxU.pairing($('pair'), 'Pair this stage display', run);
  }

  function run() {
    $('pair').hidden = true;
    $('stage').hidden = false;
    Promise.all([api('GET', '/v1/status'), api('GET', '/v1/timers')]).then(([status, timers]) => {
      state.status = status;
      setTimers(timers);
      render();
    }, () => { });
    MxU.live(['show', 'timers'], (topic, data) => {
      if (topic === 'show') { state.status = data; render(); }
      if (topic === 'timers') { setTimers(data); renderTimers(); }
    }, link => {
      $('link').className = 'stage-link' + (link === 'offline' ? ' offline' : '');
      if (link === 'unpaired') { MxU.forget(); location.reload(); }
      if (link === 'live') api('GET', '/v1/status').then(status => { state.status = status; render(); }, () => { });
    });
    setInterval(tick, 500);
    window.addEventListener('resize', fitAll);
    pointer();
    keepAwake();
  }

  function setTimers(timers) {
    state.timers = timers ?? [];
    state.timersAt = Date.now();
  }

  function render() {
    const status = state.status;
    const live = status?.liveSlide;
    const current = $('current');
    const text = live?.text ?? '';
    current.textContent = text || (live ? live.presentationName ?? '' : 'Nothing on screen');
    current.classList.toggle('empty', !text);
    $('next').textContent = status?.nextSlide?.text ?? status?.nextSlideText ?? '';
    $('item').textContent = [status?.position?.currentItemName, status?.position?.nextItemName && `next: ${status.position.nextItemName}`]
      .filter(Boolean).join('  ·  ');
    const alert = status?.alert;
    const message = $('message');
    const onStage = alert && (alert.target === 'confidence' || alert.target === 'both');
    message.hidden = !onStage;
    if (onStage) {
      message.textContent = alert.message;
      message.classList.toggle('flash', alert.behavior === 'flash');
    }
    renderTimers();
    fitAll();
  }

  /** Running timers first, at most three, each with its name under it. */
  function renderTimers() {
    const shown = [...state.timers].sort((a, b) => Number(b.running) - Number(a.running)).slice(0, 3);
    $('timers').replaceChildren(...shown.map(timer => el('span', { class: 'timer-face', 'data-id': timer.id },
      el('span', { class: 'digits' }), el('small', {}, timer.name))));
    tick();
  }

  function reading(timer) {
    const gone = (Date.now() - state.timersAt) / 1000;
    if (!(timer.running || timer.mode === 'countdownToTime')) return timer.displaySeconds;
    return timer.mode === 'countUp' ? timer.displaySeconds + gone : timer.displaySeconds - gone;
  }

  function clock(seconds) {
    const sign = seconds < 0 ? '−' : '';
    const s = Math.floor(Math.abs(seconds));
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
    const two = n => String(n).padStart(2, '0');
    return sign + (h > 0 ? `${h}:${two(m)}:${two(r)}` : `${m}:${two(r)}`);
  }

  function tick() {
    $('clock').textContent = new Date().toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    for (const timer of state.timers) {
      const face = document.querySelector(`.timer-face[data-id="${CSS.escape(timer.id)}"]`);
      if (!face) continue;
      const value = reading(timer);
      face.querySelector('.digits').textContent = clock(value);
      face.classList.toggle('running', timer.running);
      face.classList.toggle('over', timer.mode !== 'countUp' && value < 0);
    }
  }

  /** Shrinks a box's text until it fits (a binary search on the font size, as the app's own text layout does). */
  function fit(box, largest) {
    const parent = box.parentElement;
    let low = 12, high = largest;
    box.style.fontSize = high + 'px';
    if (box.scrollHeight <= parent.clientHeight && box.scrollWidth <= parent.clientWidth) return;
    while (high - low > 1) {
      const mid = Math.floor((low + high) / 2);
      box.style.fontSize = mid + 'px';
      if (box.scrollHeight <= parent.clientHeight && box.scrollWidth <= parent.clientWidth) low = mid; else high = mid;
    }
    box.style.fontSize = low + 'px';
  }

  function fitAll() {
    const current = $('current');
    if (current.classList.contains('empty')) current.style.fontSize = '';
    else fit(current, Math.round(window.innerHeight * 0.14));
    fit($('next'), Math.round(window.innerHeight * 0.065));
  }

  /** The pointer and the Full screen button show only after the screen is touched or the mouse moves. */
  function pointer() {
    const stage = $('stage');
    let hide = null;
    const show = () => {
      stage.classList.add('pointer');
      clearTimeout(hide);
      hide = setTimeout(() => stage.classList.remove('pointer'), 3000);
    };
    ['mousemove', 'touchstart'].forEach(name => document.addEventListener(name, show, { passive: true }));
    $('full').addEventListener('click', () => {
      if (document.fullscreenElement) document.exitFullscreen?.();
      else document.documentElement.requestFullscreen?.().catch(() => { });
    });
  }

  /** Asks the tablet not to sleep, where the browser allows it (it needs https, so often it can't). */
  async function keepAwake() {
    try {
      let lock = await navigator.wakeLock?.request('screen');
      document.addEventListener('visibilitychange', async () => {
        if (document.visibilityState === 'visible' && lock?.released) lock = await navigator.wakeLock.request('screen');
      });
    } catch { /* not allowed here: the device's own sleep setting applies */ }
  }
})();
