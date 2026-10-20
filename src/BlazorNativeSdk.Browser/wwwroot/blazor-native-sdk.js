// BlazorNativeSdk browser adapter. ES2022 module without dependencies.
//
// Every function called from .NET returns { ok: true, value } or { ok: false, name, message, ...hints } instead of
// throwing, so the .NET side can map DOMException names deterministically. Exceptions are reserved for invariant
// violations (for example taking a file handle twice).

const PREFIX = 'bns-sdk';
const STYLE_ID = `${PREFIX}-styles`;
const FILE_HANDLE_TTL_MS = 120_000;
const SCAN_INTERVAL_MS = 100;
const RECORDER_TYPES = ['video/mp4;codecs=avc1,mp4a', 'video/mp4', 'video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus', 'video/webm'];

const moduleState = { launchSeen: false };

// ---------------------------------------------------------------------------------------------------------------------
// Results and feature detection
// ---------------------------------------------------------------------------------------------------------------------

const ok = value => ({ ok: true, value: value === undefined ? null : value });

const fail = (name, message, extra) => ({ ok: false, name, message: message ?? '', ...(extra ?? {}) });

function fromError(error, extra) {
  if (error && typeof error === 'object' && 'name' in error) {
    return fail(String(error.name || 'Error'), String(error.message || ''), extra);
  }
  return fail('Error', String(error), extra);
}

const unavailableApi = (what) => window.isSecureContext
  ? fail('NotSupportedError', `${what} is not available in this browser.`)
  : fail('InsecureContextError', `${what} requires a secure context (HTTPS or localhost).`);

const userActivation = () => (navigator.userActivation ? navigator.userActivation.isActive : null);

const hasUserMedia = () => typeof navigator.mediaDevices?.getUserMedia === 'function';

const hasRecorder = () => typeof window.MediaRecorder === 'function';

function inCrossOriginFrame() {
  try {
    return window.top !== window.self && !window.top.location.href;
  } catch {
    return true;
  }
}

async function queryPermissionState(name) {
  try {
    if (typeof navigator.permissions?.query !== 'function') {
      return null;
    }
    const status = await navigator.permissions.query({ name });
    return status.state ?? null;
  } catch {
    return null; // The browser does not know this permission name.
  }
}

async function countVideoInputs() {
  try {
    const devices = await navigator.mediaDevices.enumerateDevices();
    return devices.filter(d => d.kind === 'videoinput').length;
  } catch {
    return null;
  }
}

function canShareFiles() {
  try {
    return typeof navigator.canShare === 'function' &&
      navigator.canShare({ files: [new File(['x'], 'x.txt', { type: 'text/plain' })] });
  } catch {
    return false;
  }
}

async function getServiceWorkerRegistration() {
  if (!('serviceWorker' in navigator)) {
    return null;
  }
  try {
    const registration = await navigator.serviceWorker.getRegistration();
    return registration?.active ? registration : null;
  } catch {
    return null;
  }
}

function stopStream(stream) {
  try {
    stream?.getTracks().forEach(track => track.stop());
  } catch {
    // Already stopped.
  }
}

async function openCameraStream(facing, withAudio) {
  const video = { width: { ideal: 1920 }, height: { ideal: 1080 } };
  if (facing) {
    video.facingMode = { ideal: facing };
  }
  if (!withAudio) {
    return navigator.mediaDevices.getUserMedia({ video, audio: false });
  }
  try {
    return await navigator.mediaDevices.getUserMedia({ video, audio: true });
  } catch (error) {
    // No usable microphone: record without sound rather than failing. Denials are reported as they are.
    if (['NotFoundError', 'NotReadableError', 'OverconstrainedError'].includes(error?.name)) {
      return navigator.mediaDevices.getUserMedia({ video, audio: false });
    }
    throw error;
  }
}

async function mediaFailure(error, permission) {
  const extra = { userActivation: null };
  if (error?.name === 'NotAllowedError') {
    extra.permissionState = await queryPermissionState(permission);
  }
  return fromError(error, extra);
}

function timestamp() {
  const d = new Date();
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}`;
}

function sanitizeData(data) {
  const result = {};
  if (data && typeof data === 'object') {
    for (const [key, value] of Object.entries(data)) {
      if (value !== undefined && value !== null) {
        result[key] = String(value);
      }
    }
  }
  return result;
}

function decodeNotificationClick(value) {
  try {
    const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const payload = JSON.parse(new TextDecoder().decode(bytes));
    if (payload && payload.type === 'bns-notification-click') {
      return { id: payload.id ?? null, action: payload.action || null, route: payload.route ?? null, data: sanitizeData(payload.data), reply: payload.reply ?? null };
    }
  } catch {
    // Not produced by blazor-native-sdk-sw.js.
  }
  return null;
}

// ---------------------------------------------------------------------------------------------------------------------
// Overlay (camera, recorder, scanner)
// ---------------------------------------------------------------------------------------------------------------------

const CSS = `
.${PREFIX}-overlay{position:fixed;inset:0;z-index:2147483000;display:flex;flex-direction:column;background:#000;color:#fff;
  font:16px/1.4 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;opacity:0;transition:opacity .15s ease-out}
