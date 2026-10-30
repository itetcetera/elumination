// DRONE playtest client. Talks to the server's /hub (docs/PROTOCOL.md) and draws what it sends.
"use strict";

const GOODS = ["food", "energy", "ore", "crystite", "helion"];
const GOOD_LETTER = { food: "F", energy: "E", ore: "O", crystite: "C", helion: "H" };
const TERRAIN_FILL = { plain: "#cdb882", mountain: "#8a7766", river: "#6fa8dc", lake: "#3d6ea8", store: "#f1c232", outpost: "#e69138" };
const SEAT_COLOR = ["#e8574a", "#4aa3e8", "#5cc46b", "#c27be8"];
const TICKS = 60;
const HEX = 26;

const $ = id => document.getElementById(id);
const el = (tag, attrs = {}, ...kids) => {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
    else if (k === "class") e.className = v;
    else if (v !== false && v != null) e.setAttribute(k, v === true ? "" : v);
  }
  for (const kid of kids.flat()) if (kid != null) e.append(kid);
  return e;
};
const svg = (tag, attrs = {}) => {
  const e = document.createElementNS("http://www.w3.org/2000/svg", tag);
  for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
  return e;
};
const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { } },
};

// ---------------------------------------------------------------- state

const me = { name: store.get("drone.name") || "", token: null, playerId: null, code: null };
let lobby = null;
let state = null;      // the last game state, with the last map kept (frames leave it out when unchanged)
let map = null;
let phaseKey = null;   // which phase the controls were built for
let draftPicks = [];
let turnMode = "walk"; // what a click on the map does in your turn
let held = 0;          // the direction held in an auction (1, -1, 0)
let belowNeed = false;
const log = [];

const hub = new signalR.HubConnectionBuilder().withUrl("/hub").withAutomaticReconnect([0, 1000, 2000, 5000, 10000]).build();

hub.on("lobby", l => { lobby = l; showLobby(); });
hub.on("frame", f => {
  if (f.full) { log.length = 0; $("log").replaceChildren(); }
  if (f.state.map) map = f.state.map;
  state = f.state;
  for (const e of f.events) addLog(e);
  showGame();
});
hub.onreconnecting(() => status("Reconnecting…"));
hub.onreconnected(async () => { status(""); if (me.code) await join(me.code); });
hub.onclose(() => status("Disconnected. Reload the page to come back."));

function status(text) { $("status").textContent = text; }

async function call(method, ...args) {
  try { return await hub.invoke(method, ...args); }
  catch (e) { flash(cleanError(e)); throw e; }
}
function cleanError(e) { return String(e.message || e).replace(/^.*HubException: /, "").replace(/^An unexpected error.*$/, "Something went wrong."); }
function flash(text) {
  const where = !$("game").classList.contains("hidden") ? $("flash") : !$("lobby").classList.contains("hidden") ? $("lobbyError") : $("joinError");
  const span = el("span", { class: "error" }, " " + text);
  if (where === $("flash")) { where.append(span); setTimeout(() => span.remove(), 3000); }
  else where.textContent = text;
}

// ---------------------------------------------------------------- joining

async function join(code) {
  const tokens = JSON.parse(store.get("drone.tokens") || "{}");
  const r = await hub.invoke("JoinRoom", code, me.name, tokens[code.toUpperCase()] || null);
  joined(r);
}
function joined(r) {
  me.token = r.token; me.playerId = r.playerId; me.code = r.room.code;
  const tokens = JSON.parse(store.get("drone.tokens") || "{}");
  tokens[me.code] = r.token;
  store.set("drone.tokens", JSON.stringify(tokens));
  history.replaceState(null, "", "/r/" + me.code);
  lobby = r.room;
  showLobby();
}
function readName() {
  me.name = $("name").value.trim();
  if (!me.name) { $("joinError").textContent = "Pick a name first."; return false; }
  store.set("drone.name", me.name);
  return true;
}

$("name").value = me.name;
$("create").onclick = async () => {
  if (!readName()) return;
  try { joined(await hub.invoke("CreateRoom", me.name)); } catch (e) { $("joinError").textContent = cleanError(e); }
};
$("joinBtn").onclick = async () => {
  if (!readName()) return;
  try { await join($("code").value.trim()); } catch (e) { $("joinError").textContent = cleanError(e); }
};
const linkCode = location.pathname.match(/^\/r\/([A-Za-z0-9]{6})/);
if (linkCode) $("code").value = linkCode[1].toUpperCase();

// ---------------------------------------------------------------- lobby

function show(screen) { for (const s of ["join", "lobby", "game"]) $(s).classList.toggle("hidden", s !== screen); }

