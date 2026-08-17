namespace GameFlow.Infrastructure.Runtime.Web.Overlay;

/// <summary>
/// The OBS overlay page, embedded as a string constant — same reasoning
/// as <see cref="WebControllerAssets"/>: one self-contained document, no
/// external requests. Art is the only thing fetched, and it comes from
/// this server.
///
/// <para>
/// The page is a tree-walker and nothing more. It knows what a
/// <c>slide</c> node is; it does not know what a trigger is, and it never
/// evaluates an expression — see <see cref="OverlayProgram"/> for why
/// that split exists. Its draw cases mirror
/// <c>ThemeSurface.RenderNode</c>, which is the contract that keeps the
/// overlay and the app's own panels drawing a theme the same way.
/// </para>
///
/// <para>
/// <b>The background is transparent on purpose</b> and must stay that
/// way: OBS composites a browser source over the scene below it, so any
/// opaque body would show up as a black box around the controller. The
/// <c>bg</c> query parameter exists to put a colour back for looking at
/// the page in an ordinary browser, where transparent-over-nothing reads
/// as "the page is broken".
/// </para>
/// </summary>
internal static class OverlayAssets
{
    internal const string OverlayPage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>GameFlow Overlay</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
html,body{width:100%;height:100%;overflow:hidden;background:transparent}
#c{display:block;position:fixed;inset:0;width:100%;height:100%}
#msg{position:fixed;left:50%;top:50%;transform:translate(-50%,-50%);
  font:13px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;
  color:#e6edf3;background:rgba(11,15,22,.82);border:1px solid #263041;
  border-radius:8px;padding:10px 16px;text-align:center;max-width:80vw}
