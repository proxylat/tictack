/* TicTack Documentation Controller */
(function () {
  function copyToClipboard(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text).catch(function () {
        return fallbackCopy(text);
      });
    }
    return fallbackCopy(text);
  }

  function fallbackCopy(text) {
    return new Promise(function (resolve) {
      var ta = document.createElement("textarea");
      ta.value = text;
      ta.style.position = "fixed";
      ta.style.left = "-9999px";
      document.body.appendChild(ta);
      ta.focus();
      ta.select();
      try { document.execCommand("copy"); } catch (e) {}
      document.body.removeChild(ta);
      resolve();
    });
  }

  /* Theme toggle */
  function initTheme() {
    var saved = localStorage.getItem("tictack-theme") || "dark";
    document.documentElement.setAttribute("data-theme", saved);
    var btn = document.getElementById("theme-btn");
    if (btn) {
      btn.textContent = saved === "light" ? "Theme: Light" : "Theme: Dark";
      btn.addEventListener("click", function () {
        var next = document.documentElement.getAttribute("data-theme") === "light" ? "dark" : "light";
        document.documentElement.setAttribute("data-theme", next);
        localStorage.setItem("tictack-theme", next);
        btn.textContent = next === "light" ? "Theme: Light" : "Theme: Dark";
      });
    }
  }

  /* Code blocks: tabs + copy button */
  function initCodeBlocks() {
    document.querySelectorAll(".code-block").forEach(function (block) {
      var copyBtn = block.querySelector(".copy-btn");
      if (copyBtn) {
        copyBtn.textContent = "Copy";
        copyBtn.addEventListener("click", function () {
          var pane = block.querySelector(".tab-pane.active pre") || block.querySelector("pre");
          if (!pane) return;
          copyToClipboard(pane.innerText).then(function () {
            copyBtn.classList.add("copied");
            copyBtn.textContent = "Copied";
            setTimeout(function () {
              copyBtn.classList.remove("copied");
              copyBtn.textContent = "Copy";
            }, 1500);
          });
        });
      }

      var tabs = block.querySelectorAll(".tab-btn");
      var panes = block.querySelectorAll(".tab-pane");
      tabs.forEach(function (btn, idx) {
        btn.addEventListener("click", function () {
          tabs.forEach(function (b) { b.classList.remove("active"); });
          panes.forEach(function (p) { p.classList.remove("active"); });
          btn.classList.add("active");
          if (panes[idx]) panes[idx].classList.add("active");
        });
      });
    });
  }

  /* Headings & Scroll-Spy TOC */
  function initUpdated() {
    var ledes = document.querySelectorAll(".article-header .article-lede");
    if (!ledes.length) return;
    var t = Date.parse(document.lastModified);
    if (isNaN(t)) return;
    var d = new Date(t);
    var months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    function pad(n) { return (n < 10 ? "0" : "") + n; }
    var label = "Updated " + d.getDate() + " " + months[d.getMonth()] + " " + d.getFullYear() + ", " + pad(d.getHours()) + ":" + pad(d.getMinutes());
    ledes.forEach(function (lede) {
      var p = document.createElement("p");
      p.className = "article-updated";
      p.textContent = label;
      lede.insertAdjacentElement("afterend", p);
    });
  }

  /* Code highlight: wrap bare text the markup left plain.
     Hand-written spans (.comment/.cmd) are kept untouched; only raw
     text nodes are tokenized, so entities and existing spans survive. */
  function initHighlight() {
    var tokenRe = /("[^"\n]*")|((?:^|[\s=:\[(])'[^'\n]*')|(--[A-Za-z][\w-]*)|(^[ \t]*[A-Za-z_][\w.-]*(?=:([ \t]|$)))/gm;
    document.querySelectorAll(".code-block pre").forEach(function (pre) {
      Array.prototype.slice.call(pre.childNodes).forEach(function (node) {
        if (node.nodeType !== 3) return;
        var text = node.nodeValue;
        tokenRe.lastIndex = 0;
        var m, last = 0, found = false;
        var frag = document.createDocumentFragment();
        while ((m = tokenRe.exec(text))) {
          found = true;
          if (m.index > last) frag.appendChild(document.createTextNode(text.slice(last, m.index)));
          var tok = m[0];
          var cls = m[1] ? "str" : m[2] ? "str" : m[3] ? "flag" : "key";
          var s = document.createElement("span");
          s.className = cls;
          if (cls === "key") {
            var lead = tok.match(/^\s*/)[0];
            frag.appendChild(document.createTextNode(lead));
            s.textContent = tok.slice(lead.length);
          } else if (m[2] && tok.charAt(0) !== "'") {
            frag.appendChild(document.createTextNode(tok.charAt(0)));
            s.textContent = tok.slice(1);
          } else {
            s.textContent = tok;
          }
          frag.appendChild(s);
          last = m.index + m[0].length;
        }
        if (!found) return;
        if (last < text.length) frag.appendChild(document.createTextNode(text.slice(last)));
        pre.replaceChild(frag, node);
      });
    });
  }
  /* Headings & Scroll-Spy TOC */
  /* Single-file views: sidebar + TOC + pager + search route by hash */
  var PAGE_OF = {};
  function buildPageMap() {
    PAGE_OF = {};
    document.querySelectorAll(".doc-page[id]").forEach(function (pg) {
      var name = pg.id.replace(/^page-/, "");
      PAGE_OF[name] = name;
      PAGE_OF[pg.id] = name;
      pg.querySelectorAll("[id]").forEach(function (el) {
        PAGE_OF[el.id] = name;
      });
    });
  }
  var PAGE_TITLE = {
    overview: "Overview",
    requirements: "Requirements & constraints",
    setup: "Quickstart",
    cli: "CLI (TicTack)",
    configuration: "YAML (TicTack)",
    remote: "Remote sync",
    jobs: "Jobs & scheduler",
    corpus: "Corpus (Architecture)",
    "corpus-cli": "CLI (Corpus)",
    "corpus-yaml": "YAML (Corpus)",
    diagnostics: "Diagnostics & profiling"
  };

  function showPage(name, activeId) {
    document.querySelectorAll(".doc-page").forEach(function (s) {
      s.classList.toggle("active", s.id === "page-" + name);
    });
    document.querySelectorAll(".toc-link").forEach(function (l) {
      var here = l.getAttribute("data-page") === name;
      l.parentElement.style.display = here ? "" : "none";
      if (activeId) l.classList.toggle("active", l.getAttribute("href") === "#" + activeId);
    });
    var toc = document.querySelector(".toc");
    if (toc) toc.style.visibility = "visible";
    var want = "#page-" + name;
    document.querySelectorAll(".nav-item").forEach(function (a) {
      a.classList.toggle("active", a.getAttribute("href") === want);
    });
    document.title = PAGE_TITLE[name] + " — TicTack Documentation";
    if (window.tictackTocRefresh) window.tictackTocRefresh();
  }

  function route() {
    var id = (location.hash || "").replace(/^#/, "");
    var page = PAGE_OF[id] || "overview";
    var el = id ? document.getElementById(id) : null;
    showPage(page, el ? id : null);
    if (el) {
      var hdr = document.querySelector(".site-header");
      var off = (hdr ? hdr.getBoundingClientRect().height : 52) + 31;
      var y = el.getBoundingClientRect().top + window.scrollY - off;
      window.scrollTo({ top: y < 0 ? 0 : y, behavior: "smooth" });
    }
    else window.scrollTo(0, 0);
  }

  function initPages() {
    buildPageMap();
    window.addEventListener("hashchange", route);
    document.addEventListener("click", function (e) {
      var a = e.target && e.target.closest ? e.target.closest('a[href^="#"]') : null;
      if (!a) return;
      var hash = a.getAttribute("href");
      if (hash.length < 2) return;
      e.preventDefault();
      if (window.tictackTocPin) window.tictackTocPin(hash.replace(/^#/, ""));
      if (location.hash === hash) route();
      else location.hash = hash;
    });
    route();
  }

  function initToc() {
    var headings = Array.from(document.querySelectorAll(".article h2[id], .article h3[id]"));
    headings.forEach(function (h) {
      if (!h.querySelector(".anchor-link")) {
        var a = document.createElement("a");
        a.className = "anchor-link";
        a.href = "#" + h.id;
        a.textContent = "#";
        a.setAttribute("aria-label", "Link to section");
        h.appendChild(a);
      }
    });

    var tocLinks = Array.from(document.querySelectorAll(".toc-link"));
    if (!tocLinks.length || !headings.length) return;

    function activeHeadings() {
      var pg = document.querySelector(".doc-page.active");
      if (!pg) return [];
      return Array.from(pg.querySelectorAll("h2[id], h3[id]"));
    }

    var lockedId = null;

    function onScroll() {
      var hs = activeHeadings();
      if (!hs.length) return;
      if (lockedId && document.getElementById(lockedId)) {
        tocLinks.forEach(function (l) {
          l.classList.toggle("active", l.getAttribute("href") === "#" + lockedId);
        });
        return;
      }
      lockedId = null;
      var y = window.scrollY + 130;
      var activeId = hs[0].id;
      var pickTop = hs[0].getBoundingClientRect().top + window.scrollY;
      for (var i = 0; i < hs.length; i++) {
        var top = hs[i].getBoundingClientRect().top + window.scrollY;
        if (top <= y) { activeId = hs[i].id; pickTop = top; }
      }
      if (window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 4) {
        var lastTop = hs[hs.length - 1].getBoundingClientRect().top + window.scrollY;
        if (Math.abs(lastTop - window.scrollY - 130) < Math.abs(pickTop - window.scrollY - 130)) {
          activeId = hs[hs.length - 1].id;
        }
      }
      tocLinks.forEach(function (l) {
        l.classList.toggle("active", l.getAttribute("href") === "#" + activeId);
      });
    }
    var spyQueued = false;
    function spySoon() {
      if (spyQueued) return;
      spyQueued = true;
      requestAnimationFrame(function () { spyQueued = false; onScroll(); });
    }
    window.addEventListener("scroll", spySoon, { passive: true });
    window.tictackTocRefresh = onScroll;
    var lockTimer = 0;
    window.tictackTocPin = function (id) {
      lockedId = id;
      if (lockTimer) clearTimeout(lockTimer);
      lockTimer = setTimeout(function () { lockedId = null; lockTimer = 0; onScroll(); }, 1800);
    };
    ["wheel", "touchmove"].forEach(function (ev) {
      window.addEventListener(ev, function () { lockedId = null; }, { passive: true });
    });
    window.addEventListener("keydown", function (e) {
      if (["ArrowUp", "ArrowDown", "PageUp", "PageDown", "Home", "End", " "].indexOf(e.key) !== -1) lockedId = null;
    });
    onScroll();
  }

  /* Search dialog (Ctrl+K): full-text over every word on every view */
  var SEARCH_INDEX = null;

  function escHtml(s) {
    return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  }

  function escReg(s) {
    return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  }

  /* Briefly mark matched words in the target section, like a selection */
  function flashTerms(id, terms) {
    if (!terms.length) return;
    var h = document.getElementById(id);
    if (!h) return;
    var re;
    try {
      re = new RegExp("(" + terms.map(escReg).join("|") + ")", "gi");
    } catch (e) { return; }
    var spans = [];
    var roots = [h];
    var n = h.nextSibling;
    while (n && !(n.nodeType === 1 && n.tagName === "H2")) {
      roots.push(n);
      n = n.nextSibling;
    }
    roots.forEach(function (root) {
      var walker = document.createTreeWalker(
        root.nodeType === 3 ? root.parentNode : root,
        NodeFilter.SHOW_TEXT,
        root.nodeType === 3
          ? { acceptNode: function (t) { return t === root ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT; } }
          : null
      );
      var texts = [];
      while (walker.nextNode()) texts.push(walker.currentNode);
      texts.forEach(function (t) {
        if (!t.nodeValue || !re.test(t.nodeValue)) return;
        re.lastIndex = 0;
        var frag = document.createDocumentFragment();
        var last = 0;
        var m;
        while ((m = re.exec(t.nodeValue))) {
          if (m.index > last) frag.appendChild(document.createTextNode(t.nodeValue.slice(last, m.index)));
          var s = document.createElement("span");
          s.className = "search-flash";
          s.textContent = m[0];
          frag.appendChild(s);
          spans.push(s);
          last = m.index + m[0].length;
        }
        if (last < t.nodeValue.length) frag.appendChild(document.createTextNode(t.nodeValue.slice(last)));
        t.parentNode.replaceChild(frag, t);
      });
    });
    if (!spans.length) return;
    setTimeout(function () {
      spans.forEach(function (s) {
        if (s.parentNode) s.parentNode.replaceChild(document.createTextNode(s.textContent), s);
      });
    }, 2200);
  }

  function buildSearchIndex() {
    if (SEARCH_INDEX) return SEARCH_INDEX;
    SEARCH_INDEX = [];
    document.querySelectorAll(".doc-page").forEach(function (pg) {
      var view = pg.id.replace(/^page-/, "");
      Array.from(pg.querySelectorAll("h2[id]")).forEach(function (h) {
        var text = "";
        var node = h.nextSibling;
        while (node && !(node.nodeType === 1 && node.tagName === "H2")) {
          text += " " + (node.textContent || "");
          node = node.nextSibling;
        }
        SEARCH_INDEX.push({
          id: h.id,
          title: h.textContent.replace(/#\s*$/, "").trim(),
          text: text.replace(/\s+/g, " ").trim(),
          view: view
        });
      });
    });
    return SEARCH_INDEX;
  }

  function searchSections(q) {
    var terms = q.toLowerCase().split(/\s+/).filter(Boolean);
    if (!terms.length) return [];
    return buildSearchIndex()
      .map(function (e) {
        var hay = (e.title + " " + e.text).toLowerCase();
        var lowTitle = e.title.toLowerCase();
        var score = 0;
        for (var i = 0; i < terms.length; i++) {
          if (hay.indexOf(terms[i]) === -1) return null;
          if (lowTitle.indexOf(terms[i]) !== -1) score += 10;
          score += 1;
        }
        var low = e.text.toLowerCase();
        var pos = -1;
        terms.forEach(function (t) {
          var i = low.indexOf(t);
          if (i !== -1 && (pos === -1 || i < pos)) pos = i;
        });
        var snippet;
        if (pos === -1) {
          snippet = e.text.slice(0, 150) + (e.text.length > 150 ? "\u2026" : "");
        } else {
          var s0 = Math.max(0, pos - 60);
          var s1 = Math.min(e.text.length, pos + 90);
          snippet = (s0 > 0 ? "\u2026" : "") + e.text.slice(s0, s1) + (s1 < e.text.length ? "\u2026" : "");
        }
        return { url: "#" + e.id, title: e.title, page: PAGE_TITLE[e.view], desc: snippet, score: score, pos: pos };
      })
      .filter(Boolean)
      .sort(function (a, b) { return b.score - a.score || a.pos - b.pos; })
      .slice(0, 12);
  }

  /* Search dialog (Ctrl+K) */
  function initSearch() {
    var overlay = document.getElementById("search-overlay");
    var input = document.getElementById("search-input");
    var list = document.getElementById("search-list");
    var triggers = document.querySelectorAll("[data-search-open]");
    if (!overlay || !input || !list) return;

    var idx = 0;
    var filtered = [];

    function render(q) {
      filtered = searchSections(q);
      idx = 0;
      if (!filtered.length) {
        list.innerHTML = '<div style="padding:14px;color:var(--text-muted);font-size:0.83rem;">No matching sections.</div>';
        return;
      }
      list.innerHTML = filtered
        .map(function (it, i) {
          return (
            '<a class="search-hit' +
            (i === idx ? " selected" : "") +
            '" href="' +
            it.url +
            '">' +
            '<div class="search-hit-top"><span>' +
            escHtml(it.title) +
            '</span><span class="search-hit-page">' +
            escHtml(it.page) +
            "</span></div>" +
            '<div class="search-hit-desc">' +
            escHtml(it.desc) +
            "</div></a>"
          );
        })
        .join("");
    }

    function open() {
      overlay.classList.add("open");
      input.value = "";
      render("");
      setTimeout(function () { input.focus(); }, 15);
    }

    function close() {
      overlay.classList.remove("open");
    }

    var escBtn = document.getElementById("search-esc");
    if (escBtn) escBtn.addEventListener("click", close);

    triggers.forEach(function (t) { t.addEventListener("click", open); });
    overlay.addEventListener("click", function (e) { if (e.target === overlay) close(); });
    input.addEventListener("input", function () { render(input.value); });
    list.addEventListener("click", function (e) {
      var a = e.target.closest ? e.target.closest(".search-hit") : null;
      if (!a) return;
      e.preventDefault();
      var terms = input.value.toLowerCase().split(/\s+/).filter(Boolean);
      var hash = a.getAttribute("href");
      var id = hash.replace(/^#/, "");
      close();
      if (location.hash === hash) route();
      else location.hash = hash;
      setTimeout(function () { flashTerms(id, terms); }, 650);
    });

    document.addEventListener("keydown", function (e) {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        if (overlay.classList.contains("open")) close();
        else open();
      } else if (e.key === "Escape" && overlay.classList.contains("open")) {
        close();
      } else if (overlay.classList.contains("open") && filtered.length) {
        var nodes = list.querySelectorAll(".search-hit");
        if (e.key === "ArrowDown") {
          e.preventDefault();
          idx = (idx + 1) % filtered.length;
          nodes.forEach(function (n, i) { n.classList.toggle("selected", i === idx); });
          if (nodes[idx]) nodes[idx].scrollIntoView({ block: "nearest" });
        } else if (e.key === "ArrowUp") {
          e.preventDefault();
          idx = (idx - 1 + filtered.length) % filtered.length;
          nodes.forEach(function (n, i) { n.classList.toggle("selected", i === idx); });
          if (nodes[idx]) nodes[idx].scrollIntoView({ block: "nearest" });
        } else if (e.key === "Enter" && filtered[idx]) {
          e.preventDefault();
          var terms = input.value.toLowerCase().split(/\s+/).filter(Boolean);
          var hash = filtered[idx].url;
          var id = hash.replace(/^#/, "");
          close();
          if (location.hash === hash) route();
          else window.location.href = hash;
          setTimeout(function () { flashTerms(id, terms); }, 650);
        }
      }
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    initTheme();
    initCodeBlocks();
    initHighlight();
    initUpdated();
    initToc();
    initSearch();
    initPages();
  });
})();