function showLobby() {
  if (!lobby) return;
  if (lobby.phase !== "lobby") { show("game"); return; }
  show("lobby");
  $("roomCode").textContent = lobby.code;
  const link = location.origin + "/r/" + lobby.code;
  $("invite").textContent = link; $("invite").href = link;
  const isHost = lobby.hostId === me.playerId;
  const mine = lobby.members.find(m => m.id === me.playerId);
  $("members").replaceChildren(...lobby.members.map(m => el("li", {},
    m.name, m.id === lobby.hostId ? " (host)" : "", m.species ? ` · ${m.species}` : " · random species",
    m.connected ? "" : " · away")));
  $("species").replaceChildren(
    ...lobby.species.map(s => {
      const taken = lobby.members.some(m => m.species === s.id && m.id !== me.playerId);
      return el("button", { class: mine?.species === s.id ? "on" : "", disabled: taken, onclick: () => call("SetSpecies", s.id).catch(() => { }) },
        cap(s.id), el("small", {}, s.ability));
    }),
    el("button", { class: !mine?.species ? "on" : "", onclick: () => call("SetSpecies", null).catch(() => { }) }, "Random", el("small", {}, "Dealt when the game starts")));
  const o = lobby.options;
  const setOpt = (rounds, land) => call("SetOptions", rounds, land).catch(() => { });
  $("options").replaceChildren(
    el("label", {}, "Length ", el("select", { disabled: !isHost, onchange: e => setOpt(+e.target.value, o.landAuction) },
      lobby.roundChoices.map(r => el("option", { value: r, selected: r === o.rounds }, `${r} rounds`)))),
    el("label", {}, "Land auctions ", el("select", { disabled: !isHost, onchange: e => setOpt(o.rounds, e.target.value) },
      el("option", { value: "live", selected: o.landAuction === "live" }, "Live"),
      el("option", { value: "sealed", selected: o.landAuction === "sealed" }, "Sealed (second price)"))));
  $("start").classList.toggle("hidden", !isHost);
}
$("start").onclick = () => call("Start").catch(() => { });

// ---------------------------------------------------------------- the game

const cap = s => s ? s[0].toUpperCase() + s.slice(1) : s;
const mySeat = () => state?.players.find(p => p.id === me.playerId)?.seat ?? -1;
const nameOf = seat => seat < 0 ? "the store" : state?.players[seat]?.name ?? `seat ${seat}`;
const secs = ticks => Math.max(0, Math.ceil(ticks / TICKS));

function showGame() {
  if (!state || !map) return;
  show("game");
  const ph = state.phase;
  const key = !ph ? "none" : ph.kind === "land" ? `land${ph.plot}` : ph.kind === "goods" ? `goods${state.round}${ph.good}`
    : ph.kind === "turn" ? `turn${state.round}-${ph.seat}` : `draft${state.round}`;
  if (key !== phaseKey) {
    if (held) release();
    if (phaseKey !== null) announce(ph);
    phaseKey = key;
    draftPicks = [];
    turnMode = "walk";
    buildControls(ph);
  }
  drawBanner(ph);
  drawSteps(ph);
  drawNow(ph);
  drawMap(ph);
  updateControls(ph);
  drawPlayers();
  drawStore();
}

function drawBanner(ph) {
  const b = $("banner");
  if (ph?.kind === "turn" && ph.seat === mySeat() && ph.message) { b.replaceChildren(el("span", { class: "msg" }, ph.message)); return; }
  b.replaceChildren();
  return;
  const round = `Round ${state.round} of ${state.rounds}`;
  let text = state.over ? "The game is over" : "";
  if (!state.over && ph) {
    if (ph.kind === "draft") text = `Land draft: rank the plots you want · ${secs(ph.endsAt - state.tick)}s`;
    if (ph.kind === "land") text = `Land auction: plot ${ph.plot} from ${nameOf(ph.seller)}${ph.sealed ? " (sealed bids)" : ""}`;
    if (ph.kind === "turn") text = `${ph.seat === mySeat() ? "Your turn" : nameOf(ph.seat) + "'s turn"} · ${Math.ceil(ph.timeLeft)}s left`;
    if (ph.kind === "goods") text = `${cap(ph.good)} auction`;
  }
  b.replaceChildren(el("b", {}, round), " · ", text, ...(ph?.kind === "turn" && ph.message ? [el("span", { class: "msg" }, ph.message)] : []));
}

// ---------------------------------------------------------------- where we are, what to do

const STEPS = [["draft", "Land draft"], ["land", "Land auctions"], ["turn", "Turns"], ...GOODS.map(g => ["goods-" + g, cap(g)])];
const stepOf = ph => !ph ? null : ph.kind === "goods" ? "goods-" + ph.good : ph.kind;
let lastStep = null;

