namespace LyricGlow.Services;

/// <summary>
/// Página que OBS muestra como "Fuente de navegador". Fondo transparente;
/// se personaliza con parámetros en la URL, por ejemplo:
/// http://localhost:7531/?size=56&amp;align=left&amp;cover=0&amp;next=0
/// </summary>
static class OverlayPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Glowline overlay</title>
<style>
  :root { --size: 52px; --align: center; --justify: center; }
  html, body { margin: 0; height: 100%; background: transparent; overflow: hidden; }
  body {
    font-family: "Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif;
    color: #fff;
    display: flex; flex-direction: column; justify-content: flex-end;
    padding: 4vh 5vw; box-sizing: border-box;
    text-shadow: 0 2px 18px rgba(0,0,0,.65), 0 1px 3px rgba(0,0,0,.8);
  }
  .track { display: flex; align-items: center; gap: 14px; justify-content: var(--justify);
           margin-bottom: 18px; opacity: .92; transition: opacity .4s; }
  .track img { width: 64px; height: 64px; border-radius: 10px; object-fit: cover;
               box-shadow: 0 6px 24px rgba(0,0,0,.45); }
  .meta { text-align: left; }
  .title { font-size: calc(var(--size) * .42); font-weight: 700; }
  .artist { font-size: calc(var(--size) * .34); opacity: .75; margin-top: 2px; }
  .line { font-size: var(--size); font-weight: 700; line-height: 1.18; text-align: var(--align);
          transition: opacity .25s ease, transform .25s ease; min-height: 1.2em; }
  .roman { font-size: calc(var(--size) * .45); font-style: italic; opacity: .78; text-align: var(--align); margin-top: 6px; }
  .next { font-size: calc(var(--size) * .48); opacity: .5; text-align: var(--align); margin-top: 10px; }
  .fade { opacity: 0; transform: translateY(8px); }
  .hidden { display: none; }
  body.box .wrap { background: rgba(10,10,14,.72); border-radius: 18px; padding: 22px 28px;
                   text-shadow: none; }
  body.light { color: #111; text-shadow: 0 1px 10px rgba(255,255,255,.7); }
</style>
</head>
<body>
<div class="wrap">
  <div class="track" id="track">
    <img id="cover" alt="">
    <div class="meta"><div class="title" id="title"></div><div class="artist" id="artist"></div></div>
  </div>
  <div class="line" id="line"></div>
  <div class="roman hidden" id="roman"></div>
  <div class="next" id="next"></div>
</div>
<script>
  const q = new URLSearchParams(location.search);
  const root = document.documentElement.style;
  const size = parseInt(q.get("size") || "52", 10);
  if (size >= 12 && size <= 200) root.setProperty("--size", size + "px");
  const align = q.get("align");
  if (align === "left" || align === "right") {
    root.setProperty("--align", align);
    root.setProperty("--justify", align === "left" ? "flex-start" : "flex-end");
  }
  if (q.get("cover") === "0") document.getElementById("cover").classList.add("hidden");
  if (q.get("track") === "0") document.getElementById("track").classList.add("hidden");
  if (q.get("box") === "1") document.body.classList.add("box");
  if (q.get("theme") === "light") document.body.classList.add("light");
  const showNext = q.get("next") !== "0";

  const el = id => document.getElementById(id);
  let lastLine = null, lastCover = -1;

  function setText(node, text) {
    if (node.textContent === text) return;
    node.textContent = text;
  }

  async function poll() {
    try {
      const res = await fetch("/state", { cache: "no-store" });
      const s = await res.json();
      setText(el("title"), s.title || "");
      setText(el("artist"), s.artist || "");
      el("track").style.opacity = s.title ? "" : "0";
      if (s.cover !== lastCover) {
        lastCover = s.cover;
        el("cover").src = "/cover?v=" + s.cover;
      }
      const line = s.hasLyrics ? (s.line || "♪") : "";
      if (line !== lastLine) {
        lastLine = line;
        const node = el("line");
        node.classList.add("fade");
        setTimeout(() => { node.textContent = line; node.classList.remove("fade"); }, 180);
      }
      setText(el("roman"), s.romanized || "");
      el("roman").classList.toggle("hidden", !s.romanized);
      setText(el("next"), showNext ? (s.next || "") : "");
    } catch (e) {
      // Glowline cerrado: se reintenta en silencio.
    }
    setTimeout(poll, 150);
  }
  el("cover").onerror = () => { el("cover").style.visibility = "hidden"; };
  el("cover").onload = () => { el("cover").style.visibility = ""; };
  poll();
</script>
</body>
</html>
""";
}
