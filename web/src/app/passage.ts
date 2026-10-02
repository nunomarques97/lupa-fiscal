import { Highlight } from './api';

/** A run of passage text; marked runs are rendered as <mark>. Always inserted as text, never HTML. */
export interface Segment {
  text: string;
  mark: boolean;
}

export interface PassageView {
  segments: Segment[];
  /** Text was cut before the first segment. */
  before: boolean;
  /** Text was cut after the last segment. */
  after: boolean;
}

// PDF line breaks become spaces one for one, so highlight offsets stay valid.
function flatten(passage: string): string {
  return passage.replace(/[\r\n\t]/g, ' ');
}

/** Valid highlights within the text, sorted and merged where they overlap or touch. */
export function mergeHighlights(highlights: readonly Highlight[], textLength: number): Highlight[] {
  const valid = highlights
    .filter(h => Number.isInteger(h.start) && Number.isInteger(h.length) && h.length > 0)
    .map(h => {
      const start = Math.max(0, h.start);
      const end = Math.min(textLength, h.start + h.length);
      return { start, length: end - start };
    })
    .filter(h => h.length > 0)
    .sort((a, b) => a.start - b.start);
  const merged: Highlight[] = [];
  for (const h of valid) {
    const last = merged[merged.length - 1];
    if (last && h.start <= last.start + last.length) {
      last.length = Math.max(last.length, h.start + h.length - last.start);
    } else {
      merged.push({ ...h });
    }
  }
  return merged;
}

// Window of about maxChars, snapped to word boundaries. It starts at the beginning of the passage
// (which usually states the question) unless a later window holds at least two more highlights.
function excerptRange(text: string, highlights: Highlight[], maxChars: number): [number, number] {
  if (text.length <= maxChars) return [0, text.length];
  const countIn = (from: number, to: number) =>
    highlights.filter(h => h.start >= from && h.start + h.length <= to).length;
  let best: [number, number] = [0, maxChars];
  let bestCount = countIn(0, maxChars) + 1;
  for (const anchor of highlights) {
    const from = Math.max(0, anchor.start - 60);
    const to = Math.min(text.length, from + maxChars);
    const count = countIn(from, to);
    if (count > bestCount) {
      best = [from, to];
      bestCount = count;
    }
  }
  let [from, to] = best;
  if (from > 0) {
    const space = text.indexOf(' ', from);
    if (space >= 0 && space < to) from = space + 1;
  }
  if (to < text.length) {
    const space = text.lastIndexOf(' ', to);
    if (space > from) to = space;
  }
  return [from, to];
}

/** Segments of the whole passage (maxChars null) or of an excerpt of about maxChars characters. */
export function passageView(passage: string, highlights: readonly Highlight[], maxChars: number | null): PassageView {
  const text = flatten(passage);
  const merged = mergeHighlights(highlights, text.length);
  const [from, to] = maxChars ? excerptRange(text, merged, maxChars) : [0, text.length];
  const segments: Segment[] = [];
  let pos = from;
  for (const h of merged) {
    const start = Math.max(h.start, from);
    const end = Math.min(h.start + h.length, to);
    if (end <= start) continue;
    if (start > pos) segments.push({ text: text.slice(pos, start), mark: false });
    segments.push({ text: text.slice(start, end), mark: true });
    pos = end;
  }
  if (pos < to) segments.push({ text: text.slice(pos, to), mark: false });
  for (const segment of segments) segment.text = segment.text.replace(/ {2,}/g, ' ');
  return { segments, before: from > 0, after: to < text.length };
}