function drawSteps(ph) {
  const cur = stepOf(ph) ?? lastStep;
  if (stepOf(ph)) lastStep = stepOf(ph);
  const at = STEPS.findIndex(([k]) => k === cur);
  $("steps").replaceChildren(el("li", { class: "on" }, `Round ${state.round}/${state.rounds}`),
    ...STEPS.filter(([k]) => k !== "goods-helion" || state.store.price[4] > 0)
      .map(([k, label]) => el("li", { class: k === cur && ph ? "on" : STEPS.findIndex(s => s[0] === k) < at ? "done" : "" }, label)));
}

const goodsSideWindow = ph => ph.pos.every(v => v === 0);

// The phase's name, and in plain words what this player should do right now.
function guide(ph) {
  const seat = mySeat(), meP = state.players[seat];
  if (state.over) return { title: "Game over", text: "The final count is in the table on the right." };
  if (!ph) return { title: "The colony is at work", text: "Production, colony news and scores happen now. Nothing to do: the next phase starts on its own." };
  if (ph.kind === "draft") {
    const n = state.players.length;
    return ph.submitted[seat]
      ? { title: "Land draft: list handed in", text: "Waiting for the others. Everyone gets one free plot.", bar: secs(ph.endsAt - state.tick) / 20 }
      : { title: "Land draft: pick your free plot", act: true, bar: secs(ph.endsAt - state.tick) / 20,
          text: [`Click up to ${n} empty plots on the map, the one you want most first.`, "Press \"Hand in my list\" below.",
            `Last place picks first; you get your highest pick nobody took before you. ${secs(ph.endsAt - state.tick)}s left.`] };
  }
  if (ph.kind === "land") {
    const lp = map.plots[ph.plot], plot = `a ${lp.terrain}${lp.oreUnits ? ` with ${lp.oreUnits} ore` : ""} plot (yellow outline on the map)`;
    if (ph.sealed) return { title: `Land auction: ${plot}`, act: ph.seller !== seat, bar: ph.timer,
      text: ph.seller === seat ? "You're selling it. Wait for the sealed bids." : `Type a bid and press Bid, or do nothing to pass. Highest bid wins and pays the second-highest bid. You have ${meP.cash}.` };
    if (ph.seller === seat) return { title: `You're selling ${plot}`, act: true, bar: ph.timer,
      text: "Hold Down (or ↓) to lower your asking price until someone bids. When the clock runs out the highest bidder buys it." };
    return { title: `Land auction: ${plot}`, act: true, bar: ph.timer,
      text: [`For sale by ${nameOf(ph.seller)}, starting at ${ph.startPrice}. You have ${meP.cash}.`,
        "To bid: hold Up (or ↑). Your bar climbs; once it passes the start line you are bidding. Let go to stop.",
        "Not interested? Do nothing. When the clock runs out the highest bid wins."] };
  }
  if (ph.kind === "turn") {
    if (ph.seat !== seat) return { title: `${nameOf(ph.seat)}'s turn`, bar: ph.timeLeft / ph.seconds,
      text: "Watch them work. Each player takes a turn in order; you'll be told when it's yours." };
    return { title: `Your turn: ${Math.ceil(ph.timeLeft)}s`, act: true, bar: ph.timeLeft / ph.seconds, text: turnGuide(ph) };
  }
  if (ph.kind === "goods") {
    const g = GOODS.indexOf(ph.good), have = meP.held[g], need = ph.line[seat], side = ph.sides[seat];
    const needText = need ? ` You need ${need} for next round.` : "";
    if (goodsSideWindow(ph)) return { title: `${cap(ph.good)} auction: buy or sell?`, act: true,
      text: `You have ${have} ${ph.good}.${needText} Press Buy or Sell now. It starts in a moment.` };
    const tail = "Units trade when a buyer's price meets a seller's. When the clock runs out the auction ends.";
    if (side === "buy") return { title: `${cap(ph.good)} auction: you're buying`, act: true, bar: ph.timer,
      text: [`You have ${have}.${needText}`, `Hold Up (or ↑) to raise what you'll pay.${ph.storeSells ? ` The store sells at ${ph.top}.` : ""} Don't want any? Do nothing.`, tail] };
    return { title: `${cap(ph.good)} auction: you're selling`, act: true, bar: ph.timer,
      text: [`You have ${have}.${needText}${need ? " You stop selling at that line unless you tick the box below." : ""}`,
        `Hold Down (or ↓) to lower your asking price. The store always buys at ${ph.bottom}.`, tail] };
  }
  return { title: "", text: "" };
}

