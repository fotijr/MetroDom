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
  let queue = null;
  let grants = new Set();
  let library = [];
  let inviteUrl = "";
  let inviteQr = "";
  let turningVolume = false;
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

  // A room's join QR holds http://host:port/#join=…: trade it for this phone's own guest token.
  function takeJoinFromUrl() {
    const code = new URLSearchParams(location.hash.slice(1)).get("join");
    if (code) history.replaceState(null, "", location.pathname + location.search);
    return code;
  }

  async function join(code) {
    // Already in (scanned twice, reopened the link): keep the token this phone has.
    if (token && (await tokenWorks())) {
      start();
      return;
    }
    let response;
    try {
      response = await fetch(new URL("join", API), { method: "POST", headers: { Authorization: `Bearer ${code}` }, cache: "no-store" });
    } catch {
      showPairing("Can't reach the band. Check you're on the venue's Wi-Fi and scan again.");
      return;
    }
    const reply = await response.json().catch(() => ({}));
    if (!response.ok) {
      showPairing(reply.error || "Couldn't join.");
      return;
    }
    token = reply.token;
    storage("set", token);
    start();
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

  // One API call. Returns the reply (or null when the Conductor can't be reached or refused the token).
  async function request(method, path, body) {
    let response;
    try {
      response = await fetch(new URL(path, API), {
        method,
        headers: { Authorization: `Bearer ${token}`, ...(body ? { "Content-Type": "application/json" } : {}) },
        body: body ? JSON.stringify(body) : undefined,
        cache: "no-store",
      });
    } catch {
      note("Can't reach the Conductor.");
      return null;
    }
    if (response.status === 401) {
      reject();
      return null;
    }
    const reply = await response.json().catch(() => ({}));
    reply.ok = response.ok;
    return reply;
  }

  // An intent: say what the Conductor answered.
  async function call(path, body, method = "POST") {
    const reply = await request(method, path, body);
    if (!reply) return null;
    if (reply.message) note(reply.message);
    else if (reply.error) note(reply.error);
    return reply;
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
    grants = new Set(session.grants);
    document.querySelectorAll("[data-grant]").forEach((element) => {
      element.hidden = !element.dataset.grant.split(" ").some((grant) => grants.has(grant));
    });
    const until = session.expiresAt ? ` · until ${when(session.expiresAt)}` : "";
    $("role").textContent = `${session.label ? `${session.label} · ` : ""}${session.role} · ${session.mode}${until}`;
    if (!grants.has("view")) {
      $("status").textContent = `This ${session.role} link can't see the band under the ${session.mode} policy.`;
      return false;
    }
    if (grants.has("queue.edit") || grants.has("queue.request")) loadLibrary();
    if (grants.has("tokens")) loadTokens();
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
      case "queue":
        queue = event.queue;
        renderQueue();
        renderLibrary();
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
    $("status").textContent = playing ? "Playing" : s.song ? "Waiting to start" : "The queue is empty";
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
    $("curfew").textContent = s.curfew || "";
    // Don't yank the slider from under a finger; the stream catches up when it's let go.
    if (!turningVolume && typeof s.volume === "number") {
      $("volume").value = s.volume;
      $("volume-value").textContent = `${s.volume}%`;
    }
    if (!playing) renderPosition(0, s.durationSeconds || 0);
    if (s.durationSeconds) duration = s.durationSeconds;
  }

  function renderPosition(position, total) {
    $("progress").style.width = total ? `${Math.min(100, (position / total) * 100)}%` : "0";
    $("time").textContent = `${clock(position)} / ${clock(total || duration)}`;
  }

  function button(text, label, onClick, disabled = false) {
    const element = document.createElement("button");
    element.type = "button";
    element.className = "ghost small";
    element.textContent = text;
    element.setAttribute("aria-label", label);
    element.disabled = disabled;
    element.addEventListener("click", onClick);
    return element;
  }

  // The queue from the stream: what's played (dimmed), what's up now, and what's coming (editable).
  function renderQueue() {
    const canEdit = grants.has("queue.edit");
    const last = queue.songs.length - 1;
    const requests = queue.requests;
    const canVote = grants.has("queue.vote") && requests && requests.votes;
    $("requests-bar").hidden = !requests || !(grants.has("queue.request") || canEdit);
    if (requests) {
      $("requests-status").textContent = requests.open ? `Requests open${requests.votes ? " · vote for what plays next" : ""}` : "Requests paused";
      $("requests-toggle").textContent = requests.open ? "Pause requests" : "Open requests";
    }
    $("queue").replaceChildren(...queue.songs.map((song) => {
      const item = document.createElement("li");
      if (song.index < queue.current) item.className = "played";
      if (song.index === queue.current) {
        item.className = "current";
        item.setAttribute("aria-current", "true");
      }
      const text = document.createElement("span");
      text.className = "song";
      text.textContent = song.title;
      if (song.addedBy || song.mine) {
        const who = document.createElement("span");
        who.className = "muted";
        who.textContent = song.mine ? " · your request" : ` · ${song.addedBy}`;
        text.append(who);
      }
      if (song.editable && song.kind === "pinned") {
        const pin = document.createElement("span");
        pin.className = "muted";
        pin.textContent = " · next up";
        text.append(pin);
      }
      item.append(text);
      if (song.editable && song.kind === "request") {
        const votes = button(`▲ ${song.votes}`, song.voted ? `Take back your vote for ${song.title}` : `Vote for ${song.title}`,
          () => call(`queue/${song.id}/vote`, null, song.voted ? "DELETE" : "POST"), !canVote || song.mine);
        votes.classList.add("vote");
        votes.setAttribute("aria-pressed", String(song.voted));
        item.append(votes);
      }
      if (canEdit && song.editable) {
        const tools = document.createElement("span");
        tools.className = "tools";
        tools.append(
          button("↑", `Move ${song.title} up`, () => call(`queue/${song.id}/move`, { to: song.index - 1 }), song.index <= queue.editableFrom),
          button("↓", `Move ${song.title} down`, () => call(`queue/${song.id}/move`, { to: song.index + 1 }), song.index >= last),
          button("✕", `Take ${song.title} out`, () => call(`queue/${song.id}`, null, "DELETE")),
        );
        item.append(tools);
      }
      return item;
    }));
    $("queue-empty").hidden = queue.songs.length > 0;
  }

  async function loadLibrary() {
    const reply = await request("GET", "library");
    if (!reply || !reply.ok) return;
    library = reply.songs;
    renderLibrary();
  }

  function renderLibrary() {
    const filter = $("library-filter").value.trim().toLowerCase();
    const shown = library.filter((song) => !filter || song.title.toLowerCase().includes(filter) || song.id.toLowerCase().includes(filter)).slice(0, 100);
    $("library").replaceChildren(...shown.map((song) => {
      const item = document.createElement("li");
      const text = document.createElement("span");
      text.className = "song";
      text.textContent = song.title;
      const detail = document.createElement("span");
      detail.className = "muted";
      detail.textContent = ` · ${clock(song.durationSeconds)} · ${Math.round(song.bpm)} BPM`;
      text.append(detail);
      // Operators add to the set; guests ask (the Conductor applies the request rules).
      const asking = !grants.has("queue.edit");
      const waiting = queue && queue.songs.some((s) => s.index >= queue.current && s.title === song.title);
      const closed = asking && !(queue && queue.requests && queue.requests.open);
      const add = asking
        ? button(waiting ? "Queued" : "Request", `Request ${song.title}`, () => call("requests", { song: song.id }), closed)
        : button("Add", `Queue ${song.title}`, () => call("queue", { song: song.id }));
      add.classList.add("add");
      item.append(text, add);
      return item;
    }));
    const empty = $("library-empty");
    empty.hidden = shown.length > 0;
    empty.textContent = library.length === 0 ? "No songs in the library folder." : "No song matches.";
  }

  async function loadTokens() {
    const reply = await request("GET", "tokens");
    if (!reply || !reply.ok) return;
    // Guests who joined with a room's QR are counted on it, not listed one by one.
    $("tokens").replaceChildren(...reply.tokens.filter((holder) => !holder.via).map((holder) => {
      const item = document.createElement("li");
      const text = document.createElement("span");
      text.className = "song";
      const kind = holder.join ? `join QR · ${holder.guests} joined` : holder.role;
      text.textContent = `${holder.label || holder.id} · ${kind} · until ${when(holder.expiresAt)}`;
      const revoke = button(holder.join ? "End" : "Revoke", `${holder.join ? "End" : "Revoke"} ${holder.label || holder.id}`, async () => {
        await call(`tokens/${holder.id}`, null, "DELETE");
        loadTokens();
      });
      item.append(text, revoke);
      return item;
    }));
  }

  async function invite(event) {
    event.preventDefault();
    const hours = parseFloat($("invite-hours").value);
    const room = $("invite-role").value === "join";
    const body = { role: room ? "guest" : $("invite-role").value, label: $("invite-label").value.trim() || undefined };
    if (room) body.join = true;
    if (hours > 0) body.hours = hours;
    const reply = await request("POST", "tokens", body);
    if (!reply) return;
    if (!reply.ok) {
      note(reply.error || "Couldn't make the invite.");
      return;
    }
    inviteUrl = room ? reply.joinUrl : reply.pairingUrl;
    if (inviteQr) URL.revokeObjectURL(inviteQr);
    inviteQr = URL.createObjectURL(new Blob([room ? reply.joinQr : reply.pairingQr], { type: "image/svg+xml" }));
    $("invite-qr").src = inviteQr;
    $("invite-who").textContent = room
      ? `${reply.holder.label || "Join QR"} · until ${when(reply.holder.expiresAt)}. Each phone that scans it joins as its own guest. Put it up on a big screen with Open screen.`
      : `${reply.holder.label || "Someone"} · ${reply.holder.role} · until ${when(reply.holder.expiresAt)}. Scan with a phone camera.`;
    $("invite-screen").hidden = !room;
    if (room) $("invite-screen").href = reply.screenUrl;
    $("invite-share").hidden = !navigator.share;
    $("invite-result").hidden = false;
    $("invite-label").value = "";
    loadTokens();
  }

  function latencyCheck(check) {
    if (!check) return "";
    switch (check.status) {
      case "ok": return " · latency ok";
      case "drifted": return ` · drifted ${check.worstDriftMs > 0 ? "+" : ""}${check.worstDriftMs} ms, recalibrate`;
      case "partial": return ` · not heard: note ${check.silentNotes.join(", ")}`;
      default: return " · not heard";
    }
  }

  function when(iso) {
    return new Date(iso).toLocaleString([], { weekday: "short", hour: "2-digit", minute: "2-digit" });
  }

  function renderInstruments(instruments) {
    $("instruments").replaceChildren(...instruments.map((i) => {
      const item = document.createElement("li");
      const dot = document.createElement("span");
      dot.className = `dot ${i.connected ? "ok" : "down"}`;
      dot.title = i.connected ? "Connected" : "Disconnected";
      const text = document.createElement("span");
      text.textContent = `${i.name} · ch ${channels(i.channels)} · ${i.latencyMs} ms${i.connected ? "" : " · disconnected"}${latencyCheck(i.latencyCheck)}`;
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
  $("curfew-form").addEventListener("submit", (event) => {
    event.preventDefault();
    if ($("curfew-at").value) call("curfew", { at: $("curfew-at").value });
  });
  $("curfew-lift").addEventListener("click", () => call("curfew", undefined, "DELETE"));
  $("volume").addEventListener("pointerdown", () => { turningVolume = true; });
  $("volume").addEventListener("input", () => {
    $("volume-value").textContent = `${$("volume").value}%`;
  });
  $("volume").addEventListener("change", async () => {
    await call("volume", { percent: Number($("volume").value) });
    turningVolume = false;
  });
  $("library-filter").addEventListener("input", renderLibrary);
  $("requests-toggle").addEventListener("click", () => call("requests", { open: !(queue && queue.requests && queue.requests.open) }, "PUT"));
  $("library-refresh").addEventListener("click", loadLibrary);
  $("invite-form").addEventListener("submit", invite);
  $("invite-copy").addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(inviteUrl);
      note("Link copied.");
    } catch {
      window.prompt("Copy this link:", inviteUrl);
    }
  });
  $("invite-share").addEventListener("click", () => navigator.share({ title: "MetroDom", url: inviteUrl }).catch(() => {}));

  // Installable app: the service worker keeps the app's own files for when the box is out of
  // reach. Browsers only allow it on https or this computer (localhost).
  if ("serviceWorker" in navigator && window.isSecureContext) {
    navigator.serviceWorker.register("sw.js").catch(() => {});
  }

  // A new pairing link opened in this tab only changes the fragment, which doesn't reload the page.
  window.addEventListener("hashchange", () => {
    const code = takeJoinFromUrl();
    if (code) {
      join(code);
      return;
    }
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

  const joinCode = takeJoinFromUrl();
  token = takeTokenFromUrl() || storage("get");
  if (joinCode) join(joinCode);
  else if (token) start();
  else showPairing("");
})();
