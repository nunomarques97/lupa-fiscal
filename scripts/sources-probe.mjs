#!/usr/bin/env node
// Source discovery probe: finds every binding-rulings library linked from the official
// "informações vinculativas" landing page, reads the page variables of its list views and fetches
// each library's listing once, to record endpoints, entry counts and listing differences.
// Polite by construction: only https://info.portaldasfinancas.gov.pt, robots.txt checked first and
// obeyed for every URL, one request at a time at least MIN_INTERVAL_MS apart, redirects never followed
// automatically (same-host https redirects are followed by hand and counted), a hard budget of
// MAX_REQUESTS requests, and never a bulk PDF download (at most one 5-byte sample per library).
// Raw responses and the request log go to a scratch directory outside the repository; the probe
// refuses a directory inside it. Findings: docs/research/sources.md
//
// Usage: node scripts/sources-probe.mjs [--scratch DIR] [--interval-ms N] [--max-requests N] [--skip-samples] [--replay RUN_DIR]
//   --scratch       where run directories are created (default: $LUPAFISCAL_PROBE_DIR or <os tmp>/lupa-fiscal-sources-probe)
//   --interval-ms   minimum gap between requests, at least 1000 (default 1500)
//   --max-requests  request budget, 1 to 60 (default 60)
//   --skip-samples  do not fetch the 5-byte PDF sample of each library (when an earlier run confirmed them)
//   --replay        rebuild the summary offline from the responses saved by an earlier run (no network)
// Exit codes: 0 discovery completed, 1 discovery failed (the reason is printed), 2 usage error.
import { mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve, isAbsolute } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const USER_AGENT = 'LupaFiscal/0.2 (+https://github.com/nunomarques97/lupa-fiscal; open-source research crawler)';
export const ROBOTS_TOKEN = 'lupafiscal';
export const ORIGIN = 'https://info.portaldasfinancas.gov.pt';
export const ALLOWED_HOST = 'info.portaldasfinancas.gov.pt';
export const RULINGS_ROOT = '/pt/informacao_fiscal/informacoes_vinculativas';
export const LANDING_PATH = `${RULINGS_ROOT}/Pages/default.aspx`;
export const MAX_REQUESTS = 60;
export const MIN_INTERVAL_MS = 1000;
export const DEFAULT_INTERVAL_MS = 1500;
export const UNFILTERED_CAML = '<IsNotNull><FieldRef Name="ID"></FieldRef></IsNotNull>';
// The listing fields of the CIRS numbered view used by v0.1 (docs/research/gate0.md).
export const CIRS_FIELDS = ['DocIcon', 'NumeroVinculativa', 'Disponibilizada_x0020_em', 'Diploma', 'Artigo', 'Assunto'];
const MAX_REDIRECTS = 3;
const TIMEOUT_MS = 30_000;
const MAX_BODY_BYTES = 20 * 1024 * 1024;
const SEARCH_PAGE_SIZE = 1000;
const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

export class ProbeError extends Error {}
export class UsageError extends ProbeError {}

function assert(condition, message) {
  if (!condition) throw new ProbeError(message);
}

// ---------------------------------------------------------------------------------------------
// Pure helpers (covered by scripts/sources-probe.test.mjs, no network)
// ---------------------------------------------------------------------------------------------

/** Returns the parsed URL when it is https on the allowlisted host with the default port and no credentials. */
export function allowedUrl(url) {
  let parsed;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  if (parsed.protocol !== 'https:' || parsed.hostname !== ALLOWED_HOST) return null;
  if (parsed.port !== '' || parsed.username !== '' || parsed.password !== '') return null;
  return parsed;
}

export function assertAllowedUrl(url) {
  const parsed = allowedUrl(url);
  assert(parsed, `Refusing a URL that is not https on ${ALLOWED_HOST}: ${url}`);
  return parsed;
}

/** Resolves a link or Location header against a base URL; null when the result leaves the allowlist. */
export function resolveAllowed(href, base) {
  if (typeof href !== 'string' || href.trim() === '') return null;
  try {
    return allowedUrl(new URL(href.trim(), base).href);
  } catch {
    return null;
  }
}

const NAMED_ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

export function decodeEntities(text) {
  return String(text ?? '').replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (match, name) => {
    if (name[0] === '#') {
      const code = name[1] === 'x' || name[1] === 'X' ? parseInt(name.slice(2), 16) : parseInt(name.slice(1), 10);
      return Number.isFinite(code) && code > 0 && code <= 0x10ffff ? String.fromCodePoint(code) : match;
    }
    return NAMED_ENTITIES[name.toLowerCase()] ?? match;
  });
}

export function stripTags(html) {
  return decodeEntities(String(html ?? '').replace(/<[^>]*>/g, ' ')).replace(/\s+/g, ' ').trim();
}

/** Library web path of a list page: the path before "/Pages/" or "/Paginas/", or null for other links. */
export function libraryWebPath(pathname) {
  const match = /^(.*?)\/(?:Pages|Paginas)\/[^/]+\.aspx$/i.exec(pathname);
  if (!match) return null;
  const web = match[1];
  if (!web.toLowerCase().startsWith(`${RULINGS_ROOT.toLowerCase()}/`)) return null;
  if (/\/_(?:layouts|vti_bin)\b/i.test(web)) return null;
  return web;
}

/**
 * Parses the landing page (or any rulings page) into libraries. A library heading (<h4>) followed by
 * a list groups its views; a list-page link outside such a group is a library of its own, named by
 * the link text. The current accordion header gives the category.
 */