// Your turn as a short checklist: get a drone, give it a job, put it on your land.
function turnGuide(ph) {
  const seat = mySeat();
  const mine = map.plots.filter(p => p.owner === seat);
  const empty = mine.filter(p => !p.drone);
  const here = map.plots[ph.location];
  const steps = [
    ["Walk into town: click the STORE.", ph.inTown || ph.leading],
    ["Buy a drone.", ph.leading],
    ["Outfit it: food, energy, ore...", ph.leading && ph.leadJob],
    [`Walk to one of your empty plots (your color outline${empty.length ? "" : "; you have none free"}).`, ph.leading && ph.leadJob && !ph.inTown && here?.owner === seat],
    ["Press \"Install\".", false],
  ];
  if (!ph.leading && empty.length === 0 && mine.length) steps.splice(0, steps.length, ["All your plots have drones. Assay a plot, chase the Glimmer ✦, or go to the pub for cash.", false]);
  let cur = steps.findIndex(([, done]) => !done);
  return el("div", {}, el("ol", {}, steps.map(([t, done], i) => el("li", { class: done ? "doneStep" : i === cur ? "curStep" : "" }, t))),
    el("div", { class: "hint" }, "Done early? \"Go to the pub\" ends your turn and pays you for the time left."));
}

let guideKey = "";
function drawNow(ph) {
  const g = guide(ph);
  $("now").classList.toggle("act", !!g.act);
  $("nowTitle").textContent = g.title;
  const key = g.text instanceof Node ? g.text.textContent : [].concat(g.text).join("|");
  if (key !== guideKey) {
    guideKey = key;
    $("nowText").replaceChildren(g.text instanceof Node ? g.text : Array.isArray(g.text) ? el("ul", {}, g.text.map(t => el("li", {}, t))) : g.text);
  }
  $("nowBar").style.width = `${Math.round(Math.max(0, Math.min(1, g.bar ?? 0)) * 100)}%`;
}

let announceTimer = null;
function announce(ph) {
  if (!ph || state.over) return;
  const seat = mySeat();
  const big = ph.kind === "draft" ? "Land draft" : ph.kind === "land" ? "Land auction" : ph.kind === "goods" ? `${cap(ph.good)} auction`
    : ph.seat === seat ? "Your turn!" : `${nameOf(ph.seat)}'s turn`;
  const small = ph.kind === "draft" ? "Pick the plots you want" : ph.kind === "land" ? `Plot ${ph.plot} is for sale` : ph.kind === "goods" ? "Choose: buy or sell"
    : ph.seat === seat ? "Get a drone and put it to work" : "Watch them play";
  if (ph.kind === "land" && lastStep === "land") return; // one card per run of land auctions
  if (ph.kind === "turn" && ph.seat !== seat && lastStep === "turn") return;
  const a = $("announce");
  a.replaceChildren(el("b", {}, big), el("span", {}, small));
  a.classList.remove("hidden");
  clearTimeout(announceTimer);
  announceTimer = setTimeout(() => a.classList.add("hidden"), 2200);
}

// Flat-top hexes, odd columns half a hex down.
function hexCenter(col, row) {
  const h = Math.sqrt(3) * HEX;
  return [HEX + col * 1.5 * HEX, h / 2 + row * h + (col % 2 ? h / 2 : 0)];
}
function hexPoints(cx, cy) {
  const pts = [];
  for (let i = 0; i < 6; i++) { const a = Math.PI / 3 * i; pts.push(`${(cx + HEX * Math.cos(a)).toFixed(1)},${(cy + HEX * Math.sin(a)).toFixed(1)}`); }
  return pts.join(" ");
}

