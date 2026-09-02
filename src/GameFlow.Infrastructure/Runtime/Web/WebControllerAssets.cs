namespace GameFlow.Infrastructure.Runtime.Web;

/// <summary>
/// The browser gamepad page, embedded as a string constant.
///
/// <para>
/// Deliberately one self-contained document — all CSS and JavaScript
/// inline, zero external requests. A phone loading this over the LAN
/// often has no working internet route at all (or is on a guest network
/// that blocks it), so any CDN reference would leave the page half-dead
/// exactly when it's needed.
/// </para>
///
/// <para>
/// The BIT table in the script is a wire contract shared with
/// <see cref="WebControllerProtocol"/> — change one and you must change
/// the other in the same commit.
/// </para>
/// </summary>
internal static class WebControllerAssets
{
    internal const string ControllerPage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover">
<meta name="mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-capable" content="yes">
<title>GameFlow Controller</title>
<style>
*{margin:0;padding:0;box-sizing:border-box;-webkit-tap-highlight-color:transparent;-webkit-user-select:none;user-select:none;touch-action:none}
html,body{width:100%;height:100%;overflow:hidden;background:#0b0f16;color:#e6edf3;font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif}
#app{position:fixed;inset:0;display:flex;flex-direction:column}
#bar{display:flex;align-items:center;gap:10px;padding:6px 12px;font-size:12px;background:#0e131c;border-bottom:1px solid #1c2432;flex:0 0 auto}
#dot{width:8px;height:8px;border-radius:50%;background:#e5534b;transition:background .2s}
#dot.on{background:#3fb950}
#pad-label{color:#ff7a1a;font-weight:600;letter-spacing:.5px}
#bar button{background:#131a26;color:#e6edf3;border:1px solid #263041;border-radius:6px;padding:4px 10px;font-size:12px;margin-left:auto}
#bar button:disabled{color:#3fb950;border-color:#1f3d29}
#bar select{background:#131a26;color:#e6edf3;border:1px solid #263041;border-radius:6px;padding:4px 8px;font-size:12px}
#surface{position:relative;flex:1 1 auto}
.zone{position:absolute}
.btn{position:absolute;display:flex;align-items:center;justify-content:center;border-radius:50%;
  background:linear-gradient(180deg,#1a2230,#131a26);border:1px solid #2a3446;color:#8b98a8;
  font-size:15px;font-weight:600;transition:background .05s,border-color .05s,color .05s}
.btn.on{background:linear-gradient(180deg,#ff8c33,#e8590c);border-color:#ff7a1a;color:#0b0f16}
.pill{border-radius:12px}
.stick-well{position:absolute;border-radius:50%;background:radial-gradient(circle at 50% 45%,#151d2a,#0f1520);border:1px solid #26314a}
.stick-nub{position:absolute;width:34%;height:34%;border-radius:50%;left:33%;top:33%;
  background:radial-gradient(circle at 40% 35%,#ff9a4d,#e8590c);border:1px solid #ff7a1a;pointer-events:none}
.trig{position:absolute;border-radius:10px;background:#131a26;border:1px solid #2a3446;overflow:hidden}
.trig-fill{position:absolute;left:0;bottom:0;width:100%;height:0%;background:linear-gradient(0deg,#e8590c,#ff9a4d)}
.trig-label{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;font-size:13px;font-weight:700;color:#8b98a8}
#touchpad{position:absolute;border-radius:12px;background:#0f1520;border:1px dashed #2a3446;
  display:flex;align-items:center;justify-content:center;color:#3d4a5c;font-size:13px}
#touchpad.on{border-color:#ff7a1a;border-style:solid}
#msg{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;
  background:rgba(11,15,22,.94);font-size:15px;color:#8b98a8;text-align:center;padding:24px;z-index:20}
#msg.hide{display:none}
</style>
</head>
<body>
<div id="app">
  <div id="bar">
    <div id="dot"></div>
    <span id="status">connecting…</span>
    <span id="pad-label"></span>
    <button id="motion">Enable motion</button>
    <select id="layout">
      <option value="xbox">Xbox 360</option>
      <option value="ds4">DualShock 4</option>
      <option value="touch">Touchpad</option>
    </select>
  </div>
  <div id="surface"></div>
</div>
<div id="msg">Connecting to GameFlow…</div>
<script>
"use strict";

// Bit order is a FIXED wire contract shared with WebControllerProtocol.cs
// on the server. Deliberately not tied to the server's ButtonId enum
// ordering — that can be reordered during refactoring, and this must not
// silently start reporting the wrong buttons if it ever is.
var BIT = {
  south:0, east:1, west:2, north:3,
  lb:4, rb:5, back:6, start:7, guide:8,
  l3:9, r3:10,
  up:11, down:12, left:13, right:14, touchpad:15
};

var state = { buttons:0, lx:0, ly:0, rx:0, ry:0, lt:0, rt:0, touch:{},
              gp:0, gy:0, gr:0, ax:0, ay:0, az:0, gyro:0 };
var ws = null, connected = false, padIndex = -1, dirty = true, rumbleTimer = null;
var surface = document.getElementById("surface");
var msgEl = document.getElementById("msg");
var dotEl = document.getElementById("dot");
var statusEl = document.getElementById("status");
var padLabelEl = document.getElementById("pad-label");

// Stable for this browser tab across WebSocket reconnects. sessionStorage
// keeps two tabs independent (localStorage would make them steal one pad
// from each other) while still letting a dropped Wi-Fi connection reclaim
// the same GameFlow device id and slot assignment.
function newClientId() {
  if (window.crypto && typeof window.crypto.randomUUID === "function") {
    return window.crypto.randomUUID();
  }
  return "web-" + Date.now().toString(36) + "-" + Math.random().toString(36).slice(2);
}
var clientId;
try {
  clientId = sessionStorage.getItem("gameflow-client-id");
  if (!clientId) {
    clientId = newClientId();
    sessionStorage.setItem("gameflow-client-id", clientId);
  }
} catch (e) {
  clientId = newClientId();
}

function setBit(bit, on) {
  var mask = 1 << bit;
  var next = on ? (state.buttons | mask) : (state.buttons & ~mask);
  if (next !== state.buttons) { state.buttons = next; dirty = true; }
}

// The portable Vibration API is on/off, not variable-amplitude. Model motor
// strength as the duty cycle inside a short period and keep repeating until
// the server sends its explicit zero state. This preserves sustained game
// rumble without leaving the phone buzzing after a disconnect.
function stopRumble() {
  if (rumbleTimer !== null) {
    clearInterval(rumbleTimer);
    rumbleTimer = null;
  }
  if (navigator.vibrate) { navigator.vibrate(0); }
}

function applyRumble(strength, periodMs) {
  stopRumble();
  if (!navigator.vibrate || strength <= 0.02) { return; }

  var period = Math.max(100, Math.min(1000, periodMs || 250));
  var pulse = Math.max(10, Math.round(period * Math.min(1, strength)));
  var vibrate = function () { navigator.vibrate(pulse); };
  vibrate();
  rumbleTimer = setInterval(vibrate, period);
}

// ---- Layout definitions. Percentages of the play surface, so one
// definition works on any phone size or aspect ratio. ----
function layoutFor(kind) {
  var faceLabels = kind === "ds4"
    ? { south:"\u2715", east:"\u25CB", west:"\u25A1", north:"\u25B3" }
    : { south:"A", east:"B", west:"X", north:"Y" };

  var items = [
    { t:"stick", id:"ls", x:6,  y:38, w:26, h:46, bit:BIT.l3 },
    { t:"stick", id:"rs", x:68, y:38, w:26, h:46, bit:BIT.r3 },

    { t:"dpad",  x:36, y:52, w:18, h:34 },

    { t:"btn", bit:BIT.north, label:faceLabels.north, x:76, y:6,  w:9,  h:16 },
    { t:"btn", bit:BIT.west,  label:faceLabels.west,  x:66, y:16, w:9,  h:16 },
    { t:"btn", bit:BIT.east,  label:faceLabels.east,  x:86, y:16, w:9,  h:16 },
    { t:"btn", bit:BIT.south, label:faceLabels.south, x:76, y:26, w:9,  h:16 },

    { t:"btn", bit:BIT.lb, label:"LB", x:4,  y:4, w:13, h:11, pill:true },
    { t:"btn", bit:BIT.rb, label:"RB", x:83, y:4, w:13, h:11, pill:true },

    { t:"trig", axis:"lt", label:"LT", x:19, y:4, w:11, h:13 },
    { t:"trig", axis:"rt", label:"RT", x:70, y:4, w:11, h:13 },

    { t:"btn", bit:BIT.back,  label:"\u2630", x:38, y:8,  w:8, h:14, pill:true },
    { t:"btn", bit:BIT.start, label:"\u25B6", x:54, y:8,  w:8, h:14, pill:true },
    { t:"btn", bit:BIT.guide, label:"\u2302", x:46, y:26, w:8, h:14 }
  ];

  if (kind === "touch") {
    // Touchpad layout: one large surface, minimal buttons — the pad
    // itself is the point, matching the desktop Touchpad Overlay.
    items = [
      { t:"touchpad", x:8, y:20, w:84, h:64 },
      { t:"btn", bit:BIT.back,  label:"\u2630", x:38, y:6, w:8, h:12, pill:true },
      { t:"btn", bit:BIT.start, label:"\u25B6", x:54, y:6, w:8, h:12, pill:true }
    ];
  } else if (kind === "ds4") {
    items.push({ t:"touchpad", x:36, y:6, w:28, h:16 });
    // The DS4's touchpad sits where Xbox puts Back/Start, so move those out.
    items = items.filter(function (i) { return !(i.bit === BIT.back || i.bit === BIT.start); });
    items.push({ t:"btn", bit:BIT.back,  label:"\u2630", x:30, y:26, w:7, h:12, pill:true });
    items.push({ t:"btn", bit:BIT.start, label:"\u25B6", x:63, y:26, w:7, h:12, pill:true });
  }
  return items;
}

var pointerOwners = {}; // pointerId -> handler object

function buildLayout(kind) {
  surface.innerHTML = "";
  pointerOwners = {};
  state.buttons = 0; state.lx = 0; state.ly = 0; state.rx = 0; state.ry = 0; state.lt = 0; state.rt = 0;
  state.touch = {};
  dirty = true;

  layoutFor(kind).forEach(function (item) {
    if (item.t === "btn") { makeButton(item); }
    else if (item.t === "stick") { makeStick(item); }
    else if (item.t === "trig") { makeTrigger(item); }
    else if (item.t === "dpad") { makeDpad(item); }
    else if (item.t === "touchpad") { makeTouchpad(item); }
  });
}

function place(el, item) {
  el.style.left = item.x + "%";
  el.style.top = item.y + "%";
  el.style.width = item.w + "%";
  el.style.height = item.h + "%";
}

function makeButton(item) {
  var el = document.createElement("div");
  el.className = "btn" + (item.pill ? " pill" : "");
  el.textContent = item.label;
  place(el, item);
  surface.appendChild(el);
  attach(el, {
    down: function () { el.classList.add("on"); setBit(item.bit, true); },
    up:   function () { el.classList.remove("on"); setBit(item.bit, false); }
  });
}

function makeTrigger(item) {
  var el = document.createElement("div");
  el.className = "trig";
  place(el, item);
  var fill = document.createElement("div"); fill.className = "trig-fill";
  var label = document.createElement("div"); label.className = "trig-label"; label.textContent = item.label;
  el.appendChild(fill); el.appendChild(label);
  surface.appendChild(el);

  // Analog: how far UP the trigger you slide is how hard it's pulled,
  // so a partial pull is possible on a touchscreen.
  function apply(ev) {
    var r = el.getBoundingClientRect();
    var v = (r.bottom - ev.clientY) / r.height;
    v = Math.max(0, Math.min(1, v));
    if (state[item.axis] !== v) { state[item.axis] = v; dirty = true; }
    fill.style.height = (v * 100) + "%";
  }
  attach(el, {
    down: apply,
    move: apply,
    up: function () { state[item.axis] = 0; dirty = true; fill.style.height = "0%"; }
  });
}

function makeStick(item) {
  var well = document.createElement("div");
  well.className = "stick-well";
  place(well, item);
  var nub = document.createElement("div"); nub.className = "stick-nub";
  well.appendChild(nub);
  surface.appendChild(well);

  var ax = item.id === "ls" ? "lx" : "rx";
  var ay = item.id === "ls" ? "ly" : "ry";
  var anchor = null;

  function apply(ev) {
    var r = well.getBoundingClientRect();
    // Anchor where the finger first lands, exactly like the desktop
    // touchpad stick mode — not pinned to the well's centre.
    if (!anchor) { anchor = { x: ev.clientX, y: ev.clientY }; }
    var radius = r.width / 2;
    var dx = (ev.clientX - anchor.x) / radius;
    // Screen Y grows downward, stick Y grows upward.
    var dy = -(ev.clientY - anchor.y) / radius;
    var mag = Math.sqrt(dx * dx + dy * dy);
    if (mag > 1) { dx /= mag; dy /= mag; }
    if (state[ax] !== dx || state[ay] !== dy) { state[ax] = dx; state[ay] = dy; dirty = true; }
    nub.style.left = (33 + dx * 26) + "%";
    nub.style.top = (33 - dy * 26) + "%";
  }

  attach(well, {
    down: apply,
    move: apply,
    up: function () {
      anchor = null;
      state[ax] = 0; state[ay] = 0; dirty = true;
      nub.style.left = "33%"; nub.style.top = "33%";
    }
  });
}

function makeDpad(item) {
  var wrap = document.createElement("div");
  wrap.className = "zone";
  place(wrap, item);
  wrap.style.borderRadius = "50%";
  wrap.style.background = "radial-gradient(circle at 50% 45%,#151d2a,#0f1520)";
  wrap.style.border = "1px solid #26314a";
  surface.appendChild(wrap);

  var dirs = [BIT.up, BIT.down, BIT.left, BIT.right];

  // 8-way by wedge: diagonals hold two adjacent directions at once,
  // the same convention the desktop wedge D-pad uses.
  function apply(ev) {
    var r = wrap.getBoundingClientRect();
    var dx = (ev.clientX - (r.left + r.width / 2)) / (r.width / 2);
    var dy = (ev.clientY - (r.top + r.height / 2)) / (r.height / 2);
    var mag = Math.sqrt(dx * dx + dy * dy);
    dirs.forEach(function (b) { setBit(b, false); });
    if (mag < 0.25) { return; }
    var deg = Math.atan2(-dy, dx) * 180 / Math.PI;
    if (deg < 0) { deg += 360; }
    if (deg >= 337.5 || deg < 22.5) { setBit(BIT.right, true); }
    else if (deg < 67.5)  { setBit(BIT.right, true); setBit(BIT.up, true); }
    else if (deg < 112.5) { setBit(BIT.up, true); }
    else if (deg < 157.5) { setBit(BIT.up, true); setBit(BIT.left, true); }
    else if (deg < 202.5) { setBit(BIT.left, true); }
    else if (deg < 247.5) { setBit(BIT.left, true); setBit(BIT.down, true); }
    else if (deg < 292.5) { setBit(BIT.down, true); }
    else { setBit(BIT.down, true); setBit(BIT.right, true); }
  }

  attach(wrap, {
    down: apply,
    move: apply,
    up: function () { dirs.forEach(function (b) { setBit(b, false); }); }
  });
}

function makeTouchpad(item) {
  var el = document.createElement("div");
  el.id = "touchpad";
  el.textContent = "TOUCHPAD";
  place(el, item);
  surface.appendChild(el);
  function apply(ev) {
    var r = el.getBoundingClientRect();
    var x = Math.max(0, Math.min(1, (ev.clientX - r.left) / r.width));
    var y = Math.max(0, Math.min(1, (ev.clientY - r.top) / r.height));
    var pressure = typeof ev.pressure === "number" && ev.pressure > 0 ? ev.pressure : 1;
    state.touch[ev.pointerId] = { i:ev.pointerId, x:x, y:y, p:Math.min(1, pressure) };
    dirty = true;
  }

  attach(el, {
    down: function (ev) {
      el.classList.add("on");
      setBit(BIT.touchpad, true);
      apply(ev);
    },
    move: apply,
    up: function (ev) {
      delete state.touch[ev.pointerId];
      var stillDown = Object.keys(state.touch).length > 0;
      el.classList.toggle("on", stillDown);
      setBit(BIT.touchpad, stillDown);
      dirty = true;
    }
  });
}

// ---- Multi-touch routing. Each pointer id is owned by whichever
// control it started on, so sliding off a button doesn't leak the press
// onto a neighbour, and several fingers work truly independently. ----
function attach(el, handlers) {
  el.addEventListener("pointerdown", function (ev) {
    ev.preventDefault();
    el.setPointerCapture(ev.pointerId);
    pointerOwners[ev.pointerId] = handlers;
    if (handlers.down) { handlers.down(ev); }
  });
  el.addEventListener("pointermove", function (ev) {
    if (pointerOwners[ev.pointerId] === handlers && handlers.move) { handlers.move(ev); }
  });
  function release(ev) {
    if (pointerOwners[ev.pointerId] === handlers) {
      delete pointerOwners[ev.pointerId];
      if (handlers.up) { handlers.up(ev); }
    }
  }
  el.addEventListener("pointerup", release);
  el.addEventListener("pointercancel", release);
}

// ---- Motion sensors ----
// A phone has a real gyroscope and accelerometer, so it can drive the
// same gyro pipeline a DualSense does. Browsers report rotation in
// DEGREES/second while the desktop side expects SDL's convention of
// RADIANS/second, so convert here rather than special-casing the phone
// downstream. iOS 13+ additionally requires an explicit permission
// prompt triggered by a user gesture — hence the button below.
var DEG2RAD = Math.PI / 180;

function onMotion(ev) {
  var r = ev.rotationRate;
  if (r) {
    // Browser axes -> SDL axes. beta is rotation about the device's X
    // (pitch), alpha about Z as held (yaw when the phone is upright in
    // landscape), gamma about Y (roll).
    state.gp = (r.beta  || 0) * DEG2RAD;
    state.gy = (r.alpha || 0) * DEG2RAD;
    state.gr = (r.gamma || 0) * DEG2RAD;
    state.gyro = 1;
  }
  var g = ev.accelerationIncludingGravity;
  if (g) {
    state.ax = g.x || 0;
    state.ay = g.y || 0;
    state.az = g.z || 0;
    state.gyro = 1;
  }
  dirty = true;
}

function startMotion() {
  if (typeof DeviceMotionEvent === "undefined") { return; }
  // iOS gates sensors behind an explicit permission request that MUST
  // originate from a user gesture; Android and desktop just work.
  if (typeof DeviceMotionEvent.requestPermission === "function") {
    DeviceMotionEvent.requestPermission().then(function (result) {
      if (result === "granted") {
        window.addEventListener("devicemotion", onMotion);
        motionBtn.textContent = "Motion on";
        motionBtn.disabled = true;
      } else {
        motionBtn.textContent = "Motion denied";
      }
    }).catch(function () { motionBtn.textContent = "Motion failed"; });
  } else {
    window.addEventListener("devicemotion", onMotion);
    motionBtn.textContent = "Motion on";
    motionBtn.disabled = true;
  }
}

var motionBtn = document.getElementById("motion");
motionBtn.addEventListener("click", startMotion);

// ---- Transport ----
function connect() {
  var proto = location.protocol === "https:" ? "wss:" : "ws:";
  ws = new WebSocket(proto + "//" + location.host + "/ws?client=" + encodeURIComponent(clientId));

  ws.onopen = function () {
    connected = true;
    dotEl.classList.add("on");
    statusEl.textContent = "connected";
    msgEl.classList.add("hide");
  };

  ws.onmessage = function (ev) {
    var m;
    try { m = JSON.parse(ev.data); } catch (e) { return; }
    if (typeof m.pad === "number") {
      padIndex = m.pad;
      padLabelEl.textContent = m.pad >= 0 ? ("PAD #" + (m.pad + 1)) : "";
      if (m.pad < 0) {
        msgEl.textContent = "All 16 controller slots are in use.";
        msgEl.classList.remove("hide");
      }
    }
    if (m.rumble && navigator.vibrate) {
      var strength = Math.max(m.rumble.low || 0, m.rumble.high || 0);
      applyRumble(strength, m.rumble.ms);
    }
  };

  ws.onclose = function () {
    connected = false;
    stopRumble();
    dotEl.classList.remove("on");
    statusEl.textContent = "reconnecting…";
    padLabelEl.textContent = "";
    msgEl.textContent = "Connection lost. Reconnecting…";
    msgEl.classList.remove("hide");
    setTimeout(connect, 1000);
  };

  ws.onerror = function () { if (ws) { ws.close(); } };
}

// Send on a fixed cadence rather than per-touch-event: touch devices
// fire move events far faster than any game needs, and this keeps the
// socket from flooding on a busy Wi-Fi network.
function pump() {
  if (connected && dirty && ws && ws.readyState === 1) {
    ws.send(JSON.stringify({
      b: state.buttons,
      lx: +state.lx.toFixed(3), ly: +state.ly.toFixed(3),
      rx: +state.rx.toFixed(3), ry: +state.ry.toFixed(3),
      lt: +state.lt.toFixed(3), rt: +state.rt.toFixed(3),
      touch: Object.keys(state.touch).slice(0, 5).map(function (id) {
        var c = state.touch[id];
        return { i:c.i, x:+c.x.toFixed(4), y:+c.y.toFixed(4), p:+c.p.toFixed(3) };
      }),
      gyro: state.gyro,
      gp: +state.gp.toFixed(4), gy: +state.gy.toFixed(4), gr: +state.gr.toFixed(4),
      ax: +state.ax.toFixed(3), ay: +state.ay.toFixed(3), az: +state.az.toFixed(3)
    }));
    dirty = false;
  }
}
setInterval(pump, 16); // ~60 Hz

// Heartbeat so the server's staleness timer doesn't drop an idle-but-present phone.
setInterval(function () { dirty = true; }, 1000);

document.getElementById("layout").addEventListener("change", function (ev) {
  buildLayout(ev.target.value);
});

window.addEventListener("contextmenu", function (ev) { ev.preventDefault(); });

buildLayout("xbox");
connect();
</script>
</body>
</html>

""";
}