#msg.hide{display:none}
</style>
</head>
<body>
<canvas id="c"></canvas>
<div id="msg">Connecting…</div>
<script>
(function(){
"use strict";

var q = new URLSearchParams(location.search);
var canvas = document.getElementById("c");
var ctx = canvas.getContext("2d");
var msg = document.getElementById("msg");

// Transparency is the default because that is what OBS needs. A colour
// here is for looking at the page in a normal browser, where a
// transparent page over a white tab reads as a broken one.
var bg = q.get("bg");
if (bg) { document.body.style.background = bg.charAt(0) === "#" ? bg : "#" + bg; }
var quiet = q.get("quiet") === "1";

function say(text) {
  if (quiet || !text) { msg.className = "hide"; return; }
  msg.textContent = text;
  msg.className = "";
}

// ── State ────────────────────────────────────────────────────────────
var program = null;      // the compiled theme
var values = [];         // this frame's evaluated expressions
var pads = [];           // this frame's touch contacts, per trailpad
var light = null;        // this frame's lightbar colour
var images = [];         // Image objects, index-aligned with program.images
var dirty = false;       // a frame arrived since the last paint

// ── Canvas sizing ────────────────────────────────────────────────────
// The canvas backing store is sized in device pixels so the art is not
// resampled twice; the theme's own coordinate space is then scaled to
// fit, letterboxed, so a browser source of any size shows the whole
// controller at the right aspect.
function resize() {
  var ratio = window.devicePixelRatio || 1;
  canvas.width = Math.max(1, Math.round(window.innerWidth * ratio));
  canvas.height = Math.max(1, Math.round(window.innerHeight * ratio));
  dirty = true;
}
window.addEventListener("resize", resize);
resize();

// ── Drawing ──────────────────────────────────────────────────────────
function sizeOf(node, img) {
  // 0 means "the bitmap's own size" — the theme schema's convention.
  // Resolved here rather than server-side because the server never
  // decodes the PNG.
  return [
    node.w > 0 ? node.w : (img ? img.naturalWidth : 0),
    node.h > 0 ? node.h : (img ? img.naturalHeight : 0)
  ];
}

function ready(index) {
  var img = images[index];
  return (img && img.complete && img.naturalWidth > 0) ? img : null;
}

// Lightbar masks, tinted to the live colour. Cached because the tint is
// a whole offscreen composite and the colour changes rarely — usually
// never — while this runs at the display rate.
var tintCache = {};
function tinted(img, w, h, color) {
  var key = img.src + "|" + w + "|" + h + "|" + color;
  var hit = tintCache[key];
  if (hit) { return hit; }

  var off = document.createElement("canvas");
  off.width = Math.max(1, Math.ceil(w));
  off.height = Math.max(1, Math.ceil(h));
  var g = off.getContext("2d");
  g.drawImage(img, 0, 0, off.width, off.height);
  // source-in keeps the mask's alpha and replaces its colour, which is
  // what PushOpacityMask + FillRectangle does in the app.
  g.globalCompositeOperation = "source-in";
  g.fillStyle = color;
  g.fillRect(0, 0, off.width, off.height);

  // A theme has a handful of lightbar masks and one colour at a time.
  // The bound is here so a strobing colour cannot grow this forever.
  var keys = Object.keys(tintCache);
  if (keys.length > 32) { delete tintCache[keys[0]]; }
  tintCache[key] = off;
  return off;
}

function value(index) {
  return (index >= 0 && index < values.length) ? values[index] : 0;
}

function drawNode(node) {
  ctx.save();
  ctx.translate(node.x || 0, node.y || 0);
  if (node.r) { ctx.rotate(node.r * Math.PI / 180); }

  switch (node.kind) {
    case "show":
      // The whole subtree is skipped, which is what makes an idle
      // controller cheap to draw: every press graphic lives in one of
      // these.
      if (value(node.value) === 0) { ctx.restore(); return; }
      break;

    case "slide":
      ctx.translate(value(node.valueX), value(node.valueY));
      var rot = value(node.valueR);
      if (rot) { ctx.rotate(rot * Math.PI / 180); }
      break;

    case "trail":
      // An up finger hides the marker AND its children, the way the
      // renderer's early return does.
      if (!pads[node.pad]) { ctx.restore(); return; }
      drawTrail(node);
      break;

    case "img":
      drawImage(node);
      break;

    case "light":
      drawLight(node);
      break;

    case "bar":
      drawBar(node);
      break;
  }

  var children = node.children;
  if (children) {
    for (var i = 0; i < children.length; i++) { drawNode(children[i]); }
  }
  ctx.restore();
}

function drawImage(node) {
  var img = ready(node.image);
  if (!img) { return; }
  var wh = sizeOf(node, img);
  ctx.drawImage(img, node.center ? -wh[0] / 2 : 0, node.center ? -wh[1] / 2 : 0, wh[0], wh[1]);
}

function drawLight(node) {
  if (!light) { return; }
  var img = ready(node.image);
  if (!img) { return; }
  var wh = sizeOf(node, img);
  if (wh[0] <= 0 || wh[1] <= 0) { return; }
  ctx.drawImage(
    tinted(img, wh[0], wh[1], light),
    node.center ? -wh[0] / 2 : 0,
    node.center ? -wh[1] / 2 : 0,
    wh[0], wh[1]);
}

// One marker per trailpad node — null when that node's finger is up. A
// theme showing two fingers declares two of these, each reading its own
// contact, so there is nothing to loop over here.
function drawTrail(node) {
  var at = pads[node.pad];
  if (!at) { return; }

  var img = ready(node.image);
  if (!img) { return; }
  var wh = sizeOf(node, img);

  ctx.translate(at[0], at[1]);
  ctx.drawImage(img, -wh[0] / 2, -wh[1] / 2, wh[0], wh[1]);
}

function drawBar(node) {
  var min = value(node.min);
  var max = value(node.max);
  var range = max - min;
  var ratio = range === 0 ? 0 : Math.min(1, Math.max(0, (value(node.value) - min) / range));

  var w = node.w, h = node.h;
  var dx = node.center ? -w / 2 : 0;
  var dy = node.center ? -h / 2 : 0;

  var fx = dx, fy = dy, fw = w, fh = h;
  switch (node.dir) {
    case "left":  fx = dx + w * (1 - ratio); fw = w * ratio; break;
    case "up":    fy = dy + h * (1 - ratio); fh = h * ratio; break;
    case "down":  fh = h * ratio; break;
    default:      fw = w * ratio; break;   // right
  }

  var img = ready(node.image);
  if (img) {
    // The whole image is drawn and clipped to the fill, so a
    // trigger-shaped PNG appears to fill in rather than being squashed.
    ctx.save();
    ctx.beginPath();
    ctx.rect(fx, fy, fw, fh);
    ctx.clip();
    ctx.drawImage(img, dx, dy, w, h);
    ctx.restore();
    return;
  }

  if (node.bg) { ctx.fillStyle = node.bg; ctx.fillRect(dx, dy, w, h); }
  if (node.fg && ratio > 0) { ctx.fillStyle = node.fg; ctx.fillRect(fx, fy, fw, fh); }
}

function paint() {
  requestAnimationFrame(paint);
  if (!program || !dirty) { return; }
  dirty = false;

  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, canvas.width, canvas.height);

  var scale = Math.min(canvas.width / program.width, canvas.height / program.height);
  ctx.setTransform(
    scale, 0, 0, scale,
    (canvas.width - program.width * scale) / 2,
    (canvas.height - program.height * scale) / 2);

  for (var i = 0; i < program.nodes.length; i++) { drawNode(program.nodes[i]); }
}
requestAnimationFrame(paint);

// ── Socket ───────────────────────────────────────────────────────────
// Everything the app knows about which controller to show is already in
// this page's query string, so the socket reuses it verbatim.
function socketUrl() {
  var scheme = location.protocol === "https:" ? "wss:" : "ws:";
  return scheme + "//" + location.host + "/overlay/ws" + location.search;
}

var retry = null;
function connect() {
  var socket;
  try { socket = new WebSocket(socketUrl()); }
  catch (e) { schedule(); return; }

  socket.onmessage = function (event) {
    var data;
    try { data = JSON.parse(event.data); } catch (e) { return; }

    if (data.type === "program") {
      program = data;
      tintCache = {};
      images = data.images.map(function (src) {
        var img = new Image();
        // A frame can arrive before the art does; drawing skips images
        // that are not ready yet, so this only needs to ask for a repaint.
        img.onload = function () { dirty = true; };
        img.src = src;
        return img;
      });
      say(null);
      dirty = true;
      return;
    }

    if (data.type === "frame") {
      values = data.v || [];
      pads = data.pads || [];
      light = data.light || null;
      dirty = true;
      return;
    }

    if (data.type === "error") {
      program = null;
      say(data.message || "Overlay error");
    }
  };

  socket.onclose = function () {
    // Deliberately keeps the last frame on screen. OBS reloads a source
    // on scene changes and GameFlow restarts during a stream; blanking
    // the controller for a second each time is worse than showing a
    // still one.
    say("Reconnecting…");
    schedule();
  };

  socket.onerror = function () { try { socket.close(); } catch (e) {} };
}

function schedule() {
  if (retry) { return; }
  retry = setTimeout(function () { retry = null; connect(); }, 1500);
}

connect();
})();
</script>
</body>
</html>
""";
}