// The hexes are rebuilt only when the map changes, so hover and clicks survive the frames;
// everything drawn on top is redrawn each frame and lets clicks through.
let drawnMap = null;
function drawMap(ph) {
  const m = $("map");
  const h = Math.sqrt(3) * HEX;
  if (drawnMap !== map) {
    drawnMap = map;
    m.setAttribute("viewBox", `0 0 ${HEX * 2 + (map.cols - 1) * 1.5 * HEX} ${h * map.rows + h / 2}`);
    const hexes = map.plots.map(p => {
      const [x, y] = hexCenter(p.col, p.row);
      const hex = svg("polygon", { points: hexPoints(x, y), class: "hex", fill: TERRAIN_FILL[p.terrain] });
      hex.addEventListener("click", () => clickPlot(p));
      const title = svg("title");
      title.textContent = plotTitle(p);
      hex.append(title);
      return hex;
    });
    m.replaceChildren(svg("g", { id: "hexes" }), svg("g", { id: "overlay", "pointer-events": "none" }));
    m.firstChild.append(...hexes);
  }
  const g = [];
  for (const p of map.plots) {
    const [x, y] = hexCenter(p.col, p.row);
    if (p.owner >= 0) g.push(svg("polygon", { points: hexPoints(x, y).split(" ").map(pt => { const [a, b] = pt.split(",").map(Number); return `${x + (a - x) * .82},${y + (b - y) * .82}`; }).join(" "), fill: "none", stroke: SEAT_COLOR[p.owner], "stroke-width": 4 }));
    if (p.terrain === "mountain" && p.oreUnits) g.push(text(x, y - 11, "▲".repeat(p.oreUnits), 8, "#3b3029"));
    if (p.terrain === "store") g.push(text(x, y + 4, "STORE", 9, "#111"));
    if (p.terrain === "outpost") g.push(text(x, y + 4, "OUT", 9, "#111"));
    if (p.drone) g.push(svg("circle", { cx: x, cy: y + 2, r: 9, fill: "#111" }), text(x, y + 6, GOOD_LETTER[p.drone], 11, SEAT_COLOR[p.owner] || "#fff"));
    if (p.assayed) g.push(text(x, y + 19, `c${p.crystite} h${p.helion}`, 8, "#222"));
    if (p.forSale) g.push(text(x, y - 3, "$", 13, "#111"));
    if (ph?.kind === "draft") {
      const i = draftPicks.indexOf(p.index);
      if (i >= 0) g.push(svg("polygon", { points: hexPoints(x, y), class: "pick" }), text(x, y - 4, `#${i + 1}`, 13, "#111"));
    }
    if (ph?.kind === "land" && ph.plot === p.index) g.push(svg("polygon", { points: hexPoints(x, y), class: "pick" }));
  }
  if (ph?.kind === "turn") {
    const at = map.plots[ph.location];
    if (ph.glimmerAt != null) { const [x, y] = hexCenter(map.plots[ph.glimmerAt].col, map.plots[ph.glimmerAt].row); g.push(text(x + 10, y - 6, "✦", 18, "#fff")); }
    if (ph.crate?.at != null && !ph.crate.caught) { const [x, y] = hexCenter(map.plots[ph.crate.at].col, map.plots[ph.crate.at].row); g.push(text(x - 11, y - 6, "▣", 15, "#7a4b16")); }
    if (at) {
      const [x, y] = hexCenter(at.col, at.row);
      g.push(svg("circle", { cx: x + 12, cy: y + 8, r: 7, fill: SEAT_COLOR[ph.seat], stroke: "#fff", "stroke-width": 2 }));
      if (ph.leading) g.push(text(x + 12, y + 22, ph.leadJob ? GOOD_LETTER[ph.leadJob] + "-drone" : "drone", 8, "#fff"));
    }
  }
  $("overlay").replaceChildren(...g);
}
function text(x, y, s, size, fill) {
  const t = svg("text", { x, y, "font-size": size, fill, "text-anchor": "middle", "font-weight": "bold" });
  t.textContent = s;
  return t;
}
function plotTitle(p) {
  const bits = [`Plot ${p.index}: ${p.terrain}`];
  if (p.oreUnits) bits.push(`${p.oreUnits} ore`);
  if (p.owner >= 0) bits.push(`owned by ${nameOf(p.owner)}`);
  if (p.drone) bits.push(`${p.drone} drone`);
  if (p.assayed) bits.push(`assay: crystite ${p.crystite}, helion ${p.helion}`);
  if (p.forSale) bits.push("for sale");
  return bits.join(" · ");
}

function clickPlot(p) {
  const ph = state?.phase;
  if (!ph) return;
  if (ph.kind === "draft") {
    if (p.owner >= 0 || p.terrain === "store" || p.terrain === "outpost") return;
    const i = draftPicks.indexOf(p.index);
    if (i >= 0) draftPicks.splice(i, 1);
    else if (draftPicks.length < state.players.length) draftPicks.push(p.index);
    showGame();
    return;
  }
  if (ph.kind === "turn" && ph.seat === mySeat()) {
    const town = p.terrain === "store" || p.terrain === "outpost";
    if (turnMode === "walk") turn(town ? "goToTown" : "walkTo", { plot: p.index });
    else turn(turnMode, { plot: p.index });
    if (turnMode !== "walk") { turnMode = "walk"; buildControls(ph); }
  }
}

function turn(type, extra = {}) { call("Turn", { type, ...extra }).catch(() => { }); }

// ---------------------------------------------------------------- phase controls

