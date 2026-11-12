// MetroDom remote: a thin client of the Control API v0 (docs/references/control-api.md).
// State comes from the /stream WebSocket; buttons POST intents. No framework, no build step.
"use strict";

(() => {
  const API = new URL("api/v0/", location.href);
  const TOKEN_KEY = "metrodom.token";
  const NUDGE = 1.02;
  const MIN_BPM = 30;
  const MAX_BPM = 300;
  const $ = (id) => document.getElementById(id);

  let token = null;
  let socket = null;
  let retry = 0;
  let state = null;
  let duration = 0;
  const messages = [];

  function storage(action, value) {
    try {
      if (action === "get") return localStorage.getItem(TOKEN_KEY);
      if (action === "set") localStorage.setItem(TOKEN_KEY, value);
      if (action === "clear") localStorage.removeItem(TOKEN_KEY);
    } catch {
      // Private mode or blocked storage: the token lives for this page only.
    }
    return null;
  }

  // Pairing: the Conductor prints http://host:port/#token=…; the fragment never reaches a server.
  function takeTokenFromUrl() {
    const params = new URLSearchParams(location.hash.slice(1));
    const fromUrl = params.get("token");
    if (fromUrl) {
      storage("set", fromUrl);
      history.replaceState(null, "", location.pathname + location.search);
      return fromUrl;
    }
    return null;
  }

  function showPairing(error) {
    if (socket) socket.close();
    socket = null;
    $("band").hidden = true;
    $("pair").hidden = false;
    $("pair-error").textContent = error || "";
    $("token").focus();
  }

  // A token the Conductor refused is useless: drop it so a reload doesn't retry it.
  function reject() {
    storage("clear");
    token = null;
    showPairing("That token didn't work. Paste the one the Conductor shows.");
  }

  function showBand() {
    $("pair").hidden = true;
    $("band").hidden = false;
  }

  async function call(path, body) {
    const response = await fetch(new URL(path, API), {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, ...(body ? { "Content-Type": "application/json" } : {}) },
      body: body ? JSON.stringify(body) : undefined,
      cache: "no-store",
    });
    if (response.status === 401) {
      reject();
      return;
    }
    const reply = await response.json().catch(() => ({}));
    if (reply.message) note(reply.message);
    else if (reply.error) note(reply.error);
  }

  async function tokenWorks() {
    try {
      const response = await fetch(new URL("session", API), { headers: { Authorization: `Bearer ${token}` }, cache: "no-store" });
      return response.status !== 401;
    } catch {
      return true; // Offline, not unauthorized: keep retrying.
    }
  }

  // What this token may do under the policy. The Conductor enforces it; this only hides buttons.
  async function loadSession() {
    let response;
    try {
      response = await fetch(new URL("session", API), { headers: { Authorization: `Bearer ${token}` }, cache: "no-store" });
    } catch {
      return true; // Offline: show everything, the stream will retry.
    }
    if (response.status === 401) {
      reject();
      return false;
    }
    if (!response.ok) return true;
    const session = await response.json();
    const grants = new Set(session.grants);
    document.querySelectorAll("[data-grant]").forEach((element) => {
      element.hidden = !grants.has(element.dataset.grant);
    });
    const until = session.expiresAt ? ` · until ${new Date(session.expiresAt).toLocaleString([], { weekday: "short", hour: "2-digit", minute: "2-digit" })}` : "";
    $("role").textContent = `${session.label ? `${session.label} · ` : ""}${session.role} · ${session.mode}${until}`;
    if (!grants.has("view")) {
      $("status").textContent = `This ${session.role} link can't see the band under the ${session.mode} policy.`;
      return false;
    }
    return true;
  }

  function connect() {
    const url = new URL("stream", API);
    url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
    let opened = false;
    socket = new WebSocket(url, ["metrodom.v0", `token.${token}`]);
    socket.onopen = () => {
      opened = true;
      retry = 0;
      link("ok", "Connected");
    };
    socket.onmessage = (event) => handle(JSON.parse(event.data));
    socket.onclose = async (event) => {
      socket = null;
      link("down", event.reason || "Disconnected");
      if (!opened && !(await tokenWorks())) {
        reject();
        return;
      }
      if ($("band").hidden) return;
      retry = Math.min(retry + 1, 5);
      setTimeout(() => { if (!socket && !$("band").hidden) connect(); }, 500 * 2 ** retry);
    };
  }

  function link(kind, title) {
    $("link").className = `dot ${kind}`;
    $("link").title = title;
    if (kind !== "ok") $("status").textContent = title;
  }

  function handle(event) {
    switch (event.type) {
      case "state":
        state = event.state;
        renderState();
        break;
      case "position":
        duration = event.durationSeconds;
        renderPosition(event.positionSeconds, event.durationSeconds);
        break;
      case "instruments":
        renderInstruments(event.instruments);
        break;
      case "message":
        note(event.text);
        break;
      default:
        break;
    }
  }

  function renderState() {
    const s = state;
    const playing = s.state === "playing";
    $("song").textContent = s.song ? s.song.title : "No song";
    $("status").textContent = playing ? "Playing" : "Waiting to start";
    const startstop = $("startstop");
    startstop.textContent = playing ? "Stop" : "Start";
    startstop.dataset.action = playing ? "transport/stop" : "transport/start";
    $("vamp").disabled = !playing;
    $("outro").disabled = !playing;

    const bpm = playing ? s.tempo.bpm : s.tempo.pendingBpm;
    $("bpm").textContent = bpm ? bpm.toFixed(1) : "–";
    const detail = [];
    if (playing && s.tempo.speed) detail.push(`× ${s.tempo.speed.toFixed(2)}`);
    if (!playing && s.tempo.pendingBpm) detail.push("next song");
    if (s.tempo.source) detail.push(s.tempo.source);
    $("tempo-detail").textContent = detail.length ? `(${detail.join(", ")})` : "";
    $("slower").disabled = !bpm;
    $("faster").disabled = !bpm;
    $("section").textContent = s.section || "";
    $("following").textContent = s.following
      ? `Following the player: ${s.following.state}${s.following.bpm ? ` at ${s.following.bpm.toFixed(1)} BPM` : ""}`
      : "";
    if (!playing) renderPosition(0, s.durationSeconds || 0);
    if (s.durationSeconds) duration = s.durationSeconds;
    renderSetlist();
  }

  function renderPosition(position, total) {
    $("progress").style.width = total ? `${Math.min(100, (position / total) * 100)}%` : "0";
    $("time").textContent = `${clock(position)} / ${clock(total || duration)}`;
  }

  async function renderSetlist() {
    const response = await fetch(new URL("queue", API), { headers: { Authorization: `Bearer ${token}` }, cache: "no-store" }).catch(() => null);
    if (!response || !response.ok) return;
    const queue = await response.json();
    const list = $("setlist");
    list.replaceChildren(...queue.songs.map((song) => {
      const item = document.createElement("li");
      item.textContent = song.title;
      if (song.index === queue.current) {
        item.className = "current";
        item.setAttribute("aria-current", "true");
      }
      return item;
    }));
  }

  function renderInstruments(instruments) {
    $("instruments").replaceChildren(...instruments.map((i) => {
      const item = document.createElement("li");
      const dot = document.createElement("span");
      dot.className = `dot ${i.connected ? "ok" : "down"}`;
      dot.title = i.connected ? "Connected" : "Disconnected";
      const text = document.createElement("span");
      text.textContent = `${i.name} · ch ${channels(i.channels)} · ${i.latencyMs} ms${i.connected ? "" : " · disconnected"}`;
      item.append(dot, text);
      return item;
    }));
  }

  // A reply and the stream often carry the same line (vamp, panic): show it once.
  let lastNote = { text: "", at: 0 };

  function note(text) {
    const now = Date.now();
    if (text === lastNote.text && now - lastNote.at < 3000) return;
    lastNote = { text, at: now };
    messages.unshift(`${new Date().toLocaleTimeString()}  ${text}`);
    messages.length = Math.min(messages.length, 6);
    $("messages").replaceChildren(...messages.map((m) => {
      const item = document.createElement("li");
      item.textContent = m;
      return item;
    }));
  }

  function clock(seconds) {
    const s = Math.max(0, Math.floor(seconds || 0));
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
  }

  function channels(list) {
    const runs = [];
    for (const c of list) {
      const last = runs[runs.length - 1];
      if (last && c === last[1] + 1) last[1] = c;
      else runs.push([c, c]);
    }
    return runs.map(([a, b]) => (a === b ? `${a}` : `${a}-${b}`)).join(",");
  }

  function nudge(factor) {
    const bpm = state && (state.state === "playing" ? state.tempo.bpm : state.tempo.pendingBpm);
    if (!bpm) return;
    const target = Math.round(Math.min(MAX_BPM, Math.max(MIN_BPM, bpm * factor)) * 10) / 10;
    call("tempo", { bpm: target });
  }

  async function start() {
    showBand();
    link("", "Connecting…");
    if (await loadSession()) connect();
  }

  $("pair-form").addEventListener("submit", (event) => {
    event.preventDefault();
    token = $("token").value.trim();
    if (!token) return;
    storage("set", token);
    $("token").value = "";
    start();
  });
  $("forget").addEventListener("click", () => {
    storage("clear");
    token = null;
    showPairing("");
  });
  $("startstop").addEventListener("click", (event) => call(event.currentTarget.dataset.action || "transport/start"));
  $("next").addEventListener("click", () => call("transport/next"));
  $("tap").addEventListener("click", () => call("tap"));
  $("slower").addEventListener("click", () => nudge(1 / NUDGE));
  $("faster").addEventListener("click", () => nudge(NUDGE));
  $("vamp").addEventListener("click", () => call("sections/vamp"));
  $("outro").addEventListener("click", () => call("sections/outro"));
  $("panic").addEventListener("click", () => call("panic"));

  // A new pairing link opened in this tab only changes the fragment, which doesn't reload the page.
  window.addEventListener("hashchange", () => {
    const fresh = takeTokenFromUrl();
    if (!fresh) return;
    token = fresh;
    if (socket) {
      socket.onclose = null;
      socket.close();
      socket = null;
    }
    start();
  });

  token = takeTokenFromUrl() || storage("get");
  if (token) start();
  else showPairing("");
})();
