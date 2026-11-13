// MetroDom big screen: now playing, up next and the room's join QR, for a TV in the venue
// (docs/references/control-api.md). It holds a join code, which only shows the band: phones
// that scan the QR trade it for their own guest token. No framework, no build step.
"use strict";

(() => {
  const API = new URL("api/v0/", location.href);
  const CODE_KEY = "metrodom.screen";
  const UP_NEXT = 6;
  const $ = (id) => document.getElementById(id);

  let code = null;
  let socket = null;
  let retry = 0;
  let qr = "";
  let duration = 0;

  function storage(action, value) {
    try {
      if (action === "get") return localStorage.getItem(CODE_KEY);
      if (action === "set") localStorage.setItem(CODE_KEY, value);
      if (action === "clear") localStorage.removeItem(CODE_KEY);
    } catch {
      // Blocked storage: the code lives for this page only.
    }
    return null;
  }

  // The remote's Open screen link is screen.html#join=…; the fragment never reaches a server.
  function takeCodeFromUrl() {
    const fromUrl = new URLSearchParams(location.hash.slice(1)).get("join");
    if (!fromUrl) return null;
    storage("set", fromUrl);
    history.replaceState(null, "", location.pathname + location.search);
    return fromUrl;
  }

  function setup(text) {
    if (socket) socket.close();
    socket = null;
    $("screen").hidden = true;
    $("setup").hidden = false;
    if (text) $("setup-text").textContent = text;
  }

  // The QR comes from the Conductor, which knows the address phones should open.
  async function loadQr() {
    let response;
    try {
      response = await fetch(new URL("join/qr", API), { headers: { Authorization: `Bearer ${code}` }, cache: "no-store" });
    } catch {
      return true; // Offline: keep the QR we have, the stream will retry.
    }
    if (response.status === 401) return false;
    if (!response.ok) return true;
    const reply = await response.json();
    if (qr) URL.revokeObjectURL(qr);
    qr = URL.createObjectURL(new Blob([reply.joinQr], { type: "image/svg+xml" }));
    $("qr").src = qr;
    $("join").hidden = false;
    return true;
  }

  function ended() {
    storage("clear");
    code = null;
    setup("This screen's join code has ended. Make a new one in the remote: Invite → Everyone in the room, then Open screen.");
  }

  async function start() {
    $("setup").hidden = true;
    $("screen").hidden = false;
    if (!(await loadQr())) {
      ended();
      return;
    }
    connect();
  }

  function connect() {
    const url = new URL("stream", API);
    url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
    let opened = false;
    socket = new WebSocket(url, ["metrodom.v0", `token.${code}`]);
    socket.onopen = () => {
      opened = true;
      retry = 0;
      link("ok", "Connected");
    };
    socket.onmessage = (event) => handle(JSON.parse(event.data));
    socket.onclose = async (event) => {
      socket = null;
      link("down", event.reason || "Disconnected");
      if (event.code === 1008 || (!opened && !(await loadQr()))) {
        ended();
        return;
      }
      if (!code) return;
      retry = Math.min(retry + 1, 5);
      setTimeout(() => { if (!socket && code) start(); }, 500 * 2 ** retry);
    };
  }

  function link(kind, title) {
    $("link").className = `dot ${kind}`;
    $("link").title = title;
  }

  function handle(event) {
    switch (event.type) {
      case "state":
        renderState(event.state);
        break;
      case "position":
        duration = event.durationSeconds;
        renderPosition(event.positionSeconds, event.durationSeconds);
        break;
      case "queue":
        renderQueue(event.queue);
        break;
      default:
        break;
    }
  }

  function renderState(s) {
    const playing = s.state === "playing";
    $("song").textContent = s.song ? s.song.title : "The band is taking a break";
    const detail = [];
    if (!playing && s.song) detail.push("Starting soon");
    if (playing && s.section) detail.push(s.section);
    if (s.curfew) detail.push(s.curfew);
    $("detail").textContent = detail.join(" · ");
    if (!playing) renderPosition(0, s.durationSeconds || 0);
    if (s.durationSeconds) duration = s.durationSeconds;
  }

  function renderPosition(position, total) {
    const length = total || duration;
    $("progress").style.width = length ? `${Math.min(100, (position / length) * 100)}%` : "0";
  }

  // What's coming, in the order it will play (pinned, then requests by votes, then the set).
  function renderQueue(queue) {
    const coming = queue.songs.filter((song) => song.index > queue.current).slice(0, UP_NEXT);
    $("next").replaceChildren(...coming.map((song) => {
      const item = document.createElement("li");
      const title = document.createElement("span");
      title.className = "title";
      title.textContent = song.title;
      item.append(title);
      if (song.kind === "request") {
        const votes = document.createElement("span");
        votes.className = "votes";
        votes.textContent = `▲ ${song.votes}`;
        item.append(votes);
      }
      if (song.addedBy) {
        const who = document.createElement("span");
        who.className = "who";
        who.textContent = song.addedBy;
        item.append(who);
      }
      return item;
    }));
    $("next-empty").hidden = coming.length > 0;
    const requests = queue.requests;
    $("requests").textContent = !requests
      ? ""
      : requests.open ? (requests.votes ? "Vote for what plays next" : "Requests are open") : "Requests are paused for now";
  }

  // Keep the TV awake where the browser allows it (secure contexts only).
  async function stayAwake() {
    try {
      if (navigator.wakeLock && document.visibilityState === "visible") await navigator.wakeLock.request("screen");
    } catch {
      // Not allowed here: the TV's own settings decide.
    }
  }

  document.addEventListener("visibilitychange", stayAwake);
  window.addEventListener("hashchange", () => {
    const fresh = takeCodeFromUrl();
    if (!fresh) return;
    code = fresh;
    if (socket) {
      socket.onclose = null;
      socket.close();
      socket = null;
    }
    start();
  });

  code = takeCodeFromUrl() || storage("get");
  stayAwake();
  if (code) start();
  else setup("");
})();