.${PREFIX}-overlay[data-open]{opacity:1}
.${PREFIX}-header{display:flex;align-items:center;justify-content:center;min-height:48px;padding:max(8px,env(safe-area-inset-top)) 16px 8px}
.${PREFIX}-title{margin:0;font-size:1rem;font-weight:600}
.${PREFIX}-stage{position:relative;flex:1;min-height:0;display:flex;align-items:center;justify-content:center;overflow:hidden}
.${PREFIX}-video{width:100%;height:100%;object-fit:contain;background:#000}
.${PREFIX}-video[data-mirrored]{transform:scaleX(-1)}
.${PREFIX}-frame{position:absolute;width:min(70vw,70vh,360px);aspect-ratio:1;border:3px solid rgba(255,255,255,.9);border-radius:16px;
  box-shadow:0 0 0 100vmax rgba(0,0,0,.35);pointer-events:none}
.${PREFIX}-frame::after{content:"";position:absolute;left:8%;right:8%;top:50%;height:2px;background:#4ade80;animation:${PREFIX}-scan 2s ease-in-out infinite alternate}
@keyframes ${PREFIX}-scan{from{top:12%}to{top:88%}}
.${PREFIX}-status{min-height:1.4em;padding:8px 16px;text-align:center}
.${PREFIX}-timer{font-variant-numeric:tabular-nums}
.${PREFIX}-controls{display:flex;align-items:center;justify-content:space-around;gap:16px;padding:12px 24px max(20px,env(safe-area-inset-bottom))}
.${PREFIX}-btn{display:inline-flex;align-items:center;justify-content:center;width:56px;height:56px;padding:0;border:0;border-radius:50%;
  background:rgba(255,255,255,.16);color:#fff;cursor:pointer;transition:background-color .15s,transform .1s}
.${PREFIX}-btn:hover{background:rgba(255,255,255,.28)}
.${PREFIX}-btn:active{transform:scale(.94)}
.${PREFIX}-btn:focus-visible{outline:3px solid #60a5fa;outline-offset:3px}
.${PREFIX}-btn[hidden]{display:none}
.${PREFIX}-btn svg{width:28px;height:28px;fill:none;stroke:currentColor;stroke-width:2;stroke-linecap:round;stroke-linejoin:round}
.${PREFIX}-primary{width:76px;height:76px;background:#fff;border:4px solid rgba(255,255,255,.5);background-clip:padding-box}
.${PREFIX}-primary:hover{background:#e5e7eb}
.${PREFIX}-primary[data-mode="video"]{background:#ef4444}
.${PREFIX}-primary[data-recording]{border-radius:16px;background:#ef4444}
.${PREFIX}-spacer{width:56px;height:56px}
.${PREFIX}-visually-hidden,.${PREFIX}-hidden-input{position:fixed!important;top:0;left:0;width:1px!important;height:1px!important;padding:0!important;
  margin:-1px!important;overflow:hidden!important;clip:rect(0,0,0,0)!important;white-space:nowrap!important;border:0!important;opacity:0}
@media (prefers-reduced-motion:reduce){
  .${PREFIX}-overlay,.${PREFIX}-overlay *{transition:none!important;animation:none!important}
  .${PREFIX}-frame::after{top:50%}
}`;

const ICONS = {
  close: '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M6 6l12 12M18 6L6 18"/></svg>',
  switch: '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M20 7h-3l-2-3H9L7 7H4a1 1 0 0 0-1 1v11a1 1 0 0 0 1 1h16a1 1 0 0 0 1-1V8a1 1 0 0 0-1-1z"/><path d="M9 13a3 3 0 0 1 5.2-2M15 13a3 3 0 0 1-5.2 2M14.5 9.5V11H13M9.5 16.5V15H11"/></svg>',
};

function ensureStyles() {
  if (document.getElementById(STYLE_ID)) {
    return;
  }
  const style = document.createElement('style');
  style.id = STYLE_ID;
  style.textContent = CSS;
  (document.head ?? document.documentElement).appendChild(style);
}

let overlayCounter = 0;

class Overlay {
  #root;
  #previousFocus;
  #previousOverflow;
  #keydown;
  #liveRegion;
  #timer;
  primary = null;
  switchButton;
  cancelButton;
  video;
  onCancel = () => {};
  onSwitch = () => {};
  onPrimary = () => {};

  /** @param {{ title: string, status: string, mode: 'photo'|'video'|'scan', text: object }} config */
  constructor({ title, status, mode, text }) {
    ensureStyles();
    const titleId = `${PREFIX}-title-${++overlayCounter}`;
    const root = document.createElement('div');
    root.className = `${PREFIX}-overlay`;
    root.setAttribute('role', 'dialog');
    root.setAttribute('aria-modal', 'true');
    root.setAttribute('aria-labelledby', titleId);

    const header = document.createElement('div');
    header.className = `${PREFIX}-header`;
    const heading = document.createElement('h2');
    heading.className = `${PREFIX}-title`;
    heading.id = titleId;
    heading.textContent = title;
    header.append(heading);

    const stage = document.createElement('div');
    stage.className = `${PREFIX}-stage`;
    const video = document.createElement('video');
    video.className = `${PREFIX}-video`;
    video.muted = true;
    video.playsInline = true;
    video.autoplay = true;
    video.setAttribute('playsinline', '');
    video.setAttribute('aria-hidden', 'true');
    stage.append(video);
    if (mode === 'scan') {
      const frame = document.createElement('div');
      frame.className = `${PREFIX}-frame`;
      frame.setAttribute('aria-hidden', 'true');
      stage.append(frame);
    }

    const statusLine = document.createElement('div');
    statusLine.className = `${PREFIX}-status`;
    statusLine.setAttribute('role', 'status');
    statusLine.setAttribute('aria-live', 'polite');
    statusLine.textContent = status ?? '';
    this.#liveRegion = statusLine;

    const controls = document.createElement('div');
    controls.className = `${PREFIX}-controls`;
    this.cancelButton = this.#button(`${PREFIX}-btn`, text.cancel, ICONS.close, () => this.onCancel());
    controls.append(this.cancelButton);
    if (mode === 'scan') {
      const spacer = document.createElement('span');
      spacer.className = `${PREFIX}-spacer`;
      controls.append(spacer);
    } else {
      this.primary = this.#button(`${PREFIX}-btn ${PREFIX}-primary`, mode === 'video' ? text.startRecording : text.capture, '', () => this.onPrimary());
      this.primary.dataset.mode = mode;
      controls.append(this.primary);
    }
    this.switchButton = this.#button(`${PREFIX}-btn`, text.switchCamera, ICONS.switch, () => this.onSwitch());
    this.switchButton.hidden = true;
    controls.append(this.switchButton);

    root.append(header, stage, statusLine, controls);
    this.#root = root;
    this.video = video;

    this.#keydown = event => this.#onKeyDown(event);
  }

  show() {
    this.#previousFocus = document.activeElement;
    this.#previousOverflow = document.documentElement.style.overflow;
    document.documentElement.style.overflow = 'hidden';
    document.body.append(this.#root);
    document.addEventListener('keydown', this.#keydown, true);
    requestAnimationFrame(() => this.#root.setAttribute('data-open', ''));
    (this.primary ?? this.cancelButton).focus({ preventScroll: true });
  }

  attach(stream, mirrored) {
    this.video.srcObject = stream;
    this.video.toggleAttribute('data-mirrored', !!mirrored);
    const played = this.video.play();
    if (played && typeof played.catch === 'function') {
      played.catch(() => { /* Autoplay is allowed for muted video; ignore interrupted play() calls. */ });
    }
  }

  announce(message) {
    this.#liveRegion.textContent = message ?? '';
  }

  setTimer(seconds) {
    if (!this.#timer) {
      this.#timer = document.createElement('span');
      this.#timer.className = `${PREFIX}-timer`;
      this.#timer.setAttribute('aria-hidden', 'true');
      this.#liveRegion.after(this.#timer);
      this.#timer.style.textAlign = 'center';
      this.#timer.style.display = 'block';
    }
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    this.#timer.textContent = `${m}:${String(s).padStart(2, '0')}`;
  }

  close() {
    document.removeEventListener('keydown', this.#keydown, true);
    try {
      this.video.pause();
      this.video.srcObject = null;
    } catch {
      // Ignore.
    }
    this.#root.remove();
    document.documentElement.style.overflow = this.#previousOverflow ?? '';
    if (this.#previousFocus instanceof HTMLElement && this.#previousFocus.isConnected) {
      this.#previousFocus.focus({ preventScroll: true });
    }
  }

  #button(className, label, icon, onClick) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = className;
    button.setAttribute('aria-label', label);
    button.title = label;
    button.innerHTML = icon;
    button.addEventListener('click', event => {
      event.preventDefault();
      onClick();
    });
    return button;
  }

  #onKeyDown(event) {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.onCancel();
      return;
    }
    if (event.key !== 'Tab') {
      return;
    }
    const focusable = [...this.#root.querySelectorAll('button:not([hidden]):not([disabled])')];
    if (focusable.length === 0) {
      return;
    }
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement;
    if (!this.#root.contains(active)) {
      event.preventDefault();
      first.focus();
    } else if (event.shiftKey && active === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && active === last) {
      event.preventDefault();
      first.focus();
    }
  }
}

function grabFrame(video, maxDimension, quality) {
  const width = video.videoWidth;
  const height = video.videoHeight;
  if (!width || !height) {
    return Promise.reject(new DOMException('The camera has not delivered a frame yet.', 'InvalidStateError'));
  }
  const scale = maxDimension && Math.max(width, height) > maxDimension ? maxDimension / Math.max(width, height) : 1;
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.round(width * scale));
  canvas.height = Math.max(1, Math.round(height * scale));
  const context = canvas.getContext('2d');
  context.drawImage(video, 0, 0, canvas.width, canvas.height);
  return new Promise((resolve, reject) => canvas.toBlob(
    blob => (blob ? resolve(blob) : reject(new DOMException('The photo could not be encoded.', 'EncodingError'))),
    'image/jpeg',
    quality));
}

function pickRecorderType() {
  if (typeof MediaRecorder.isTypeSupported !== 'function') {
    return '';
  }
  return RECORDER_TYPES.find(type => MediaRecorder.isTypeSupported(type)) ?? '';
}

function toPickerTypes(types) {
  const result = [];
  for (const filter of types ?? []) {
    const extensions = (filter.extensions ?? []).filter(e => /^\.[A-Za-z0-9+.-]{1,15}$/.test(e));
    const mimes = (filter.mimeTypes ?? []).filter(m => /^[A-Za-z0-9.+-]+\/[A-Za-z0-9.+*-]+$/.test(m));
    if (extensions.length === 0 && mimes.length === 0) {
      continue;
    }
    const accept = {};
    for (const mime of mimes.length > 0 ? mimes : ['application/octet-stream']) {
      accept[mime] = extensions;
    }
    result.push({ description: filter.description ?? '', accept });
  }
  return result;
}

function toBarcodeDto(code) {
  const box = code.boundingBox;
  return {
    rawValue: code.rawValue ?? null,
    format: code.format,
    x: box ? box.x : null,
    y: box ? box.y : null,
    width: box ? box.width : null,
    height: box ? box.height : null,
  };
}

function execCommandCopy(text) {
  if (typeof document.execCommand !== 'function' || !document.body) {
    return false;
  }
  const active = document.activeElement;
  const selection = document.getSelection();
  const ranges = [];
  for (let i = 0; i < (selection?.rangeCount ?? 0); i++) {
    ranges.push(selection.getRangeAt(i));
  }
  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.setAttribute('readonly', '');
  textarea.setAttribute('aria-hidden', 'true');
  textarea.tabIndex = -1;
  textarea.className = `${PREFIX}-hidden-input`;
  textarea.style.fontSize = '12pt'; // Prevents iOS from zooming.
  ensureStyles();
  document.body.append(textarea);
  let copied = false;
  try {
    textarea.select();
    textarea.setSelectionRange(0, text.length);
    copied = document.execCommand('copy');
  } catch {
    copied = false;
  } finally {
    textarea.remove();
    if (selection) {
      selection.removeAllRanges();
      ranges.forEach(range => selection.addRange(range));
    }
    if (active instanceof HTMLElement) {
      active.focus({ preventScroll: true });
    }
  }
  return copied;
}

function requestNotificationPermission() {
  return new Promise((resolve, reject) => {
    try {
      // Old Safari only supports the callback form; current browsers return a promise.
      const promise = Notification.requestPermission(resolve);
      if (promise && typeof promise.then === 'function') {
        promise.then(resolve, reject);
      }
    } catch (error) {
      reject(error);
    }
  });
}

// ---------------------------------------------------------------------------------------------------------------------
// Best-effort secure storage: AES-GCM 256 with a non-extractable WebCrypto key, all persisted in IndexedDB.
// ---------------------------------------------------------------------------------------------------------------------

const secureDatabases = new Map();
const secureKeys = new Map();
const encoder = new TextEncoder();
const decoder = new TextDecoder();

const idbRequest = request => new Promise((resolve, reject) => {
  request.onsuccess = () => resolve(request.result);
  request.onerror = () => reject(request.error);
});

const idbDone = transaction => new Promise((resolve, reject) => {
  transaction.oncomplete = () => resolve();
  transaction.onerror = () => reject(transaction.error);
  transaction.onabort = () => reject(transaction.error ?? new DOMException('The storage transaction was aborted.', 'UnknownError'));
});

function openSecureDatabase(name) {
  let pending = secureDatabases.get(name);
  if (!pending) {
    pending = new Promise((resolve, reject) => {
      const request = indexedDB.open(name, 1);
      request.onupgradeneeded = () => {
        const db = request.result;
        if (!db.objectStoreNames.contains('keys')) {
          db.createObjectStore('keys');
        }
        if (!db.objectStoreNames.contains('values')) {
          db.createObjectStore('values');
        }
      };
      request.onsuccess = () => {
        const db = request.result;
        const forget = () => {
          secureDatabases.delete(name);
          secureKeys.delete(name);
        };
        db.onversionchange = () => {
          db.close();
          forget();
        };
        db.onclose = forget;
        resolve(db);
      };
      request.onerror = () => reject(request.error);
      request.onblocked = () => reject(new DOMException('The secure storage database is blocked by another tab.', 'InvalidStateError'));
    });
    secureDatabases.set(name, pending);
    pending.catch(() => secureDatabases.delete(name));
  }
  return pending;
}

function getSecureKey(name, db) {
  let pending = secureKeys.get(name);
  if (!pending) {
    pending = (async () => {
      const existing = await idbRequest(db.transaction('keys', 'readonly').objectStore('keys').get('master'));
      if (existing) {
        return existing;
      }
      const generated = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
      // Another tab may have stored a key meanwhile: read and add inside one readwrite transaction.
      const transaction = db.transaction('keys', 'readwrite');
      const store = transaction.objectStore('keys');
      let key = generated;
      const read = store.get('master');
      read.onsuccess = () => {
        if (read.result) {
          key = read.result;
        } else {
          store.add(generated, 'master');
        }
      };
      await idbDone(transaction);
      return key;
    })();
    secureKeys.set(name, pending);
    pending.catch(() => secureKeys.delete(name));
  }
  return pending;
}

function secureStorageUnavailable() {
  if (!window.isSecureContext || !globalThis.crypto?.subtle) {
    return fail('InsecureContextError', 'WebCrypto is only available in a secure context (HTTPS or localhost).');
  }
  if (typeof indexedDB === 'undefined' || indexedDB === null) {
    return fail('NotSupportedError', 'IndexedDB is not available.');
  }
  return null;
}

// ---------------------------------------------------------------------------------------------------------------------
// Session: one per .NET scope (the WebAssembly app or one Blazor Server circuit).
// ---------------------------------------------------------------------------------------------------------------------

class Session {
  #dotnet;
  #options;
  #operations = new Map();
  #files = new Map();
  #listeners = [];
  #pageNotifications = new Map();
  #fileCounter = 0;
  #disposed = false;
  #lifecycleStarted = false;
  #connectivityStarted = false;
  #onServiceWorkerMessage;

  constructor(dotnet, options) {
    this.#dotnet = dotnet;
    this.#options = options;

    this.#onServiceWorkerMessage = event => {
      const data = event.data;
      if (data && data.type === 'bns-notification-click') {
        this.#notificationClicked({ id: data.id ?? null, action: data.action || null, route: data.route ?? null, data: sanitizeData(data.data), reply: data.reply ?? null });
      }
    };
    if ('serviceWorker' in navigator) {
      navigator.serviceWorker.addEventListener('message', this.#onServiceWorkerMessage);
      navigator.serviceWorker.startMessages?.();
    }

    if (typeof window.launchQueue?.setConsumer === 'function') {
      window.launchQueue.setConsumer(params => {
        if (this.#disposed || !params?.targetURL) {
          return;
        }
        const isColdStart = !moduleState.launchSeen;
        moduleState.launchSeen = true;
        try {
          const target = new URL(params.targetURL, location.href);
          const route = target.origin === location.origin ? target.pathname + target.search + target.hash : null;
          this.#invoke('OnLaunch', target.href, route, isColdStart);
        } catch {
          // Malformed target URL.
        }
      });
    }
  }

  // ----- infrastructure ---------------------------------------------------------------------------------------------

  #invoke(method, ...args) {
    if (this.#disposed) {
      return;
    }
    try {
      this.#dotnet.invokeMethodAsync(method, ...args).catch(() => { /* The .NET side is gone. */ });
    } catch {
      // The .NET side is gone.
    }
  }

  #on(target, type, handler, options) {
    target.addEventListener(type, handler, options);
    this.#listeners.push(() => target.removeEventListener(type, handler, options));
  }

  #begin(operationId) {
    const operation = { id: operationId, controller: new AbortController(), activation: userActivation() };
    this.#operations.set(operationId, operation);
    return operation;
  }

  #end(operationId) {
    this.#operations.delete(operationId);
  }

  #register(operation, file, fallbackName) {
    const handle = `${operation.id}:${++this.#fileCounter}`;
    const timer = setTimeout(() => this.#files.delete(handle), FILE_HANDLE_TTL_MS);
    this.#files.set(handle, { blob: file, operationId: operation.id, timer });
    return {
      handle,
      name: file.name || fallbackName,
      type: file.type || null,
      size: file.size,
      lastModified: typeof file.lastModified === 'number' ? file.lastModified : null,
    };
  }

  #registerAll(operation, files, fallbackName) {
    return files.map(file => this.#register(operation, file, fallbackName));
  }

  /** Called by .NET when the caller cancels an operation; also releases files that were not taken. */
  abort(operationId) {
    this.#operations.get(operationId)?.controller.abort();
    for (const [handle, entry] of this.#files) {
      if (entry.operationId === operationId) {
        clearTimeout(entry.timer);
        this.#files.delete(handle);
      }
    }
  }

  /** Returns a Blob registered by an operation. .NET receives it as an IJSObjectReference. */
  takeFile(handle) {
    const entry = this.#files.get(handle);
    if (!entry) {
      throw new Error('The file is no longer available.');
    }
    clearTimeout(entry.timer);
    this.#files.delete(handle);
    return entry.blob;
  }

  /** Returns the Blob itself; .NET receives it as an IJSStreamReference. */
  blobStream(blob) {
    return blob;
  }

  dispose() {
    if (this.#disposed) {
      return;
    }
    this.#disposed = true;
    for (const operation of this.#operations.values()) {
      operation.controller.abort();
    }
    this.#operations.clear();
    for (const entry of this.#files.values()) {
      clearTimeout(entry.timer);
    }
    this.#files.clear();
    this.#listeners.forEach(remove => remove());
    this.#listeners = [];
    if ('serviceWorker' in navigator) {
      navigator.serviceWorker.removeEventListener('message', this.#onServiceWorkerMessage);
    }
  }

  // ----- feature detection ------------------------------------------------------------------------------------------

  async probe(section) {
    try {
      const probe = { secureContext: !!window.isSecureContext, userActivation: userActivation() };
      switch (section) {
        case 'camera':
        case 'barcode':
          probe.userMedia = hasUserMedia();
          probe.mediaRecorder = hasRecorder();
          if (probe.userMedia) {
            probe.cameraPermission = await queryPermissionState('camera');
            probe.videoInputs = await countVideoInputs();
          }
          if (section === 'barcode') {
            probe.barcodeDetector = typeof window.BarcodeDetector === 'function';
            if (probe.barcodeDetector) {
              try {
                probe.barcodeFormats = await window.BarcodeDetector.getSupportedFormats();
              } catch {
                probe.barcodeFormats = [];
              }
            }
          }
          break;
        case 'files':
          probe.openFilePicker = typeof window.showOpenFilePicker === 'function' && !inCrossOriginFrame();
          probe.saveFilePicker = typeof window.showSaveFilePicker === 'function' && !inCrossOriginFrame();
          break;
        case 'share':
          probe.share = typeof navigator.share === 'function';
          probe.canShareFiles = probe.share && canShareFiles();
          break;
        case 'clipboard':
          probe.clipboardRead = typeof navigator.clipboard?.readText === 'function';
          probe.clipboardWrite = typeof navigator.clipboard?.writeText === 'function';
          try {
            probe.execCommandCopy = typeof document.queryCommandSupported === 'function' && document.queryCommandSupported('copy');
          } catch {
            probe.execCommandCopy = false;
          }
          break;
        case 'permissions':
          probe.permissionsApi = typeof navigator.permissions?.query === 'function';
          break;
        case 'notifications':
          probe.notification = 'Notification' in window;
          if (probe.notification) {
            probe.notificationPermission = Notification.permission;
            probe.maxNotificationActions = typeof Notification.maxActions === 'number' ? Notification.maxActions : 0;
          }
          probe.serviceWorkerRegistration = !!(await getServiceWorkerRegistration());
          probe.badging = typeof navigator.setAppBadge === 'function';
          break;
        case 'secure-storage':
          probe.indexedDb = typeof indexedDB !== 'undefined' && indexedDB !== null;
          probe.subtleCrypto = !!globalThis.crypto?.subtle;
          break;
        case 'protocol':
          probe.protocolHandler = typeof navigator.registerProtocolHandler === 'function';
          break;
        default:
          break;
      }
      return ok(probe);
    } catch (error) {
      return fromError(error);
    }
  }

  // ----- camera -----------------------------------------------------------------------------------------------------

  async capture(operationId, json) {
    const request = JSON.parse(json);
    const operation = this.#begin(operationId);
    try {
      const fallbackName = request.video ? `video-${timestamp()}.webm` : `photo-${timestamp()}.jpg`;
      if (!hasUserMedia() || (request.video && !hasRecorder())) {
        const files = await this.#chooseWithInput(operation, {
          accept: request.accept,
          multiple: false,
          capture: request.facing === 'user' ? 'user' : 'environment',
        });
        return files.ok ? ok(this.#registerAll(operation, files.value.slice(0, 1), fallbackName)) : files;
      }
      const result = await this.#captureWithOverlay(operation, request);
      return result.ok ? ok([this.#register(operation, result.value, fallbackName)]) : result;
    } catch (error) {
      return fromError(error);
    } finally {
      this.#end(operationId);
    }
  }

  async #captureWithOverlay(operation, request) {
    const signal = operation.controller.signal;
    let facing = request.facing ?? null;
    let stream;
    try {
      stream = await openCameraStream(facing, request.video);
    } catch (error) {
      return mediaFailure(error, 'camera');
    }
    if (signal.aborted) {
      stopStream(stream);
      return fail('AbortError', 'The operation was cancelled.');
    }

    const text = this.#options.text;
    const overlay = new Overlay({ title: request.title || text.cameraTitle, status: '', mode: request.video ? 'video' : 'photo', text });
    let recorder = null;
    let durationTimer = 0;
    let tickTimer = 0;
    try {
      overlay.attach(stream, facing === 'user');
      overlay.show();
      overlay.switchButton.hidden = ((await countVideoInputs()) ?? 0) < 2;

      return await new Promise(resolve => {
        let settled = false;
        const finish = result => {
          if (!settled) {
            settled = true;
            resolve(result);
          }
        };
        const watchTracks = current => current.getVideoTracks().forEach(track => track.addEventListener(
          'ended',
          () => {
            if (current === stream) {
              finish(fail('NotReadableError', 'The camera stopped delivering video.'));
            }
          },
          { once: true }));
        watchTracks(stream);

        overlay.onCancel = () => finish(fail('AbortError', 'The user cancelled the capture.'));
        signal.addEventListener('abort', () => finish(fail('AbortError', 'The operation was cancelled.')), { once: true });

        overlay.onSwitch = async () => {
          if (recorder) {
            return;
          }
          overlay.switchButton.disabled = true;
          const next = facing === 'user' ? 'environment' : 'user';
          const previous = stream;
          stream = null;
          stopStream(previous);
          try {
            stream = await openCameraStream(next, request.video);
            facing = next;
            if (settled) {
              stopStream(stream);
              return;
            }
            watchTracks(stream);
            overlay.attach(stream, facing === 'user');
          } catch (error) {
            finish(await mediaFailure(error, 'camera'));
          } finally {
            overlay.switchButton.disabled = false;
          }
        };

        if (!request.video) {
          overlay.onPrimary = async () => {
            overlay.primary.disabled = true;
            try {
              const blob = await grabFrame(overlay.video, request.maxDimension, request.quality ?? 0.9);
              finish(ok(new File([blob], `photo-${timestamp()}.jpg`, { type: 'image/jpeg', lastModified: Date.now() })));
            } catch (error) {
              overlay.primary.disabled = false;
              if (error?.name !== 'InvalidStateError') {
                finish(fromError(error));
              }
            }
          };
          return;
        }

        overlay.onPrimary = () => {
          if (recorder) {
            if (recorder.state !== 'inactive') {
              overlay.primary.disabled = true;
              recorder.stop();
            }
            return;
          }
          if (!stream) {
            return;
          }
          const mimeType = pickRecorderType();
          const chunks = [];
          try {
            recorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined);
          } catch (error) {
            finish(fromError(error));
            return;
          }
          recorder.ondataavailable = event => {
            if (event.data && event.data.size > 0) {
              chunks.push(event.data);
            }
          };
          recorder.onerror = event => finish(fromError(event.error ?? event));
          recorder.onstop = () => {
            const type = (recorder.mimeType || mimeType || 'video/webm').split(';')[0];
            const extension = type.includes('mp4') ? 'mp4' : 'webm';
            finish(ok(new File(chunks, `video-${timestamp()}.${extension}`, { type, lastModified: Date.now() })));
          };
          recorder.start(1000);
          overlay.switchButton.hidden = true;
          overlay.primary.setAttribute('data-recording', '');
          overlay.primary.setAttribute('aria-label', text.stopRecording);
          overlay.primary.title = text.stopRecording;
          overlay.announce(text.stopRecording);
          const started = performance.now();
          overlay.setTimer(0);
          tickTimer = setInterval(() => overlay.setTimer((performance.now() - started) / 1000), 500);
          if (request.maxDurationMs > 0) {
            durationTimer = setTimeout(() => {
              if (recorder?.state === 'recording') {
                recorder.stop();
              }
            }, request.maxDurationMs);
          }
        };
      });
    } finally {
      clearTimeout(durationTimer);
      clearInterval(tickTimer);
      if (recorder && recorder.state !== 'inactive') {
        recorder.onstop = null;
        try {
          recorder.stop();
        } catch {
          // Already stopped.
        }
      }
      stopStream(stream);
      overlay.close();
    }
  }

  /** Shows a hidden file input. Resolves { ok, value: File[] } ([] = dismissed) or an error envelope. */
  #chooseWithInput(operation, { accept, multiple, capture }) {
    if (operation.activation === false) {
      return Promise.resolve(fail('UserGestureRequiredError', 'Opening a file chooser requires a user gesture.'));
    }
    ensureStyles();
    const input = document.createElement('input');
    input.type = 'file';
    input.className = `${PREFIX}-hidden-input`;
    input.tabIndex = -1;
    input.setAttribute('aria-hidden', 'true');
    if (accept) {
      input.accept = accept;
    }
    input.multiple = !!multiple;
    if (capture) {
      input.setAttribute('capture', capture);
    }
    document.body.append(input);

    return new Promise(resolve => {
      const signal = operation.controller.signal;
      let settled = false;
      let focusTimer = 0;
      const onFocus = () => {
        // Fallback for browsers without the 'cancel' event: the window regains focus when the dialog closes.
        // 'change' can arrive slightly after 'focus', so wait before concluding that the user dismissed the dialog.
        clearTimeout(focusTimer);
        focusTimer = setTimeout(() => {
          if (!input.files || input.files.length === 0) {
            finish(ok([]));
          }
        }, 1000);
      };
      const onAbort = () => finish(fail('AbortError', 'The operation was cancelled.'));
      const finish = result => {
        if (settled) {
          return;
        }
        settled = true;
        clearTimeout(focusTimer);
        window.removeEventListener('focus', onFocus);
        signal.removeEventListener('abort', onAbort);
        input.remove();
        resolve(result);
      };
      input.addEventListener('change', () => finish(ok([...(input.files ?? [])])), { once: true });
      input.addEventListener('cancel', () => finish(ok([])), { once: true });
      signal.addEventListener('abort', onAbort, { once: true });
      try {
        input.click();
      } catch (error) {
        finish(fromError(error));
        return;
      }
      if (!('oncancel' in input)) {
        setTimeout(() => window.addEventListener('focus', onFocus), 0);
      }
    });
  }

  // ----- files ------------------------------------------------------------------------------------------------------

  async pickFiles(operationId, json) {
    const request = JSON.parse(json);
    const operation = this.#begin(operationId);
    try {
      const types = toPickerTypes(request.types);
      if (request.useFileSystemAccess && typeof window.showOpenFilePicker === 'function' && !inCrossOriginFrame()) {
        try {
          const handles = await window.showOpenFilePicker({ multiple: !!request.multiple, types, excludeAcceptAllOption: types.length > 0 });
          const files = await Promise.all(handles.map(handle => handle.getFile()));
          if (operation.controller.signal.aborted) {
            return fail('AbortError', 'The operation was cancelled.');
          }
          return ok(this.#registerAll(operation, files, 'file'));
        } catch (error) {
          if (error?.name === 'AbortError') {
            return fail('AbortError', 'The user dismissed the file picker.');
          }
          if (error?.name === 'SecurityError' && userActivation() === false) {
            return fail('UserGestureRequiredError', 'Opening a file picker requires a user gesture.');
          }
          if (error?.name !== 'SecurityError' && error?.name !== 'TypeError') {
            return fromError(error);
          }
          // SecurityError (frame restrictions) or TypeError (filters the API rejects): use the file input instead.
        }
      }
      const files = await this.#chooseWithInput(operation, { accept: request.accept, multiple: request.multiple });
      return files.ok ? ok(this.#registerAll(operation, files.value, 'file')) : files;
    } catch (error) {
      return fromError(error);
    } finally {
      this.#end(operationId);
    }
  }

  async saveFile(operationId, json, content) {
    const request = JSON.parse(json);
    const operation = this.#begin(operationId);
    try {
      if (request.useFileSystemAccess && typeof window.showSaveFilePicker === 'function' && !inCrossOriginFrame()) {
        let handle = null;
        try {
          const types = toPickerTypes(request.types);
          handle = await window.showSaveFilePicker({ suggestedName: request.suggestedName, ...(types.length > 0 ? { types } : {}) });
        } catch (error) {
          if (error?.name === 'AbortError') {
            return fail('AbortError', 'The user dismissed the save dialog.');
          }
          if (error?.name === 'SecurityError' && userActivation() === false) {
            return fail('UserGestureRequiredError', 'Opening a save dialog requires a user gesture.');
          }
          if (error?.name !== 'SecurityError' && error?.name !== 'TypeError') {
            return fromError(error);
          }
        }
        if (handle) {
          let writable = null;
          try {
            // The content is streamed from .NET only now, after the user chose the location.
            writable = await handle.createWritable();
            const readable = await content.stream();
            await readable.pipeTo(writable, { signal: operation.controller.signal });
            return ok({ method: 'dialog', name: handle.name });
          } catch (error) {
            try {
              await writable?.abort();
            } catch {
              // Already closed by pipeTo.
            }
            return fromError(error);
          }
        }
      }

      const buffer = await content.arrayBuffer();
      if (operation.controller.signal.aborted) {
        return fail('AbortError', 'The operation was cancelled.');
      }
      const url = URL.createObjectURL(new Blob([buffer], { type: request.contentType || 'application/octet-stream' }));
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = request.suggestedName;
      anchor.rel = 'noopener';
      anchor.style.display = 'none';
      document.body.append(anchor);
      anchor.click();
      anchor.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
      return ok({ method: 'download', name: request.suggestedName });
    } catch (error) {
      return fromError(error);
    } finally {
      this.#end(operationId);
    }
  }

  // ----- barcode ----------------------------------------------------------------------------------------------------

  async scanBarcode(operationId, json) {
    const request = JSON.parse(json);
    const operation = this.#begin(operationId);
    const signal = operation.controller.signal;
    let stream = null;
    let overlay = null;
    let scanTimer = 0;
    let timeoutTimer = 0;
    try {
      if (typeof window.BarcodeDetector !== 'function') {
        return fail('NotSupportedError', 'BarcodeDetector is not available in this browser.');
      }
      if (!hasUserMedia()) {
        return unavailableApi('Camera access (getUserMedia)');
      }
      const detector = new window.BarcodeDetector({ formats: request.formats });
      try {
        stream = await openCameraStream(request.facing ?? null, false);
      } catch (error) {
        return mediaFailure(error, 'camera');
      }
      if (signal.aborted) {
        return fail('AbortError', 'The operation was cancelled.');
      }

      const text = this.#options.text;
      overlay = new Overlay({ title: text.scannerTitle, status: request.prompt || text.scannerPrompt, mode: 'scan', text });
      overlay.attach(stream, request.facing === 'user');
      overlay.show();

      return await new Promise(resolve => {
        let settled = false;
        const finish = result => {
          if (!settled) {
            settled = true;
            resolve(result);
          }
        };
        overlay.onCancel = () => finish(fail('AbortError', 'The user cancelled scanning.'));
        signal.addEventListener('abort', () => finish(fail('AbortError', 'The operation was cancelled.')), { once: true });
        stream.getVideoTracks().forEach(track => track.addEventListener('ended', () => finish(fail('NotReadableError', 'The camera stopped delivering video.')), { once: true }));
        if (request.timeoutMs > 0) {
          timeoutTimer = setTimeout(() => finish(fail('TimeoutError', 'No barcode was detected before the timeout.')), request.timeoutMs);
        }
        const tick = async () => {
          if (settled) {
            return;
          }
          if (overlay.video.readyState >= 2) {
            try {
              const codes = await detector.detect(overlay.video);
              if (codes.length > 0) {
                finish(ok(toBarcodeDto(codes[0])));
                return;
              }
            } catch (error) {
              if (error?.name === 'NotSupportedError') {
                finish(fromError(error));
                return;
              }
              // Transient failures (frame not ready) are retried on the next tick.
            }
          }
          if (!settled) {
            scanTimer = setTimeout(tick, SCAN_INTERVAL_MS);
          }
        };
        scanTimer = setTimeout(tick, SCAN_INTERVAL_MS);
      });
    } catch (error) {
      return fromError(error);
    } finally {
      clearTimeout(scanTimer);
      clearTimeout(timeoutTimer);
      stopStream(stream);
      overlay?.close();
      this.#end(operationId);
    }
  }

  async detectBarcodes(operationId, json, source) {
    const request = JSON.parse(json);
    this.#begin(operationId);
    let bitmap = null;
    try {
      if (typeof window.BarcodeDetector !== 'function') {
        return fail('NotSupportedError', 'BarcodeDetector is not available in this browser.');
      }
      const blob = source instanceof Blob
        ? source
        : new Blob([await source.arrayBuffer()], { type: request.contentType || '' });
      try {
        bitmap = await createImageBitmap(blob);
      } catch {
        return fail('DataError', 'The image could not be decoded by the browser.');
      }
      const detector = new window.BarcodeDetector({ formats: request.formats });
      const codes = await detector.detect(bitmap);
      return ok(codes.map(toBarcodeDto));
    } catch (error) {
      return fromError(error);
    } finally {
      bitmap?.close();
      this.#end(operationId);
    }
  }

  // ----- share ------------------------------------------------------------------------------------------------------

  async share(operationId, json, ...sources) {
    const request = JSON.parse(json);
    const operation = this.#begin(operationId);
    try {
      if (typeof navigator.share !== 'function') {
        return unavailableApi('The Web Share API');
      }
      if (operation.activation === false) {
        return fail('NotAllowedError', 'navigator.share requires a user gesture.', { userActivation: false });
      }
      const data = {};
      if (request.title) {
        data.title = request.title;
      }
      if (request.text) {
        data.text = request.text;
      }
      if (request.url) {
        data.url = request.url;
      }
      if (request.files.length > 0) {
        const files = [];
        for (let i = 0; i < request.files.length; i++) {
          const meta = request.files[i];
          const source = sources[i];
          if (meta.kind === 'blob') {
            files.push(source instanceof File && source.name === meta.name
              ? source
              : new File([source], meta.name, { type: meta.type || source.type || '' }));
          } else {
            files.push(new File([await source.arrayBuffer()], meta.name, { type: meta.type || '' }));
          }
        }
        if (typeof navigator.canShare !== 'function' || !navigator.canShare({ files })) {
          return fail('NotSupportedError', 'This browser cannot share these files.');
        }
        data.files = files;
      }
      if (typeof navigator.canShare === 'function' && !navigator.canShare(data)) {
        return fail('TypeError', 'The browser rejected the share data.');
      }
      if (operation.controller.signal.aborted) {
        return fail('AbortError', 'The operation was cancelled.');
      }
      try {
        await navigator.share(data);
        return ok(null);
      } catch (error) {
        return fromError(error, error?.name === 'NotAllowedError' ? { userActivation: userActivation() } : undefined);
      }
    } catch (error) {
      return fromError(error);
    } finally {
      this.#end(operationId);
    }
  }

  // ----- clipboard --------------------------------------------------------------------------------------------------

  async clipboardRead(operationId) {
    this.#begin(operationId);
    try {
      if (typeof navigator.clipboard?.readText !== 'function') {
        return unavailableApi('navigator.clipboard.readText');
      }
      try {
        return ok(await navigator.clipboard.readText());
      } catch (error) {
        const state = await queryPermissionState('clipboard-read');
        // A denied permission is reported as such; otherwise a missing gesture is the likely cause.
        return fromError(error, { permissionState: state, userActivation: state === 'denied' ? null : userActivation() });
      }
    } finally {
      this.#end(operationId);
    }
  }

  async clipboardWrite(text) {
    const activation = userActivation();
    let firstError = null;
    if (typeof navigator.clipboard?.writeText === 'function') {
      try {
        await navigator.clipboard.writeText(text);
        return ok(null);
      } catch (error) {
        firstError = error;
      }
    }
    if (execCommandCopy(text)) {
      return ok(null);
    }
    return firstError
      ? fromError(firstError, { userActivation: activation })
      : unavailableApi('The Clipboard API');
  }

  // ----- permissions ------------------------------------------------------------------------------------------------

  async permissionQuery(name) {
    try {
      if (name === 'notifications' && 'Notification' in window) {
        return ok(Notification.permission === 'default' ? 'prompt' : Notification.permission);
      }
      return ok(await queryPermissionState(name));
    } catch (error) {
      return fromError(error);
    }
  }

  async permissionRequest(operationId, name) {
    this.#begin(operationId);
    try {
      switch (name) {
        case 'camera':
        case 'microphone': {
          if (!hasUserMedia()) {
            return unavailableApi('Media capture (getUserMedia)');
          }
          try {
            const stream = await navigator.mediaDevices.getUserMedia(name === 'camera' ? { video: true } : { audio: true });
            stopStream(stream);
            return ok('granted');
          } catch (error) {
            if (error?.name === 'NotAllowedError') {
              // A dismissed prompt leaves the state at 'prompt'; a denial makes it 'denied'.
              const state = await queryPermissionState(name);
              return ok(state === 'prompt' ? 'prompt' : 'denied');
            }
            return fromError(error);
          }
        }
        case 'notifications': {
          if (!('Notification' in window)) {
            return unavailableApi('The Notifications API');
          }
          const result = await requestNotificationPermission();
          return ok(result === 'default' ? 'prompt' : result);
        }
        default:
          return fail('NotSupportedError', `The browser has no '${name}' permission that can be requested.`);
      }
    } catch (error) {
      return fromError(error);
    } finally {
      this.#end(operationId);
    }
  }

  // ----- notifications ----------------------------------------------------------------------------------------------

  #notificationClicked(click) {
    this.#invoke('OnNotificationClick', JSON.stringify(click));
  }

  async showNotification(json) {
    try {
      const request = JSON.parse(json);
      if (!('Notification' in window)) {
        return unavailableApi('The Notifications API');
      }
      if (Notification.permission !== 'granted') {
        return fail('NotAllowedError', 'Notification permission has not been granted.', {
          permissionState: Notification.permission === 'default' ? 'prompt' : Notification.permission,
        });
      }
      if (typeof request.badge === 'number') {
        const badge = request.badge > 0 ? navigator.setAppBadge?.(request.badge) : navigator.clearAppBadge?.();
        badge?.catch?.(() => { /* Badging is best effort. */ });
      }
      const data = { id: request.id, route: request.route ?? null, data: request.data ?? {} };
      const options = {
        body: request.body ?? '',
        tag: request.id,
        data: { bns: data },
        silent: !!request.silent,
        requireInteraction: !!request.requireInteraction,
      };

      const registration = this.#options.useServiceWorkerNotifications ? await getServiceWorkerRegistration() : null;
      if (registration) {
        const maxActions = typeof Notification.maxActions === 'number' ? Notification.maxActions : 0;
        if (request.actions.length > 0 && maxActions > 0) {
          options.actions = request.actions.slice(0, maxActions).map(a => ({ action: a.action, title: a.title }));
        }
        await registration.showNotification(request.title, options);
        return ok({ via: 'service-worker', actionsShown: request.actions.length === 0 || !!options.actions });
      }

      let notification;
      try {
        notification = new Notification(request.title, options);
      } catch (error) {
        if (error?.name === 'TypeError') {
          return fail('NotSupportedError', 'This browser shows notifications only from a service worker (for example Chrome on Android). Register a service worker that imports blazor-native-sdk-sw.js.');
        }
        throw error;
      }
      this.#pageNotifications.get(request.id)?.close();
      this.#pageNotifications.set(request.id, notification);
      notification.onclick = event => {
        event.preventDefault?.();
        try {
          window.focus();
        } catch {
          // Focus is best effort.
        }
        notification.close();
        this.#pageNotifications.delete(request.id);
        this.#notificationClicked({ id: data.id, action: null, route: data.route, data: sanitizeData(data.data), reply: null });
      };
      notification.onclose = () => {
        if (this.#pageNotifications.get(request.id) === notification) {
          this.#pageNotifications.delete(request.id);
        }
      };
      return ok({ via: 'page', actionsShown: request.actions.length === 0 });
    } catch (error) {
      return fromError(error);
    }
  }

  async closeNotifications(id) {
    try {
      for (const [key, notification] of this.#pageNotifications) {
        if (id == null || key === id) {
          notification.close();
          this.#pageNotifications.delete(key);
        }
      }
      const registration = await getServiceWorkerRegistration();
      if (typeof registration?.getNotifications === 'function') {
        const shown = await registration.getNotifications(id == null ? undefined : { tag: id });
        shown.filter(n => n.data && n.data.bns).forEach(n => n.close());
      }
      return ok(null);
    } catch (error) {
      return fromError(error);
    }
  }

  // ----- secure storage ---------------------------------------------------------------------------------------------

  async secureGet(database, key) {
    const unavailable = secureStorageUnavailable();
    if (unavailable) {
      return unavailable;
    }
    try {
      const db = await openSecureDatabase(database);
      const record = await idbRequest(db.transaction('values', 'readonly').objectStore('values').get(key));
      if (!record) {
        return ok(null);
      }
      const cryptoKey = await getSecureKey(database, db);
      let plain;
      try {
        plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: record.iv, additionalData: encoder.encode(key) }, cryptoKey, record.data);
      } catch {
        return fail('OperationError', 'The stored value could not be decrypted (the key was replaced or the data is corrupted).');
      }
      return ok(decoder.decode(plain));
    } catch (error) {
      return fromError(error);
    }
  }

  async secureSet(database, key, value) {
    const unavailable = secureStorageUnavailable();
    if (unavailable) {
      return unavailable;
    }
    try {
      const db = await openSecureDatabase(database);
      const cryptoKey = await getSecureKey(database, db);
      const iv = crypto.getRandomValues(new Uint8Array(12));
      const data = await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: encoder.encode(key) }, cryptoKey, encoder.encode(value));
      const transaction = db.transaction('values', 'readwrite');
      transaction.objectStore('values').put({ v: 1, iv, data }, key);
      await idbDone(transaction);
      return ok(null);
    } catch (error) {
      return fromError(error);
    }
  }

  async secureRemove(database, key) {
    const unavailable = secureStorageUnavailable();
    if (unavailable) {
      return unavailable;
    }
    try {
      const db = await openSecureDatabase(database);
      const transaction = db.transaction('values', 'readwrite');
      const store = transaction.objectStore('values');
      let existed = false;
      const count = store.count(key);
      count.onsuccess = () => {
        existed = count.result > 0;
        if (existed) {
          store.delete(key);
        }
      };
      await idbDone(transaction);
      return ok(existed);
    } catch (error) {
      return fromError(error);
    }
  }

  async secureClear(database) {
    const unavailable = secureStorageUnavailable();
    if (unavailable) {
      return unavailable;
    }
    try {
      const db = await openSecureDatabase(database);
      const transaction = db.transaction('values', 'readwrite');
      transaction.objectStore('values').clear();
      await idbDone(transaction);
      return ok(null);
    } catch (error) {
      return fromError(error);
    }
  }

  // ----- state store (localStorage) ---------------------------------------------------------------------------------

  stateGet(key) {
    try {
      return ok(localStorage.getItem(key));
    } catch (error) {
      return fromError(error);
    }
  }

  stateSet(key, value) {
    try {
      localStorage.setItem(key, value);
      return ok(null);
    } catch (error) {
      return fromError(error);
    }
  }

  stateRemove(key) {
    try {
      const existed = localStorage.getItem(key) !== null;
      localStorage.removeItem(key);
      return ok(existed);
    } catch (error) {
      return fromError(error);
    }
  }

  stateKeys(prefix) {
    try {
      const keys = [];
      for (let i = 0; i < localStorage.length; i++) {
        const key = localStorage.key(i);
        if (key !== null && key.startsWith(prefix)) {
          keys.push(key);
        }
      }
      return ok(keys);
    } catch (error) {
      return fromError(error);
    }
  }

  // ----- lifecycle and connectivity ---------------------------------------------------------------------------------

  startLifecycle() {
    const current = () => {
      if (document.visibilityState === 'hidden') {
        return 'background';
      }
      return document.hasFocus() ? 'active' : 'inactive';
    };
    if (!this.#lifecycleStarted) {
      this.#lifecycleStarted = true;
      const report = reason => this.#invoke('OnLifecycle', current(), reason);
      this.#on(document, 'visibilitychange', () => report('visibilitychange'));
      this.#on(window, 'focus', () => report('focus'));
      this.#on(window, 'blur', () => report('blur'));
      this.#on(window, 'pagehide', event => this.#invoke('OnLifecycle', 'terminating', event.persisted ? 'pagehide-bfcache' : 'pagehide'));
      this.#on(window, 'pageshow', event => {
        if (event.persisted) {
          report('pageshow');
        }
      });
      this.#on(document, 'freeze', () => this.#invoke('OnLifecycle', 'background', 'freeze'));
      this.#on(document, 'resume', () => report('resume'));
    }
    return ok(current());
  }

  startConnectivity() {
    const snapshot = () => {
      const connection = navigator.connection;
      return {
        onLine: navigator.onLine !== false,
        type: typeof connection?.type === 'string' ? connection.type : null,
        saveData: typeof connection?.saveData === 'boolean' ? connection.saveData : null,
      };
    };
    if (!this.#connectivityStarted) {
      this.#connectivityStarted = true;
      const report = () => this.#invoke('OnConnectivity', JSON.stringify(snapshot()));
      this.#on(window, 'online', report);
      this.#on(window, 'offline', report);
      if (typeof navigator.connection?.addEventListener === 'function') {
        this.#on(navigator.connection, 'change', report);
      }
    }
    return ok(snapshot());
  }

  // ----- activation -------------------------------------------------------------------------------------------------

  /** Reads (and removes from the address bar) the protocol-handler link and service-worker notification tap. */
  launchInfo() {
    try {
      const url = new URL(location.href);
      let link = null;
      let notificationClick = null;
      let changed = false;
      const linkParameter = this.#options.protocolParameter;
      if (url.searchParams.has(linkParameter)) {
        link = url.searchParams.get(linkParameter);
        url.searchParams.delete(linkParameter);
        changed = true;
      }
      const clickParameter = this.#options.notificationParameter;
      if (url.searchParams.has(clickParameter)) {
        notificationClick = decodeNotificationClick(url.searchParams.get(clickParameter) ?? '');
        url.searchParams.delete(clickParameter);
        changed = true;
      }
      if (changed) {
        try {
          history.replaceState(history.state, '', url.href);
        } catch {
          // Sandboxed documents may not change history.
        }
      }
      return ok({ link, notificationClick });
    } catch (error) {
      return fromError(error);
    }
  }

  registerProtocolHandler(scheme, parameter) {
    if (typeof navigator.registerProtocolHandler !== 'function') {
      return fail('NotSupportedError', 'navigator.registerProtocolHandler is not available in this browser.');
    }
    try {
      const base = document.baseURI.split('#')[0].split('?')[0];
      navigator.registerProtocolHandler(scheme, `${base}?${encodeURIComponent(parameter)}=%s`);
      return ok(null);
    } catch (error) {
      return fromError(error);
    }
  }
}

/**
 * Creates the session used by one .NET scope.
 * @param {any} dotnet DotNetObjectReference receiving lifecycle, connectivity, notification and launch callbacks.
 * @param {string} optionsJson Serialized session options.
 */
export function createSession(dotnet, optionsJson) {
  return new Session(dotnet, JSON.parse(optionsJson));
}