function buildControls(ph) {
  const c = $("controls");
  c.replaceChildren();
  $("scaleArea").replaceChildren(...(ph && (ph.kind === "land" || ph.kind === "goods") ? [el("div", { id: "scaleBox" })] : []));
  if (!ph) return;
  const seat = mySeat();
  if (ph.kind === "draft") {
    c.append(el("div", { class: "group" },
      el("span", { class: "hint", id: "draftList" }),
      el("button", { onclick: () => call("Draft", draftPicks).then(() => flash("List handed in.")).catch(() => { }) }, "Hand in my list"),
      el("button", { onclick: () => { draftPicks = []; showGame(); } }, "Clear")),
      el("p", { class: "hint" }, "Click plots in the order you want them. Lists are resolved last place first; each player gets one plot."));
  }
  if (ph.kind === "land") {
    if (ph.sealed) {
      const input = el("input", { type: "number", min: 0, step: 1, placeholder: "your bid" });
      c.append(el("div", { class: "group" }, el("span", { class: "label" }, "Sealed bid"), input,
        el("button", { onclick: () => call("SealedBid", Math.max(0, Math.round(+input.value || 0))).then(() => flash("Bid handed in.")).catch(() => { }) }, "Bid"),
        el("span", { class: "hint" }, "Highest bid wins and pays the second-highest bid.")));
    } else {
      c.append(holdButtons(d => call("LandMove", d).catch(() => { }),
        ph.seller === seat ? "You are selling: hold down to lower your asking price." : "Hold up to bid more, down to back off. Arrow keys work too."));
    }
  }
  if (ph.kind === "turn" && ph.seat === seat) {
    const mode = (m, label) => el("button", { class: turnMode === m ? "on" : "", onclick: () => { turnMode = turnMode === m ? "walk" : m; buildControls(ph); } }, label);
    c.append(
      el("div", { class: "group" }, el("span", { class: "label" }, "Town"),
        el("button", { onclick: () => turn("buyDrone") }, "Buy drone"),
        GOODS.map(g => el("button", { onclick: () => turn("outfit", { good: g }) }, `Outfit ${g}`)),
        el("button", { onclick: () => turn("sellDrone") }, "Sell drone back")),
      el("div", { class: "group" }, el("span", { class: "label" }, "Field"),
        el("button", { onclick: () => turn("press") }, "Install"),
        el("button", { onclick: () => turn("release") }, "Let drone go"),
        el("button", { onclick: () => turn("catchGlimmer") }, "Catch Glimmer"),
        el("button", { onclick: () => turn("catchCrate") }, "Grab crate")),
      el("div", { class: "group" }, el("span", { class: "label" }, "Click map"),
        el("button", { class: turnMode === "walk" ? "on" : "", onclick: () => { turnMode = "walk"; buildControls(ph); } }, "Walk there"),
        mode("assay", "Assay plot"), mode("markForSale", "Put up for auction"), mode("quickSale", "Sell to store now")),
      el("div", { class: "group" }, el("span", { class: "label" }, "End"),
        el("button", { onclick: () => turn("pub") }, "Go to the pub (cash for time left)"),
        el("button", { onclick: () => turn("endTurn") }, "End turn")),
      el("p", { class: "hint" }, "Actions queue up and happen in order. Click the store or an outpost to walk into town."));
  }
  if (ph.kind === "goods") {
    c.append(
      el("div", { class: "group" }, el("span", { class: "label" }, "Side"),
        el("button", { id: "sideBuy", onclick: () => call("GoodsSide", "buy").catch(() => { }) }, "Buy"),
        el("button", { id: "sideSell", onclick: () => call("GoodsSide", "sell").catch(() => { }) }, "Sell"),
        el("span", { class: "hint" }, "Pick a side in the first moments.")),
      holdButtons(d => call("GoodsMove", d, belowNeed).catch(() => { }), "Buyers hold up to raise their price, sellers hold down to lower theirs."),
      ...(ph.good === "food" || ph.good === "energy" ? [el("label", { class: "group" }, el("input", { type: "checkbox", onchange: e => { belowNeed = e.target.checked; if (held) call("GoodsMove", held, belowNeed).catch(() => { }); } }),
          " Sell below what I need next round (you may go short)")] : []));
  }
}

