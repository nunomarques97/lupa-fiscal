// Shared helpers for the direction mocks: formatting and highlight segments built from offsets.
// Text is inserted with textContent only; passages are never treated as HTML.
(function () {
  const MONTHS = ['jan.', 'fev.', 'mar.', 'abr.', 'mai.', 'jun.', 'jul.', 'ago.', 'set.', 'out.', 'nov.', 'dez.'];
  const SECTIONS = {
    header: 'Identificação', request: 'Pedido', facts: 'Factos',
    'legal-framework': 'Enquadramento', conclusion: 'Conclusão', content: 'Texto',
  };

  // "78-D" -> "78.º-D"
  function article(value) {
    const [num, suffix] = value.split('-');
    return num + '.º' + (suffix ? '-' + suffix : '');
  }

  function dateShort(iso) {
    const [y, m, d] = iso.split('-');
    return d + '/' + m + '/' + y;
  }

  function dateLong(iso) {
    const [y, m, d] = iso.split('-').map(Number);
    return d + ' ' + MONTHS[m - 1] + ' ' + y;
  }

  // PDF line breaks become spaces one for one, so highlight offsets stay valid.
  function flatten(passage) {
    return passage.replace(/[\r\n\t]/g, ' ');
  }

  function sortedHighlights(highlights) {
    const list = [...highlights].sort((a, b) => a.start - b.start);
    const merged = [];
    for (const h of list) {
      const last = merged[merged.length - 1];
      if (last && h.start <= last.start + last.length) {
        last.length = Math.max(last.length, h.start + h.length - last.start);
      } else {
        merged.push({ start: h.start, length: h.length });
      }
    }
    return merged;
  }

  // Window of about maxChars, snapped to word boundaries. It starts at the beginning of the passage
  // (which usually states the question) unless a later window holds at least two more highlights.
  function excerptRange(text, highlights, maxChars) {
    if (text.length <= maxChars) return [0, text.length];
    const countIn = (from, to) => highlights.filter(h => h.start >= from && h.start + h.length <= to).length;
    let best = [0, maxChars], bestCount = countIn(0, maxChars) + 1;
    for (const anchor of highlights) {
      const from = Math.max(0, anchor.start - 60);
      const to = Math.min(text.length, from + maxChars);
      const count = countIn(from, to);
      if (count > bestCount) { best = [from, to]; bestCount = count; }
    }
    let [from, to] = best;
    if (from > 0) { const s = text.indexOf(' ', from); if (s >= 0 && s < to) from = s + 1; }
    if (to < text.length) { const s = text.lastIndexOf(' ', to); if (s > from) to = s; }
    return [from, to];
  }

  // Segments [{text, mark}] for the whole passage or an excerpt of it.
  function segments(result, maxChars) {
    const text = flatten(result.passage);
    const highlights = sortedHighlights(result.highlights);
    const [from, to] = maxChars ? excerptRange(text, highlights, maxChars) : [0, text.length];
    const out = [];
    let pos = from;
    for (const h of highlights) {
      const s = Math.max(h.start, from), e = Math.min(h.start + h.length, to);
      if (e <= s) continue;
      if (s > pos) out.push({ text: text.slice(pos, s), mark: false });
      out.push({ text: text.slice(s, e), mark: true });
      pos = e;
    }
    if (pos < to) out.push({ text: text.slice(pos, to), mark: false });
    for (const seg of out) seg.text = seg.text.replace(/ {2,}/g, ' ');
    return { out, before: from > 0, after: to < text.length };
  }

  // Appends the passage into el: plain text nodes plus <mark> elements.
  function renderPassage(el, result, maxChars) {
    const { out, before, after } = segments(result, maxChars);
    if (before) el.append('… ');
    for (const seg of out) {
      if (seg.mark) {
        const m = document.createElement('mark');
        m.textContent = seg.text;
        el.append(m);
      } else {
        el.append(seg.text);
      }
    }
    if (after) el.append(' …');
  }

  function h(tag, attrs, ...children) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v == null) continue;
      if (k === 'class') el.className = v; else el.setAttribute(k, v);
    }
    for (const c of children) if (c != null) el.append(c);
    return el;
  }

  window.Mock = { article, dateShort, dateLong, section: s => SECTIONS[s] || s, renderPassage, h };
})();