export function parseLanding(html, baseUrl) {
  const groups = [];
  const groupRe = /<h4\b[^>]*>([\s\S]*?)<\/h4>\s*<ul\b[^>]*>([\s\S]*?)<\/ul>/gi;
  for (let m; (m = groupRe.exec(html)); ) groups.push({ start: m.index, end: m.index + m[0].length, name: stripTags(m[1]) });
  const categories = [];
  const categoryRe = /<a\b[^>]*class=["'][^"']*accordion-toggle[^"']*["'][^>]*>([\s\S]*?)<\/a>/gi;
  for (let m; (m = categoryRe.exec(html)); ) categories.push({ at: m.index, name: stripTags(m[1]) });

  const libraries = new Map();
  const anchorRe = /<a\b[^>]*?\bhref\s*=\s*(["'])(.*?)\1[^>]*>([\s\S]*?)<\/a>/gi;
  for (let m; (m = anchorRe.exec(html)); ) {
    const url = resolveAllowed(decodeEntities(m[2]), baseUrl);
    if (!url) continue;
    const web = libraryWebPath(url.pathname);
    if (!web) continue;
    const label = stripTags(m[3]);
    const group = groups.find((g) => m.index >= g.start && m.index < g.end);
    const category = categories.filter((c) => c.at < m.index).at(-1)?.name ?? '';
    const key = web.toLowerCase();
    if (!libraries.has(key)) libraries.set(key, { name: group?.name || label, category, webPath: web, views: [] });
    const library = libraries.get(key);
    if (!library.views.some((v) => v.path.toLowerCase() === url.pathname.toLowerCase())) {
      library.views.push({ label, path: url.pathname });
    }
  }
  return [...libraries.values()];
}

function unquoteJs(value) {
  const quote = value[0];
  const body = value.slice(1, -1);
  if (quote === '"') {
    try {
      return JSON.parse(value);
    } catch {
      return body;
    }
  }
  return body.replace(/\\(['"\\])/g, '$1');
}

/** Extracts the listdocs.js page variables (__fieldnames, __itemid, ...) and the SharePoint web path. */
export function extractPageVariables(html) {
  const raw = {};
  const varRe = /\bvar\s+__([A-Za-z]+)\s*=\s*('(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*"|-?\d+|null|true|false)\s*;/g;
  for (let m; (m = varRe.exec(html)); ) {
    const value = m[2];
    raw[m[1]] = /^['"]/.test(value) ? unquoteJs(value) : value === 'null' ? null : value === 'true' ? true : value === 'false' ? false : Number(value);
  }
  const web = /\bwebServerRelativeUrl\s*:\s*("(?:[^"\\]|\\.)*")/.exec(html);
  const fieldNames = typeof raw.fieldnames === 'string' && raw.fieldnames.trim() !== '' ? raw.fieldnames.split(',').map((f) => f.trim()) : null;
  return {
    fieldNames,
    listSort: typeof raw.listsort === 'string' ? raw.listsort : null,
    filterValue: typeof raw.filtervalue === 'string' ? decodeEntities(raw.filtervalue) : null,
    enableSearch: Number(raw.enableSearch ?? 0) ? 1 : 0,
    libraries: typeof raw.libraries === 'string' ? raw.libraries : '',
    groupBy: typeof raw.groupBy === 'string' ? raw.groupBy : '',
    itemId: raw.itemid === undefined || raw.itemid === null || String(raw.itemid).trim() === '' ? null : String(raw.itemid).trim(),
    webServerRelativeUrl: web ? unquoteJs(web[1]) : null,
  };
}

export function hasListing(vars) {
  return Boolean(vars && vars.fieldNames && vars.itemId && vars.webServerRelativeUrl);
}

const SAFE_PARAM = /^[A-Za-z0-9_,:.-]*$/;

function param(value) {
  const text = String(value ?? '');
  return SAFE_PARAM.test(text) ? text : encodeURIComponent(text);
}

/** The listdocs URL exactly as listdocs.js builds it (one response with every entry). */
export function buildListingUrl(vars) {
  assert(hasListing(vars), 'Page has no listing variables');
  assert(/^\d+$/.test(vars.itemId), `Unexpected page item id ${JSON.stringify(vars.itemId)}`);
  let url =
    `${ORIGIN}${vars.webServerRelativeUrl}/_vti_bin/portalat/docs.svc/listdocs` +
    `?fields=${param(vars.fieldNames.join(','))}` +
    `&sort=${param(vars.listSort ?? '')}` +
    `&filter=${encodeURIComponent(vars.filterValue ?? '')}` +
    `&id=${vars.itemId}`;
  if (!vars.enableSearch && vars.libraries) url += `&library=${encodeURIComponent(vars.libraries.split(',')[0])}`;
  return assertAllowedUrl(url).href;
}

/** The paged searchdocs URL used by pages with __enableSearch = 1 (unfiltered search only). */
export function buildSearchUrl(vars, pageSize, pageNumber) {
  assert(vars && vars.fieldNames && vars.webServerRelativeUrl, 'Page has no search variables');
  const url =
    `${ORIGIN}${vars.webServerRelativeUrl}/_vti_bin/portalat/docs.svc/searchdocs` +
    `?fields=${param(vars.fieldNames.join(','))}` +
    '&sort=1:desc' +
    `&searchBy=${encodeURIComponent('*')}` +
    `&libraries=${param(vars.libraries)}` +
    `&pageSize=${pageSize}&pageNumber=${pageNumber}`;
  return assertAllowedUrl(url).href;
}

export function isUnfilteredCaml(caml) {
  return String(caml ?? '').replace(/\s+/g, '') === UNFILTERED_CAML.replace(/\s+/g, '');
}

/** Parses a listdocs/searchdocs response: one row per entry, columns in field order. */
export function parseListing(json, fieldNames, baseUrl = ORIGIN) {
  assert(json && typeof json === 'object' && Array.isArray(json.data), 'Listing response has no "data" array');
  return json.data.map((row) => {
    assert(Array.isArray(row) && row.length === fieldNames.length, `Unexpected listing row shape: ${JSON.stringify(row).slice(0, 200)}`);
    const values = {};
    let href = null;
    fieldNames.forEach((field, index) => {
      const cell = String(row[index] ?? '');
      if (href === null) href = /href\s*=\s*['"]([^'"]+)['"]/i.exec(cell)?.[1] ?? null;
      values[field] = stripTags(cell);
    });
    const url = href ? resolveAllowed(decodeEntities(href), baseUrl) : null;
    const isPdf = Boolean(url && /\.pdf$/i.test(url.pathname));
    return {
      href,
      pdfUrl: isPdf ? url.href : null,
      rejected: href && !isPdf ? href : null,
      values,
    };
  });
}

/** Lower-case, percent-decoded file name of a PDF URL; the identity used for cross-library comparison. */
export function pdfFileName(pdfUrl) {
  const last = new URL(pdfUrl).pathname.split('/').at(-1);
  return safeDecode(last).normalize('NFC').toLowerCase();
}

const DATE_FIELD = /Disponibilizada|Data/i;
const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

export function summarizeListing(entries, fieldNames) {
  const withPdf = entries.filter((e) => e.pdfUrl);
  const distinct = new Set(withPdf.map((e) => e.pdfUrl.toLowerCase()));
  const dateField = fieldNames.find((f) => DATE_FIELD.test(f)) ?? null;
  const dates = dateField ? entries.map((e) => e.values[dateField]).filter((d) => ISO_DATE.test(d)).sort() : [];
  const count = (field) => {
    const counts = {};
    if (!fieldNames.includes(field)) return null;
    for (const e of entries) counts[e.values[field]] = (counts[e.values[field]] ?? 0) + 1;
    return counts;
  };
  const folders = {};
  for (const e of withPdf) {
    const path = new URL(e.pdfUrl).pathname;
    const folder = safeDecode(path.slice(0, path.lastIndexOf('/') + 1));
    folders[folder] = (folders[folder] ?? 0) + 1;
  }
  return {
    entries: entries.length,
    withPdf: withPdf.length,
    distinctPdfs: distinct.size,
    rejectedLinks: entries.filter((e) => e.rejected).length,
    noLink: entries.filter((e) => !e.href).length,
    dateField,
    firstDate: dates[0] ?? null,
    lastDate: dates.at(-1) ?? null,
    undatedEntries: entries.length - dates.length,
    emptyNumbers: fieldNames.includes('NumeroVinculativa') ? entries.filter((e) => e.values.NumeroVinculativa === '').length : null,
    diplomas: count('Diploma'),
    distinctArticles: fieldNames.includes('Artigo') ? new Set(entries.map((e) => e.values.Artigo)).size : null,
    pdfFolders: folders,
  };
}

/** File names that appear in the listings of more than one library: [{name, libraries:[code...]}]. */
export function crossLibraryDuplicates(pdfUrlsByLibrary) {
  const owners = new Map();
  for (const [library, urls] of Object.entries(pdfUrlsByLibrary)) {
    for (const url of urls) {
      const name = pdfFileName(url);
      if (!owners.has(name)) owners.set(name, new Set());
      owners.get(name).add(library);
    }
  }
  return [...owners.entries()]
    .filter(([, libs]) => libs.size > 1)
    .map(([name, libs]) => ({ name, libraries: [...libs].sort() }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

/** Picks the view to list a library: the numbered view, else a single page, else the date view. */
export function chooseView(views) {
  const numbered = views.find((v) => /por\s+n[uú]mero/i.test(v.label));
  if (numbered) return { view: numbered, reason: 'numbered view ("Visualização por Número")' };
  if (views.length === 1) return { view: views[0], reason: 'single list page' };
  const byDate = views.find((v) => /data\s+de\s+disponibiliza/i.test(v.label));
  if (byDate) return { view: byDate, reason: 'no numbered view; date view' };
  return { view: views[0], reason: 'no numbered view; first view' };
}

/** Order in which the other views are compared with the chosen one when the budget allows. */
export function extraViewRank(view) {
  if (/legisla[cç][aã]o\s+complementar/i.test(view.label)) return 1;
  if (/verba|artigos?\s+do\s+civa/i.test(view.label)) return 2;
  if (/diploma/i.test(view.label)) return 3;
  if (/artigo/i.test(view.label)) return 4;
  return 5;
}

const CODE_HINTS = { beneficios_fiscais: 'EBF' };

/** Proposed tax code for a library: a short upper-case heading, else a hint, else the last path segment. */
export function proposeCode(name, webPath) {
  const segment = webPath.split('/').at(-1);
  if (CODE_HINTS[segment.toLowerCase()]) return CODE_HINTS[segment.toLowerCase()];
  const trimmed = String(name ?? '').trim();
  if (/^[A-Z]{2,6}$/.test(trimmed)) return trimmed;
  return segment.toUpperCase();
}

/** How a library's chosen listing differs from the CIRS one, as short phrases. */
export function differencesFromCirs(library) {
  const out = [];
  const page = library.chosen?.path.split('/').at(-1) ?? '';
  const folder = library.chosen?.path.split('/').at(-2) ?? '';
  if (folder && folder !== 'Pages') out.push(`pages folder "${folder}"`);
  if (page && page !== 'Vinc_numero.aspx') out.push(`page "${page}"`);
  const vars = library.vars;
  if (!vars) return out;
  const fields = vars.fieldNames ?? [];
  const missing = CIRS_FIELDS.filter((f) => !fields.includes(f));
  const extra = fields.filter((f) => !CIRS_FIELDS.includes(f));
  if (missing.length) out.push(`no ${missing.join(', ')}`);
  if (extra.length) out.push(`extra ${extra.join(', ')}`);
  if (fields.length && missing.length === 0 && extra.length === 0 && fields.join(',') !== CIRS_FIELDS.join(',')) out.push('fields reordered');
  if (vars.filterValue !== null && !isUnfilteredCaml(vars.filterValue)) out.push('filtered view');
  if (vars.enableSearch) out.push('paged searchdocs');
  if (vars.libraries) out.push(`library parameter "${vars.libraries}"`);
  if (vars.listSort && !/^NumeroVinculativa:DESC/.test(vars.listSort)) out.push(`sort ${vars.listSort}`);
  return out;
}

export function parseArgs(argv) {
  const options = { scratch: null, intervalMs: DEFAULT_INTERVAL_MS, maxRequests: MAX_REQUESTS, replay: null, samples: true };
  for (let i = 0; i < argv.length; i++) {
    const name = argv[i];
    const value = argv[i + 1];
    const need = () => {
      if (value === undefined || value.startsWith('--')) throw new UsageError(`${name} needs a value`);
      i++;
      return value;
    };
    if (name === '--scratch') options.scratch = need();
    else if (name === '--replay') options.replay = need();
    else if (name === '--skip-samples') options.samples = false;
    else if (name === '--interval-ms') options.intervalMs = Number(need());
    else if (name === '--max-requests') options.maxRequests = Number(need());
    else throw new UsageError(`Unknown option ${name}`);
  }
  if (!Number.isInteger(options.intervalMs) || options.intervalMs < MIN_INTERVAL_MS) throw new UsageError(`--interval-ms must be an integer >= ${MIN_INTERVAL_MS}`);
  if (!Number.isInteger(options.maxRequests) || options.maxRequests < 1 || options.maxRequests > MAX_REQUESTS) throw new UsageError(`--max-requests must be an integer from 1 to ${MAX_REQUESTS}`);
  return options;
}

/** True when dir is the repository root or inside it (raw responses must never land in the repo). */
export function isInside(dir, root) {
  const rel = relative(resolve(root), resolve(dir));
  return rel === '' || (!rel.startsWith('..') && !isAbsolute(rel));
}

// Minimal RFC 9309 evaluation: the group for our product token (or "*"), longest match wins,
// Allow wins ties, "*" and "$" wildcards supported. Same rules as scripts/gate0-probe.mjs.
export function parseRobots(text) {
  const groups = [];
  let current = null;
  let lastWasAgent = false;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.replace(/#.*$/, '').trim();
    const match = /^([A-Za-z-]+)\s*:\s*(.*)$/.exec(line);
    if (!match) continue;
    const key = match[1].toLowerCase();
    const value = match[2].trim();
    if (key === 'user-agent') {
      if (!lastWasAgent) {
        current = { agents: [], rules: [] };
        groups.push(current);
      }
      current.agents.push(value.toLowerCase());
      lastWasAgent = true;
    } else {
      lastWasAgent = false;
      if (current && (key === 'allow' || key === 'disallow')) current.rules.push({ allow: key === 'allow', path: value });
    }
  }
  return groups;
}

export function robotsRulesFor(groups) {
  const own = groups.filter((g) => g.agents.some((a) => a !== '*' && ROBOTS_TOKEN.startsWith(a)));
  const chosen = own.length > 0 ? own : groups.filter((g) => g.agents.includes('*'));
  return chosen.flatMap((g) => g.rules);
}

function robotsPatternMatches(pattern, path) {
  const anchored = pattern.endsWith('$');
  const body = anchored ? pattern.slice(0, -1) : pattern;
  const regex = body.split('*').map((part) => part.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*');
  return new RegExp(`^${regex}${anchored ? '$' : ''}`).test(path);
}

export function robotsAllows(rules, path) {
  let best = null;
  for (const rule of rules) {
    if (rule.path === '' || !robotsPatternMatches(rule.path, path)) continue;
    if (!best || rule.path.length > best.path.length || (rule.path.length === best.path.length && rule.allow)) best = rule;
  }
  return best ? best.allow : true;
}

// ---------------------------------------------------------------------------------------------
// Network client (live) and replay client (offline)
// ---------------------------------------------------------------------------------------------

function slug(url) {
  const u = new URL(url);
  return (u.pathname.split('/').filter(Boolean).slice(-3).join('_') || 'root').replace(/[^A-Za-z0-9_.-]/g, '_').slice(0, 80);
}

// Reads at most maxBytes; "truncated" tells whether the body had more. A sample stops once it has enough.
async function readLimited(response, maxBytes, sample) {
  const reader = response.body?.getReader();
  if (!reader) return { body: Buffer.alloc(0), truncated: false };
  const chunks = [];
  let length = 0;
  let truncated = false;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    length += value.length;
    if (sample && length >= maxBytes) break;
    if (length > maxBytes) {
      truncated = true;
      break;
    }
  }
  await reader.cancel().catch(() => {});
  return { body: Buffer.concat(chunks).subarray(0, maxBytes), truncated };
}

/** Polite HTTP client. fetchImpl, now and sleep are injectable so tests never touch the network. */
export class LiveClient {
  constructor({ runDir = null, intervalMs = DEFAULT_INTERVAL_MS, maxRequests = MAX_REQUESTS, fetchImpl = globalThis.fetch, now = Date.now, sleep = (ms) => new Promise((r) => setTimeout(r, ms)) }) {
    assert(Number.isInteger(intervalMs) && intervalMs >= MIN_INTERVAL_MS, `The interval must be at least ${MIN_INTERVAL_MS} ms`);
    assert(Number.isInteger(maxRequests) && maxRequests >= 1 && maxRequests <= MAX_REQUESTS, `The budget must be 1 to ${MAX_REQUESTS} requests`);
    this.runDir = runDir;
    this.intervalMs = intervalMs;
    this.maxRequests = maxRequests;
    this.fetchImpl = fetchImpl;
    this.now = now;
    this.sleep = sleep;
    this.log = [];
    this.lastEnd = 0;
    this.rules = [];
  }

  get used() {
    return this.log.length;
  }

  get remaining() {
    return this.maxRequests - this.log.length;
  }

  /** One GET. maxBytes caps what is read; sampleBytes reads only the first bytes (PDF sample). */
  async get(url, { accept, sampleBytes = null, purpose }) {
    const parsed = assertAllowedUrl(url);
    assert(this.remaining > 0, `Request budget of ${this.maxRequests} exhausted`);
    assert(parsed.pathname === '/robots.txt' || robotsAllows(this.rules, parsed.pathname), `robots.txt disallows ${parsed.pathname}`);
    const wait = this.lastEnd + this.intervalMs - this.now();
    if (this.lastEnd > 0 && wait > 0) await this.sleep(wait);
    const n = this.log.length + 1;
    const started = new Date(this.now());
    const entry = { n, time: started.toISOString(), method: 'GET', url: parsed.href, purpose, status: null, contentType: null, bytes: 0, ms: 0, file: null, location: null };
    this.log.push(entry);
    try {
      const response = await this.fetchImpl(parsed.href, {
        headers: { 'User-Agent': USER_AGENT, Accept: accept },
        redirect: 'manual',
        signal: AbortSignal.timeout(TIMEOUT_MS),
      });
      entry.status = response.status;
      entry.contentType = response.headers.get('content-type');
      entry.contentLength = response.headers.get('content-length');
      entry.location = response.headers.get('location');
      const { body, truncated } = await readLimited(response, sampleBytes ?? MAX_BODY_BYTES, sampleBytes !== null);
      assert(!truncated, `Response larger than ${MAX_BODY_BYTES} bytes: ${parsed.href}`);
      entry.bytes = body.length;
      entry.file = `${String(n).padStart(3, '0')}-${slug(parsed.href)}${sampleBytes ? '.sample' : ''}.raw`;
      if (this.runDir) writeFileSync(join(this.runDir, 'responses', entry.file), body);
      return { status: entry.status, contentType: entry.contentType, contentLength: entry.contentLength, location: entry.location, body };
    } catch (error) {
      entry.error = error instanceof ProbeError ? error.message : String(error?.cause?.code ?? error?.message ?? error);
      throw error instanceof ProbeError ? error : new ProbeError(`Network error for ${parsed.href}: ${entry.error}`);
    } finally {
      entry.ms = this.now() - started.getTime();
      this.lastEnd = this.now();
      console.log(`[request ${n}/${this.maxRequests}] ${entry.time} GET ${parsed.href} -> ${entry.status ?? entry.error}`);
    }
  }
}

class ReplayClient {
  constructor(sourceDir) {
    this.sourceDir = sourceDir;
    const saved = JSON.parse(readFileSync(join(sourceDir, 'requests.json'), 'utf8'));
    this.recorded = saved.requests;
    this.maxRequests = saved.maxRequests ?? MAX_REQUESTS;
    this.log = [];
    this.rules = [];
  }

  get used() {
    return this.log.length;
  }

  get remaining() {
    return this.maxRequests - this.log.length;
  }

  async get(url, { purpose }) {
    const parsed = assertAllowedUrl(url);
    const recorded = this.recorded[this.log.length];
    assert(recorded && recorded.url === parsed.href, `Replay diverged at request ${this.log.length + 1}: expected ${recorded?.url}, got ${parsed.href}`);
    this.log.push({ ...recorded, purpose });
    assert(recorded.file, `Request ${recorded.n} has no saved response (${recorded.error ?? 'error'})`);
    assert(/^[\w.-]+$/.test(recorded.file), `Request ${recorded.n} names an unexpected response file`);
    const body = readFileSync(join(this.sourceDir, 'responses', recorded.file));
    return { status: recorded.status, contentType: recorded.contentType, contentLength: recorded.contentLength, location: recorded.location, body };
  }
}

// ---------------------------------------------------------------------------------------------
// Discovery
// ---------------------------------------------------------------------------------------------

/** GET that follows same-host https redirects by hand (each hop counted); other redirects fail. */
async function getFollowing(client, url, options) {
  let current = url;
  for (let hop = 0; ; hop++) {
    const response = await client.get(current, options);
    if (response.status < 300 || response.status >= 400) return { ...response, finalUrl: current };
    const next = resolveAllowed(response.location ?? '', current);
    assert(next, `Redirect from ${current} to ${response.location} leaves the allowlist; not followed`);
    assert(hop < MAX_REDIRECTS, `More than ${MAX_REDIRECTS} redirects from ${url}`);
    current = next.href;
  }
}

async function checkRobots(client) {
  const response = await client.get(`${ORIGIN}/robots.txt`, { accept: 'text/plain', purpose: 'robots.txt' });
  if (response.status >= 400 && response.status < 500) {
    return { status: response.status, rules: [], note: `HTTP ${response.status}: no restrictions (RFC 9309 section 2.3.1.3)` };
  }
  assert(response.status === 200, `robots.txt returned HTTP ${response.status}; RFC 9309 treats it as disallow all, discovery stops`);
  const groups = parseRobots(response.body.toString('utf8'));
  const rules = robotsRulesFor(groups);
  return { status: 200, rules, note: `HTTP 200, ${groups.length} group(s), ${rules.length} rule(s) for "${ROBOTS_TOKEN}"` };
}

async function fetchPage(client, path, purpose) {
  const response = await getFollowing(client, `${ORIGIN}${path}`, { accept: 'text/html', purpose });
  assert(response.status === 200, `HTTP ${response.status} for ${path}`);
  return { html: response.body.toString('utf8'), finalUrl: response.finalUrl };
}

// Time spent in the listing requests themselves (the polite wait before each one excluded).
function requestMs(client, from) {
  return client.log.slice(from).reduce((sum, entry) => sum + (entry.ms ?? 0), 0);
}

async function fetchListing(client, vars, purpose) {
  const from = client.log.length;
  if (!vars.enableSearch) {
    const url = buildListingUrl(vars);
    const response = await client.get(url, { accept: 'application/json', purpose });
    assert(response.status === 200, `Listing returned HTTP ${response.status}`);
    assert((response.contentType ?? '').includes('application/json'), `Listing returned content type "${response.contentType}"`);
    const json = parseJson(response.body);
    return { endpoint: 'listdocs', url, pages: 1, reportedTotal: json.total ?? null, entries: parseListing(json, vars.fieldNames), bytes: response.body.length, ms: requestMs(client, from) };
  }
  assert(isUnfilteredCaml(vars.filterValue) || !vars.filterValue, 'Paged searchdocs view with a filter is not probed (search syntax unknown)');
  const entries = [];
  let reportedTotal = null;
  let bytes = 0;
  let page = 1;
  for (; ; page++) {
    assert(client.remaining > 0, 'Request budget exhausted while paging searchdocs');
    const url = buildSearchUrl(vars, SEARCH_PAGE_SIZE, page);
    const response = await client.get(url, { accept: 'application/json', purpose });
    assert(response.status === 200, `searchdocs returned HTTP ${response.status}`);
    const json = parseJson(response.body);
    bytes += response.body.length;
    reportedTotal = json.total ?? reportedTotal;
    const rows = parseListing(json, vars.fieldNames);
    entries.push(...rows);
    if (rows.length === 0 || rows.length < SEARCH_PAGE_SIZE || (reportedTotal && entries.length >= reportedTotal)) break;
  }
  return { endpoint: 'searchdocs', url: buildSearchUrl(vars, SEARCH_PAGE_SIZE, 1), pages: page, reportedTotal, entries, bytes, ms: requestMs(client, from) };
}

function parseJson(body) {
  try {
    return JSON.parse(body.toString('utf8').replace(/^﻿/, ''));
  } catch (error) {
    throw new ProbeError(`Listing response is not JSON: ${error.message}`);
  }
}

function safeDecode(text) {
  try {
    return decodeURIComponent(text);
  } catch {
    return text;
  }
}

async function samplePdf(client, pdfUrl, purpose) {
  const response = await client.get(pdfUrl, { accept: 'application/pdf', sampleBytes: 5, purpose });
  return {
    url: pdfUrl,
    status: response.status,
    contentType: response.contentType,
    contentLength: response.contentLength ? Number(response.contentLength) : null,
    magic: response.body.subarray(0, 5).toString('latin1'),
  };
}

export async function discover(client, { samples = true } = {}) {
  const result = { startedAt: new Date().toISOString(), userAgent: USER_AGENT, robots: null, landing: LANDING_PATH, libraries: [], extraLinks: [] };
  result.robots = await checkRobots(client);
  client.rules = result.robots.rules;

  const landing = await fetchPage(client, LANDING_PATH, 'landing page');
  const libraries = parseLanding(landing.html, landing.finalUrl);
  assert(libraries.length > 0, 'The landing page lists no rulings library');
  const known = new Set(libraries.map((l) => l.webPath.toLowerCase()));

  // Phase 1: the chosen list page of every library. Its links may reveal further libraries or views.
  const queue = [...libraries];
  for (let i = 0; i < queue.length; i++) {
    const library = queue[i];
    library.code = proposeCode(library.name, library.webPath);
    const { view, reason } = chooseView(library.views);
    library.chosen = { ...view, reason };
    library.extraViews = library.views.filter((v) => v !== view).map((v) => ({ ...v, rank: extraViewRank(v) }));
    result.libraries.push(library);
    try {
      assert(client.remaining >= 2, 'request budget exhausted before this library');
      const page = await fetchPage(client, view.path, `${library.code} list page`);
      library.vars = extractPageVariables(page.html);
      for (const other of parseLanding(page.html, page.finalUrl)) {
        if (known.has(other.webPath.toLowerCase())) {
          addLinkedViews(queue, other, view.path);
          continue;
        }
        known.add(other.webPath.toLowerCase());
        result.extraLinks.push({ webPath: other.webPath, name: other.name, foundOn: view.path });
        queue.push(other);
      }
      assert(hasListing(library.vars), `no listdocs variables on ${view.path}`);
      library.supported = true;
    } catch (error) {
      if (!(error instanceof ProbeError)) throw error;
      library.supported = false;
      library.unsupportedReason = error.message;
    }
  }

  const listed = result.libraries.filter((l) => l.supported);
  const pendingListings = () => listed.filter((l) => !l.listing).length + listed.filter((l) => !l.listing && !isUnfilteredCaml(l.vars.filterValue)).length;
  const pendingSamples = () => (samples ? listed.filter((l) => l.supported && !l.sample).length : 0);

  // Phase 2: page variables of the other views (fields, filters, ids), most informative first.
  const extras = listed.flatMap((l) => l.extraViews.map((v) => ({ library: l, view: v }))).sort((a, b) => a.view.rank - b.view.rank);
  for (const { library, view } of extras) {
    if (client.remaining - pendingListings() - VERIFY_LISTINGS - pendingSamples() < 1) break;
    try {
      const page = await fetchPage(client, view.path, `${library.code} other view`);
      view.vars = extractPageVariables(page.html);
      view.relation = relationToChosen(library.vars, view.vars);
    } catch (error) {
      if (!(error instanceof ProbeError)) throw error;
      view.error = error.message;
    }
  }

  // Phase 3: one superset listing per library (chosen page id, unfiltered, every field its views use).
  // A filtered chosen view is also listed as the browser lists it, to compare.
  for (const library of listed) {
    const viewVars = library.extraViews.filter((v) => v.vars && v.relation === 'same list').map((v) => v.vars);
    library.listingVars = { ...library.vars, filterValue: UNFILTERED_CAML, fieldNames: unionFields([library.vars, ...viewVars]) };
    try {
      let listing;
      try {
        listing = await fetchListing(client, library.listingVars, `${library.code} superset listing`);
      } catch (error) {
        if (!(error instanceof ProbeError) || client.remaining - pendingSamples() < 2) throw error;
        library.supersetError = error.message;
        library.listingVars = library.vars;
        listing = await fetchListing(client, library.vars, `${library.code} listing (fallback)`);
      }
      library.listing = { endpoint: listing.endpoint, url: listing.url, pages: listing.pages, reportedTotal: listing.reportedTotal, bytes: listing.bytes, ms: listing.ms };
      library.summary = summarizeListing(listing.entries, library.listingVars.fieldNames);
      library.fieldFill = fieldFill(listing.entries, library.listingVars.fieldNames);
      library.pdfUrls = [...new Set(listing.entries.filter((e) => e.pdfUrl).map((e) => e.pdfUrl))];
      library.rejectedSamples = listing.entries.filter((e) => e.rejected).slice(0, 5).map((e) => e.rejected);
      if (library.summary.withPdf === 0) {
        library.supported = false;
        library.unsupportedReason = 'listing has no PDF link';
      }
      if (library.listingVars !== library.vars && !isUnfilteredCaml(library.vars.filterValue) && client.remaining - pendingSamples() >= 1) {
        library.browserListing = compareListing(await fetchListing(client, library.vars, `${library.code} listing as the page builds it`), library.pdfUrls);
      }
    } catch (error) {
      if (!(error instanceof ProbeError)) throw error;
      library.supported = false;
      library.unsupportedReason = error.message;
    }
  }

  // Phase 4: verify that other views list nothing the superset misses, as the browser builds them.
  const verifiable = extras
    .filter((x) => x.library.supported && x.view.vars && hasListing(x.view.vars))
    .sort((a, b) => verifyRank(a) - verifyRank(b) || a.view.rank - b.view.rank);
  for (const { library, view } of verifiable) {
    if (client.remaining - pendingSamples() < 1) break;
    try {
      view.listing = compareListing(await fetchListing(client, view.vars, `${library.code} other view listing`), library.pdfUrls);
      if (view.listing.notInSuperset > 0) library.supersetViolated = true;
    } catch (error) {
      if (!(error instanceof ProbeError)) throw error;
      view.listingError = error.message;
    }
  }

  // Phase 5: one 5-byte PDF sample per supported library, to confirm the URL pattern and type.
  for (const library of samples ? result.libraries.filter((l) => l.supported) : []) {
    if (client.remaining < 1) break;
    try {
      library.sample = await samplePdf(client, library.pdfUrls[0], `${library.code} sample PDF (5 bytes)`);
    } catch (error) {
      if (!(error instanceof ProbeError)) throw error;
      library.sample = { url: library.pdfUrls[0], error: error.message };
    }
  }

  const byLibrary = Object.fromEntries(result.libraries.filter((l) => l.supported).map((l) => [l.code, l.pdfUrls]));
  result.duplicates = crossLibraryDuplicates(byLibrary);
  result.requests = client.log;
  result.finishedAt = new Date().toISOString();
  return result;
}

const VERIFY_LISTINGS = 3;

// Views on another list first, then views of libraries whose chosen view is filtered.
function verifyRank({ library, view }) {
  if (view.relation !== 'same list') return 0;
  if (!isUnfilteredCaml(library.vars.filterValue)) return 1;
  return 2;
}

function compareListing(listing, supersetUrls) {
  const superset = new Set(supersetUrls.map((u) => u.toLowerCase()));
  const urls = [...new Set(listing.entries.filter((e) => e.pdfUrl).map((e) => e.pdfUrl.toLowerCase()))];
  const missing = urls.filter((u) => !superset.has(u));
  return { entries: listing.entries.length, distinctPdfs: urls.length, notInSuperset: missing.length, notInSupersetSamples: missing.slice(0, 5) };
}

/** Field names a view uses (displayed fields, then sort fields), DocIcon first, each once. */
export function unionFields(varsList) {
  const fields = ['DocIcon'];
  for (const vars of varsList) {
    const sortFields = String(vars?.listSort ?? '')
      .split(',')
      .map((part) => part.split(':')[0].trim())
      .filter(Boolean);
    for (const field of [...(vars?.fieldNames ?? []), ...sortFields]) {
      if (/^[A-Za-z0-9_]+$/.test(field) && !fields.includes(field)) fields.push(field);
    }
  }
  return fields;
}

/** Number of entries with a non-empty value per field. */
export function fieldFill(entries, fieldNames) {
  return Object.fromEntries(fieldNames.map((f) => [f, entries.filter((e) => e.values[f] !== '').length]));
}

// Views of a known library that its own or another library's pages link to but the landing does not.
function addLinkedViews(libraries, linked, foundOn) {
  const library = libraries.find((l) => l.webPath.toLowerCase() === linked.webPath.toLowerCase());
  for (const view of linked.views) {
    if (library.views.some((v) => v.path.toLowerCase() === view.path.toLowerCase())) continue;
    const added = { ...view, foundOn };
    library.views.push(added);
    if (library.extraViews) library.extraViews.push({ ...added, rank: extraViewRank(added) });
  }
}

/** Whether a view reads the same list as the library's chosen view (same web, library and endpoint). */
export function relationToChosen(chosen, other) {
  if (!hasListing(other)) return 'no listing on page';
  if (!chosen) return 'not comparable';
  const same =
    other.webServerRelativeUrl === chosen.webServerRelativeUrl &&
    (other.libraries ?? '') === (chosen.libraries ?? '') &&
    !other.enableSearch &&
    !chosen.enableSearch;
  return same ? 'same list' : 'different list';
}

// ---------------------------------------------------------------------------------------------
// Report (scratch only; docs/research/sources.md is written from it by hand)
// ---------------------------------------------------------------------------------------------

function cell(text) {
  return String(text ?? '').replace(/\|/g, '\\|').replace(/\n/g, ' ');
}

function viewDetail(v) {
  if (!v.vars) return `not checked${v.error ? ` (${cell(v.error)})` : ' (budget)'}`;
  let text = `item id ${v.vars.itemId}, fields \`${cell(v.vars.fieldNames?.join(','))}\`, filter \`${cell(v.vars.filterValue)}\`, ${v.relation}`;
  if (v.listing) text += `; listing ${v.listing.entries} entries, ${v.listing.distinctPdfs} PDFs, ${v.listing.notInSuperset} not in the superset`;
  if (v.listingError) text += `; listing error ${cell(v.listingError)}`;
  return text;
}

export function renderSummary(result) {
  const lines = [];
  lines.push('# Sources probe summary', '', `Started ${result.startedAt}, finished ${result.finishedAt}. User-Agent: \`${result.userAgent}\`.`, '');
  lines.push(`robots.txt: ${result.robots?.note}`, '');
  lines.push(
    '## Libraries',
    '',
    '| Code | Name | Category | Web path | View page | Item id | Page fields | Page filter | Page sort | Entries | Distinct PDFs | Dates | Differences from CIRS | Supported |',
    '|---|---|---|---|---|---|---|---|---|---|---|---|---|---|',
  );
  for (const l of result.libraries) {
    const s = l.summary;
    const page = l.chosen?.path.split('/').slice(-2).join('/');
    const dates = s ? `${s.firstDate} to ${s.lastDate}` : '';
    const supported = l.supported ? 'yes' : `no: ${cell(l.unsupportedReason)}`;
    lines.push(
      `| ${cell(l.code)} | ${cell(l.name)} | ${cell(l.category)} | \`${cell(l.webPath)}\` | \`${cell(page)}\` (${cell(l.chosen?.reason)}) | ${cell(l.vars?.itemId)} | \`${cell(l.vars?.fieldNames?.join(','))}\` | \`${cell(l.vars?.filterValue)}\` | \`${cell(l.vars?.listSort)}\` | ${s?.entries ?? ''} | ${s?.distinctPdfs ?? ''} | ${dates} | ${cell(differencesFromCirs(l).join('; ') || 'none')} | ${supported} |`,
    );
  }
  lines.push('', '## Listing details', '');
  for (const l of result.libraries.filter((x) => x.summary)) {
    const s = l.summary;
    const diplomas = s.diplomas ? Object.entries(s.diplomas).sort((a, b) => b[1] - a[1]) : [];
    lines.push(`### ${l.code}`, '');
    let listing = `- listing: ${l.listing.endpoint}, ${l.listing.pages} request(s), ${l.listing.bytes} bytes, ${l.listing.ms} ms, reported total ${l.listing.reportedTotal}`;
    if (l.supersetError) listing += `; superset request failed (${cell(l.supersetError)}), page listing used`;
    lines.push(listing);
    lines.push(`- listing fields: \`${l.listingVars.fieldNames.join(',')}\`, filter \`${cell(l.listingVars.filterValue)}\``);
    if (l.fieldFill) lines.push(`- non-empty values per field: ${Object.entries(l.fieldFill).map(([f, c]) => `${f} ${c}`).join(', ')}`);
    if (l.browserListing) {
      const b = l.browserListing;
      lines.push(`- page listing (filtered as the page builds it): ${b.entries} entries, ${b.distinctPdfs} PDFs, ${b.notInSuperset} not in the superset`);
    }
    const rejected = l.rejectedSamples?.length ? ` (${l.rejectedSamples.map(cell).join(', ')})` : '';
    lines.push(`- entries ${s.entries}, with PDF ${s.withPdf}, distinct PDFs ${s.distinctPdfs}, rejected links ${s.rejectedLinks}${rejected}, no link ${s.noLink}`);
    lines.push(`- dates (${s.dateField}): ${s.firstDate} to ${s.lastDate}, undated ${s.undatedEntries}; empty numbers ${s.emptyNumbers}; distinct articles ${s.distinctArticles}`);
    lines.push(`- PDF folders: ${Object.entries(s.pdfFolders).map(([f, c]) => `\`${f}\` ${c}`).join(', ')}`);
    if (diplomas.length) {
      const top = diplomas.slice(0, 8).map(([d, c]) => `${cell(d) || '(empty)'} ${c}`).join('; ');
      lines.push(`- diplomas (${diplomas.length}): ${top}${diplomas.length > 8 ? '; ...' : ''}`);
    }
    if (l.sample) {
      const p = l.sample;
      lines.push(`- sample PDF: ${p.url} -> ${p.status ?? p.error} ${p.contentType ?? ''}, Content-Length ${p.contentLength ?? 'unknown'}, starts with ${JSON.stringify(p.magic ?? '')}`);
    }
    for (const v of l.extraViews) {
      lines.push(`- other view "${cell(v.label)}" \`${v.path.split('/').at(-1)}\`${v.foundOn ? ' (linked from a library page)' : ''}: ${viewDetail(v)}`);
    }
    lines.push('');
  }
  lines.push('## Cross-library duplicate file names', '', `${result.duplicates.length} file name(s) appear in more than one library listing.`, '');
  const pairs = {};
  for (const d of result.duplicates) pairs[d.libraries.join('+')] = (pairs[d.libraries.join('+')] ?? 0) + 1;
  for (const [k, v] of Object.entries(pairs).sort((a, b) => b[1] - a[1])) lines.push(`- ${k}: ${v}`);
  lines.push('', result.duplicates.slice(0, 30).map((d) => `\`${d.name}\` (${d.libraries.join(', ')})`).join(', '), '');
  if (result.extraLinks.length) {
    lines.push('## Pages linked from library pages', '', ...result.extraLinks.map((x) => `- ${x.name} \`${x.webPath}\` (on ${x.foundOn})`), '');
  }
  lines.push('## Request log', '', '| # | Time (UTC) | URL | Status | Bytes | ms |', '|---|---|---|---|---|---|');
  for (const r of result.requests) lines.push(`| ${r.n} | ${r.time} | ${cell(r.url)} | ${r.status ?? cell(r.error)} | ${r.bytes} | ${r.ms} |`);
  lines.push('', `Total requests: ${result.requests.length}.`);
  return lines.join('\n') + '\n';
}

function scratchRoot(option) {
  return resolve(option ?? process.env.LUPAFISCAL_PROBE_DIR ?? join(tmpdir(), 'lupa-fiscal-sources-probe'));
}

function realOrSelf(path) {
  try {
    return realpathSync(path);
  } catch {
    return resolve(path);
  }
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const root = scratchRoot(options.scratch);
  assert(!isInside(root, REPO_ROOT), `Scratch directory ${root} is inside the repository; raw responses must stay outside it`);
  mkdirSync(root, { recursive: true });
  assert(!isInside(realOrSelf(root), realOrSelf(REPO_ROOT)), `Scratch directory ${root} resolves inside the repository; raw responses must stay outside it`);
  const runDir = join(root, new Date().toISOString().replace(/[:.]/g, '-') + (options.replay ? '-replay' : ''));
  mkdirSync(join(runDir, 'responses'), { recursive: true });
  console.log(`User-Agent: ${USER_AGENT}`);
  console.log(`Run directory: ${runDir}`);
  const client = options.replay ? new ReplayClient(resolve(options.replay)) : new LiveClient({ runDir, intervalMs: options.intervalMs, maxRequests: options.maxRequests });
  let result;
  try {
    result = await discover(client, { samples: options.samples });
  } finally {
    writeFileSync(join(runDir, 'requests.json'), JSON.stringify({ maxRequests: client.maxRequests, requests: client.log }, null, 2));
  }
  writeFileSync(join(runDir, 'summary.json'), JSON.stringify(result, null, 2));
  writeFileSync(join(runDir, 'summary.md'), renderSummary(result));
  const supported = result.libraries.filter((l) => l.supported);
  console.log(`Libraries: ${result.libraries.length} found, ${supported.length} with a listing of PDFs.`);
  for (const l of result.libraries) console.log(`  ${l.code}: ${l.supported ? `${l.summary.entries} entries, ${l.summary.distinctPdfs} distinct PDFs` : `not supported (${l.unsupportedReason})`}`);
  console.log(`Cross-library duplicate file names: ${result.duplicates.length}. Requests: ${client.used}.`);
  console.log(`Summary: ${join(runDir, 'summary.md')}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().catch((error) => {
    console.error(`Sources probe FAILED: ${error instanceof ProbeError ? error.message : error?.stack ?? error}`);
    process.exitCode = error instanceof UsageError ? 2 : 1;
  });
}
