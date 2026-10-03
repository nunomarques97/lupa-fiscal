#!/usr/bin/env node
// Gate 0 probe: checks that the CIRS binding-rulings source is reachable and may be crawled.
// Sends at most 5 sequential requests, spaced at least MIN_INTERVAL_MS apart, with the project
// User-Agent. Nothing is written to disk. Exits non-zero on any failure.
// Findings and field mapping: docs/research/gate0.md

const USER_AGENT = 'LupaFiscal/0.2 (+https://github.com/nunomarques97/lupa-fiscal; open-source research crawler)';
const ROBOTS_TOKEN = 'lupafiscal';
const ORIGIN = 'https://info.portaldasfinancas.gov.pt';
const ALLOWED_HOSTS = new Set(['info.portaldasfinancas.gov.pt']);
const CIRS_WEB = '/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs';

// Same parameters as the "Vinc_numero.aspx" list page (page item id 42), plus the paragraph field
// shown on the "by article" page, so one response carries every listing field.
const LISTING_FIELDS = [
  'DocIcon',
  'NumeroVinculativa',
  'Disponibilizada_x0020_em',
  'Diploma',
  'Artigo',
  'N_x002e__x00ba__x002f_Al_x00ed_nea',
  'Assunto',
];
const LISTING_URL =
  `${ORIGIN}${CIRS_WEB}/_vti_bin/portalat/docs.svc/listdocs` +
  `?fields=${LISTING_FIELDS.join(',')}` +
  '&sort=NumeroVinculativa:DESC' +
  `&filter=${encodeURIComponent('<IsNotNull><FieldRef Name="ID"></FieldRef></IsNotNull>')}` +
  '&id=42';

const MAX_REQUESTS = 5;
const MIN_INTERVAL_MS = 1500;
const TIMEOUT_MS = 30_000;
const MAX_LISTING_BYTES = 20 * 1024 * 1024;

let requestCount = 0;
let lastRequestEnd = 0;

class ProbeError extends Error {}

function assert(condition, message) {
  if (!condition) throw new ProbeError(message);
}

function assertAllowedUrl(url) {
  const parsed = new URL(url);
  assert(parsed.protocol === 'https:', `Refusing non-https URL: ${url}`);
  assert(ALLOWED_HOSTS.has(parsed.hostname), `Refusing host outside the allowlist: ${parsed.hostname}`);
  return parsed;
}

