// MxU Slides browser remote: what the remote and the stage view share.
//
// - The key: pairing (POST /remote/pair) gives this browser a key, kept in localStorage on this
//   device only. It is sent in the Authorization header and in the WebSocket's first message,
//   never in an address, so it can't end up in history, bookmarks or a server log.
// - Requests: api() calls the app's Local API on the same computer that served this page.
// - Live updates: live() keeps a WebSocket open to /remote/ws, subscribes to topics, and comes
//   back by itself after a dropped Wi-Fi connection.
// - Pairing: pairing() draws the "type the code" screen both pages start with.
'use strict';

const MxU = (() => {
  const STORE = 'mxu-slides-remote-key';

  function key() {
    try { return localStorage.getItem(STORE); } catch { return null; }
  }

  function remember(secret) {
    try { localStorage.setItem(STORE, secret); } catch { /* private mode: works until the tab closes */ }
    memory = secret;
  }

  function forget() {
    try { localStorage.removeItem(STORE); } catch { }
    memory = null;
  }

  // A fallback for browsers that refuse localStorage (some private modes).
  let memory = null;
  const current = () => key() ?? memory;

  /** An error from the app, with its status and its own message. */
  class ApiError extends Error {
    constructor(status, message, body) { super(message); this.status = status; this.body = body; }
  }

  /** Calls the Local API. Throws ApiError; a 401 means this device must pair again. */
  async function api(method, path, body) {
    const headers = { Accept: 'application/json' };
    const secret = current();
    if (secret) headers.Authorization = 'Bearer ' + secret;
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    let response;
    try {
      response = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store' });
    } catch {
      throw new ApiError(0, "Can't reach the computer. Check this device is on the church Wi-Fi.");
    }
    const text = await response.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch { json = null; }
    if (!response.ok) throw new ApiError(response.status, json?.message ?? `The computer said no (${response.status}).`, json);
    return json;
  }

  // Thumbnails are fetched with the key in the header, then shown from memory (a blob: URL).
  const images = new Map();
  const queue = [];
  let loading = 0;

  /** A slide picture's local address, fetched at most two at a time so the app's screen stays smooth. */
  function image(path) {
    if (images.has(path)) return images.get(path);
    const promise = new Promise((resolve, reject) => queue.push({ path, resolve, reject }));
    images.set(path, promise);
    promise.catch(() => images.delete(path));
    pump();
    return promise;
  }

  function pump() {
    while (loading < 2 && queue.length > 0) {
      const job = queue.shift();
      loading++;
      fetch(job.path, { headers: { Authorization: 'Bearer ' + current() } })
        .then(r => (r.ok ? r.blob() : Promise.reject(new ApiError(r.status, 'No picture'))))
        .then(blob => job.resolve(URL.createObjectURL(blob)), job.reject)
        .finally(() => { loading--; pump(); });
    }
  }

  /**
   * Keeps a WebSocket open. onEvent(topic, data) gets each push; onState(state) is told
   * 'connecting', 'live', 'offline' or 'unpaired'. Returns { close, send }.
   */
  function live(topics, onEvent, onState) {
    let socket = null;
    let closed = false;
    let wait = 1000;
    let ping = null;
    const send = message => { if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message)); };

    function open() {
      if (closed) return;
      onState('connecting');
      const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
      socket = new WebSocket(`${scheme}//${location.host}/remote/ws`);
      socket.onopen = () => socket.send(JSON.stringify({ type: 'auth', token: current() ?? '' }));
      socket.onmessage = message => {
        let frame;
        try { frame = JSON.parse(message.data); } catch { return; }
        if (frame.type === 'hello') {
          wait = 1000;
          onState('live');
          send({ type: 'subscribe', topics });
          clearInterval(ping);
          ping = setInterval(() => send({ type: 'ping' }), 25000);
        } else if (frame.type === 'event') {
          onEvent(frame.topic, frame.data);
        } else if (frame.type === 'error' && frame.code === 'unauthorized') {
          closed = true;
          onState('unpaired');
        }
      };
      socket.onclose = () => {
        clearInterval(ping);
        if (closed) return;
        onState('offline');
        setTimeout(open, wait);
        wait = Math.min(wait * 2, 15000);
      };
    }

    // A phone that slept (screen off) reconnects as soon as it wakes.
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible' && socket?.readyState === WebSocket.CLOSED) { wait = 1000; open(); }
    });
    open();
    return { send, close: () => { closed = true; socket?.close(); } };
  }

  /** "iPhone", "iPad", "Android phone"…: the name a new key is given, so it's easy to find in Settings. */
  function deviceName() {
    const ua = navigator.userAgent;
    if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'iPad';
    if (/iPhone/.test(ua)) return 'iPhone';
    if (/Android/.test(ua)) return /Mobile/.test(ua) ? 'Android phone' : 'Android tablet';
    if (/Windows/.test(ua)) return 'Windows browser';
    if (/Macintosh/.test(ua)) return 'Mac browser';
    return 'Browser';
  }

  function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [name, value] of Object.entries(props)) {
      if (name === 'class') node.className = value;
      // Styles go through the style object: the page's security policy refuses style="" attributes.
      else if (name === 'style') { for (const [property, setting] of Object.entries(value ?? {})) node.style.setProperty(property, setting); }
      else if (name.startsWith('on')) node.addEventListener(name.slice(2), value);
      else if (value === true) node.setAttribute(name, '');
      else if (value !== false && value != null) node.setAttribute(name, value);
    }
    for (const child of children.flat()) if (child != null) node.append(child);
    return node;
  }

  /**
   * The pairing screen: a 6-digit code from Settings, or a pasted key. Calls onPaired(session)
   * once this device has a working key.
   */
  function pairing(container, purpose, onPaired) {
    const message = el('p', { class: 'pair-message', role: 'status' });
    const code = el('input', {
      id: 'pair-code', inputmode: 'numeric', autocomplete: 'one-time-code', maxlength: '7', placeholder: '123 456',
      'aria-label': 'Pairing code',
    });
    const name = el('input', { id: 'pair-name', maxlength: '40', value: deviceName(), 'aria-label': 'This device’s name' });
    const pasted = el('input', { id: 'pair-key', autocomplete: 'off', placeholder: 'mxu_…', 'aria-label': 'Key' });
    const pairButton = el('button', { class: 'primary', type: 'submit' }, 'Pair');

    async function finish(secret) {
      remember(secret);
      try {
        onPaired(await api('GET', '/remote/session'));
      } catch (e) {
        forget();
        message.textContent = e.status === 401 ? 'That key isn’t one this computer knows.' : e.message;
      }
    }

    const form = el('form', {
      class: 'pair-form',
      onsubmit: async event => {
        event.preventDefault();
        message.textContent = 'Pairing…';
        pairButton.disabled = true;
        try {
          const answer = await api('POST', '/remote/pair', { code: code.value, name: name.value });
          await finish(answer.key);
        } catch (e) {
          message.textContent = e.message;
        } finally {
          pairButton.disabled = false;
        }
      },
    },
    el('label', { for: 'pair-code' }, 'Pairing code'),
    code,
    el('label', { for: 'pair-name' }, 'Name this device'),
    name,
    pairButton);

    const keyForm = el('form', {
      class: 'pair-form',
      onsubmit: event => { event.preventDefault(); if (pasted.value.trim()) finish(pasted.value.trim()); },
    }, pasted, el('button', { type: 'submit' }, 'Use this key'));

    container.replaceChildren(
      el('div', { class: 'pair-card' },
        el('img', { src: '/remote/assets/icon.svg', alt: '', class: 'pair-icon' }),
        el('h1', {}, purpose),
        el('p', { class: 'muted' }, 'On the computer running MxU Slides, open ', el('b', {}, 'Screens & Settings › Browser remote'),
          ' and press ', el('b', {}, 'Pair a phone'), '. Type the code it shows.'),
        form,
        el('details', {}, el('summary', {}, 'Have a key instead?'), keyForm),
        message));
    code.focus();
  }

  return { api, image, live, pairing, key: current, forget, el, ApiError };
})();