let holdSend = null;
function holdButtons(send, hint) {
  holdSend = send;
  const btn = (d, label) => {
    const b = el("button", {}, label);
    b.addEventListener("pointerdown", () => press(d));
    for (const ev of ["pointerup", "pointerleave", "pointercancel"]) b.addEventListener(ev, () => { if (held === d) release(); });
    return b;
  };
  return el("div", { class: "group" }, el("span", { class: "label" }, "Hold"), btn(1, "▲ Up"), btn(-1, "▼ Down"), el("span", { class: "hint" }, hint));
}
function press(d) { if (held === d || !holdSend) return; held = d; holdSend(d); }
function release() { held = 0; holdSend?.(0); }
document.addEventListener("keydown", e => {
  if (e.repeat || e.target.tagName === "INPUT") return;
  if (e.key === "ArrowUp") { press(1); e.preventDefault(); }
  if (e.key === "ArrowDown") { press(-1); e.preventDefault(); }
});
document.addEventListener("keyup", e => { if ((e.key === "ArrowUp" && held === 1) || (e.key === "ArrowDown" && held === -1)) release(); });

function updateControls(ph) {
  if (state.over) {
    const best = [...state.players].sort((a, b) => b.total - a.total)[0];
    $("controls").replaceChildren(el("div", { class: "over" }, `Final count: ${best.name} wins with ${best.total}.`));
    return;
  }
  if (!ph) return;
  if (ph.kind === "draft") {
    const list = $("draftList");
    if (list) list.textContent = draftPicks.length ? "Your list: " + draftPicks.map((p, i) => `#${i + 1} plot ${p}`).join(", ") : "Your list is empty.";
  }
  if (ph.kind === "goods") {
    const s = ph.sides[mySeat()];
    $("sideBuy")?.classList.toggle("on", s === "buy");
    $("sideSell")?.classList.toggle("on", s === "sell");
  }
  const box = $("scaleBox");
  if (box && ph.kind === "land") box.replaceChildren(landScale(ph));
  if (box && ph.kind === "goods") box.replaceChildren(goodsScale(ph));
}

// A price scale drawn upright: each player's line, plus the auction's own marks.
function scale(lo, hi, marks, players, footer) {
  const W = 600, H = 210, top = 30, bottom = H - 26;
  const y = v => bottom - (v - lo) / Math.max(1, hi - lo) * (bottom - top);
  const s = svg("svg", { viewBox: `0 0 ${W} ${H}`, class: "scale" });
  for (const m of marks) {
    s.append(svg("line", { x1: 40, x2: W - 10, y1: y(m.v), y2: y(m.v), stroke: m.color, "stroke-dasharray": m.dash ? "4 4" : "", "stroke-width": 1.5 }));
    const t = svg("text", { x: 44, y: y(m.v) - 3, fill: m.color, "font-size": 11 }); t.textContent = m.label; s.append(t);
  }
  const lane = (W - 120) / players.length;
  players.forEach((p, i) => {
    const x = 110 + lane * i + lane / 2;
    const v = Math.min(hi, Math.max(lo, p.v));
    s.append(svg("rect", { x: x - 22, y: y(v) - 5, width: 44, height: 10, rx: 3, fill: SEAT_COLOR[p.seat], opacity: p.out ? .35 : 1 }));
    const t = svg("text", { x, y: H - 8, fill: "#ccc", "font-size": 11, "text-anchor": "middle" }); t.textContent = p.label; s.append(t);
    const pv = svg("text", { x, y: y(v) - 8, fill: "#fff", "font-size": 11, "text-anchor": "middle" }); pv.textContent = p.out ? "out" : p.v; s.append(pv);
  });
  const f = svg("text", { x: 4, y: 12, fill: "#aaa", "font-size": 11 }); f.textContent = footer; s.append(f);
  return s;
}

function landScale(ph) {
  const lo = ph.bottom, hi = Math.max(ph.startPrice * 2, ...ph.positions, ph.highBid) + 10;
  const marks = [{ v: ph.startPrice, label: `start ${ph.startPrice}`, color: "#f1c232" }];
  if (ph.highBidder >= 0) marks.push({ v: ph.highBid, label: `high bid ${ph.highBid} (${nameOf(ph.highBidder)})`, color: SEAT_COLOR[ph.highBidder], dash: true });
  const players = state.players.filter(p => p.seat !== ph.seller).map(p => ({ seat: p.seat, v: ph.positions[p.seat], label: p.name, out: ph.positions[p.seat] < ph.startPrice }));
  return scale(lo, hi, marks, players, `Clock ${Math.round(ph.timer * 100)}%`);
}

