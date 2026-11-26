// KaTeX 0.16.11。trust:false は必ず守る（\href などで何も開かない）
function renderOne(tex, display, fg, bg, px) {
  var o = document.getElementById('out');
  document.body.style.background = bg;
  o.style.color = fg;
  o.style.fontSize = px + 'px';
  o.style.padding = (px * 0.15) + 'px';   // 余白は字の大きさに比例（倍率を変えても見た目が同じ）
  o.textContent = '';
  try {
    katex.render(tex, o, { displayMode: display, throwOnError: true, strict: 'ignore', trust: false });
    var b = 0;
    if (!display) {   // 文中の式だけ、文字の基準線の位置を測る（独立行では要らない。足すと1行増える）
      var s = document.createElement('span');
      s.style.cssText = 'display:inline-block;width:0;height:0;vertical-align:baseline';
      o.appendChild(s);
      b = s.getBoundingClientRect().top - o.getBoundingClientRect().top;
    }
    var r = o.getBoundingClientRect();
    // 修正J42: 窓の中の大きさも返す（広げなくてよい時に、別に聞き直さない）
    return JSON.stringify({ ok: true, x: r.left, y: r.top, w: r.width, h: r.height, base: b, iw: window.innerWidth, ih: window.innerHeight });
  } catch (e) {
    return JSON.stringify({ ok: false, err: String((e && e.message) || e) });
  }
}

// 初回の式で Web フォントの読み込み待ちが起きると幅の測り違いで右が欠けるので、先に全部読んでおく
window.fontsOk = false;
Promise.all(Array.from(document.fonts).map(function (f) { return f.load().catch(function () {}); }))
  .then(function () { return document.fonts.ready; })
  .then(function () { window.fontsOk = true; });