async function politeFetch(url, accept) {
  assert(requestCount < MAX_REQUESTS, `Request budget of ${MAX_REQUESTS} exhausted`);
  assertAllowedUrl(url);
  const wait = lastRequestEnd + MIN_INTERVAL_MS - Date.now();
  if (lastRequestEnd > 0 && wait > 0) await new Promise((resolve) => setTimeout(resolve, wait));
  requestCount++;
  const started = new Date();
  try {
    const response = await fetch(url, {
      headers: { 'User-Agent': USER_AGENT, Accept: accept },
      redirect: 'manual',
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
    console.log(`[request ${requestCount}/${MAX_REQUESTS}] ${started.toISOString()} GET ${url} -> ${response.status}`);
    return response;
  } finally {
    lastRequestEnd = Date.now();
  }
}

// Minimal RFC 9309 evaluation: pick the group for our product token (or "*"), longest match wins,
// Allow wins ties, "*" and "$" wildcards supported.
function parseRobots(text) {
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

function robotsRulesFor(groups) {
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

function robotsAllows(rules, path) {
  let best = null;
  for (const rule of rules) {
    if (rule.path === '' || !robotsPatternMatches(rule.path, path)) continue;
    if (!best || rule.path.length > best.path.length || (rule.path.length === best.path.length && rule.allow)) best = rule;
  }
  return best ? best.allow : true;
}

async function checkRobots() {
  const response = await politeFetch(`${ORIGIN}/robots.txt`, 'text/plain');
  const status = response.status;
  if (status === 404 || (status >= 400 && status < 500)) {
    await response.body?.cancel();
    console.log(`robots.txt: HTTP ${status}. RFC 9309 section 2.3.1.3: an unavailable robots.txt (4xx) means no crawl restrictions.`);
    return [];
  }
  assert(status === 200, `robots.txt: HTTP ${status}. RFC 9309 treats an unreachable robots.txt (5xx, redirects not followed here) as disallow all; gate fails.`);
  const text = await response.text();
  const groups = parseRobots(text);
  const rules = robotsRulesFor(groups);
  console.log(`robots.txt: HTTP 200, ${groups.length} group(s). Rules applying to "${ROBOTS_TOKEN}":`);
  for (const rule of rules) console.log(`  ${rule.allow ? 'Allow' : 'Disallow'}: ${rule.path}`);
  if (rules.length === 0) console.log('  (none)');
  return rules;
}

function stripTags(html) {
  return String(html ?? '').replace(/<[^>]*>/g, '').trim();
}

function parseListing(json) {
  assert(json && Array.isArray(json.data), 'Listing response has no "data" array');
  const column = Object.fromEntries(LISTING_FIELDS.map((name, index) => [name, index]));
  return json.data.map((row) => {
    assert(Array.isArray(row) && row.length === LISTING_FIELDS.length, `Unexpected listing row shape: ${JSON.stringify(row).slice(0, 200)}`);
    const href = /href=['"]([^'"]+\.pdf)['"]/i.exec(String(row[column.DocIcon]))?.[1] ?? null;
    return {
      pdfPath: href,
      number: stripTags(row[column.NumeroVinculativa]),
      date: stripTags(row[column.Disponibilizada_x0020_em]),
      diploma: stripTags(row[column.Diploma]),
      article: stripTags(row[column.Artigo]),
      paragraph: stripTags(row[column.N_x002e__x00ba__x002f_Al_x00ed_nea]),
      subject: stripTags(row[column.Assunto]),
    };
  });
}

async function checkListing(rules) {
  const listingPath = new URL(LISTING_URL).pathname;
  assert(robotsAllows(rules, listingPath), `robots.txt disallows the listing endpoint ${listingPath}`);
  const response = await politeFetch(LISTING_URL, 'application/json');
  assert(response.status === 200, `Listing endpoint returned HTTP ${response.status}`);
  const contentType = response.headers.get('content-type') ?? '';
  assert(contentType.includes('application/json'), `Listing endpoint returned content type "${contentType}"`);
  const body = await response.text();
  assert(body.length <= MAX_LISTING_BYTES, `Listing response larger than ${MAX_LISTING_BYTES} bytes`);
  const entries = parseListing(JSON.parse(body));
  const withPdf = entries.filter((entry) => entry.pdfPath);
  const byDiploma = {};
  for (const entry of entries) byDiploma[entry.diploma] = (byDiploma[entry.diploma] ?? 0) + 1;
  const dates = entries.map((entry) => entry.date).filter(Boolean).sort();
  console.log(`Listing: ${entries.length} entries, ${withPdf.length} with a PDF link, ${new Set(withPdf.map((e) => e.pdfPath)).size} distinct PDFs.`);
  console.log(`Listing: diploma "CIRS" ${byDiploma.CIRS ?? 0}, other diplomas ${entries.length - (byDiploma.CIRS ?? 0)}; dates ${dates[0]} to ${dates.at(-1)}.`);
  assert(withPdf.length >= 1, 'Listing has no entry with a PDF link');
  console.log(`Listing sample: ${JSON.stringify(withPdf[0])}`);
  return withPdf;
}

async function checkPdf(entry, rules) {
  const url = assertAllowedUrl(new URL(entry.pdfPath, ORIGIN).href);
  assert(robotsAllows(rules, url.pathname), `robots.txt disallows ${url.pathname}`);
  const response = await politeFetch(url.href, 'application/pdf');
  assert(response.status === 200, `PDF returned HTTP ${response.status}`);
  const contentType = response.headers.get('content-type') ?? '';
  assert(contentType.toLowerCase().startsWith('application/pdf'), `PDF returned content type "${contentType}"`);
  // Read only the first bytes; the document itself is not kept.
  const reader = response.body.getReader();
  const chunks = [];
  let length = 0;
  while (length < 5) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    length += value.length;
  }
  await reader.cancel();
  const magic = Buffer.concat(chunks).subarray(0, 5).toString('latin1');
  assert(magic === '%PDF-', `PDF body starts with ${JSON.stringify(magic)} instead of "%PDF-"`);
  console.log(`PDF: ${url.href} -> ${contentType}, Content-Length ${response.headers.get('content-length') ?? 'unknown'}, starts with %PDF-`);
}

async function main() {
  console.log(`User-Agent: ${USER_AGENT}`);
  const rules = await checkRobots();
  const entries = await checkListing(rules);
  await checkPdf(entries[0], rules);
  console.log(`Gate 0 probe passed with ${requestCount} request(s).`);
}

main().catch((error) => {
  console.error(`Gate 0 probe FAILED: ${error instanceof ProbeError ? error.message : error?.stack ?? error}`);
  process.exitCode = 1;
});