function goodsScale(ph) {
  const lo = ph.bottom - ph.step, hi = ph.top + ph.step;
  const marks = [{ v: ph.bottom, label: `store buys at ${ph.bottom}`, color: "#9aa1ad" }];
  if (ph.storeSells) marks.push({ v: ph.top, label: `store sells at ${ph.top} (${state.store.stock[GOODS.indexOf(ph.good)]} left)`, color: "#f1c232" });
  const players = state.players.map(p => {
    const v = ph.pos[p.seat], buy = ph.sides[p.seat] === "buy";
    const line = ph.line[p.seat] ? `, needs ${ph.line[p.seat]}` : "";
    return { seat: p.seat, v, out: buy ? v < ph.bottom : v > ph.top, label: `${p.name}: ${ph.sides[p.seat]} (${p.held[GOODS.indexOf(ph.good)]}${line})` };
  });
  const trading = ph.trading ? ` · ${nameOf(ph.trading[1])} → ${nameOf(ph.trading[0])} at ${ph.trading[2]}` : "";
  return scale(lo, hi, marks, players, `Clock ${Math.round(ph.timer * 100)}% · ${ph.units} sold${trading}`);
}

// ---------------------------------------------------------------- side panels

function drawPlayers() {
  const head = el("tr", {}, ["Player", "Cash", ...GOODS.map(g => GOOD_LETTER[g]), "Food need", "Total"].map(h => el("th", {}, h)));
  const rows = state.players.map(p => el("tr", { class: p.id === me.playerId ? "me" : "", title: `${cap(p.species)}: ${p.ability}` },
    el("td", {}, el("span", { class: "swatch", style: `background:${SEAT_COLOR[p.seat]}` }), p.name, p.isCpu ? " (CPU)" : "", el("br"), el("small", { class: "hint" }, `${cap(p.species)} · #${p.rank}`)),
    el("td", {}, p.cash),
    p.held.map(h => el("td", {}, h)),
    el("td", {}, p.foodNeed),
    el("td", {}, p.total)));
  $("players").replaceChildren(head, ...rows);
}
function drawStore() {
  const s = state.store;
  $("store").replaceChildren(
    el("tr", {}, ["Store", ...GOODS.map(g => GOOD_LETTER[g])].map(h => el("th", {}, h))),
    el("tr", {}, el("td", {}, "Stock"), s.stock.map(v => el("td", {}, v))),
    el("tr", {}, el("td", {}, "Price"), s.price.map(v => el("td", {}, v))),
    el("tr", {}, el("td", {}, "Outfit cost"), s.outfitCost.map(v => el("td", {}, v))),
    el("tr", {}, el("td", {}, `Drones ${s.drones} at ${s.dronePrice} · land ${s.landPrice}`)));
}

function addLog(e) {
  const t = describe(e);
  if (!t) return;
  const li = el("li", { class: e.t === "Score" || e.t === "RoundEventHappened" ? "round" : "" }, t);
  $("log").prepend(li);
  while ($("log").children.length > 200) $("log").lastChild.remove();
}
function describe(e) {
  switch (e.t) {
    case "Draft": return `${nameOf(e.seat)} drafted plot ${e.plot}.`;
    case "LandSold": return `Plot ${e.plot} sold to ${nameOf(e.buyer)} for ${e.price}.`;
    case "LandUnsold": return `Nobody bid on plot ${e.plot}.`;
    case "QuickSale": return `${nameOf(e.seat)} sold plot ${e.plot} to the store for ${e.price}.`;
    case "Turn": return `${nameOf(e.seat)} gets a ${Math.round(e.seconds)}s turn${e.starving ? " (starving)" : ""}.`;
    case "TurnAction": return `${nameOf(e.seat)}: ${e.action}${e.plot >= 0 ? ` (plot ${e.plot})` : ""}${e.amount ? ` ${e.amount}` : ""}.`;
    case "PersonalEventHappened": return `${nameOf(e.seat)}: ${e.id} (${e.amount}).`;
    case "RoundEventHappened": return `Colony news: ${e.detail || e.kind}.`;
    case "Production": return e.shortage ? `${nameOf(e.seat)} was short of energy on ${e.shortage} drone(s).` : null;
    case "Trade": return null;
    case "Auction": return e.skipped ? `No ${e.good} auction (nothing to trade).` : `${cap(e.good)}: ${e.units} sold, price now ${e.newPrice}.`;
    case "Score": return e.rank === 1 ? `Round ${e.round} scores: ${nameOf(e.seat)} leads with ${e.total}.` : null;
    case "GameOver": return `Game over. Colony ${e.colony}: ${e.verdict}. ${nameOf(e.winner)} ${e.firstFounder ? "is First Founder" : "wins"}.`;
    default: return null;
  }
}

// ---------------------------------------------------------------- start

(async () => {
  status("Connecting…");
  try { await hub.start(); status(""); }
  catch { status("Can't reach the server."); return; }
  const tokens = JSON.parse(store.get("drone.tokens") || "{}");
  if (linkCode && me.name && tokens[linkCode[1].toUpperCase()]) {
    try { await join(linkCode[1]); } catch { show("join"); }
  }
})();
