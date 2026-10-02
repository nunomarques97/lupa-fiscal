// Checks the open-source documents and copy rules. Offline, no dependencies.
// Exits 1 (listing every problem) when:
// - docs/STATE.md front-matter lacks status, sponsor_action, kill_review or success_metric (or one is empty);
// - an em-dash appears in README.md, CONTRIBUTING.md, docs/WALKTHROUGH.md or any file under web/src;
// - docs/WALKTHROUGH.md exceeds 1100 words;
// - README.md has no image, references a local image that does not exist, or references a remote image;
// - LICENSE is not the MIT licence text with a copyright line;
// - a tracked (or untracked, not ignored) file name contains "token" or "secret".
// Usage: node scripts/validate-docs.mjs [--root DIR]   (default: the repository root)
import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { basename, dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const STATE_KEYS = ['status', 'sponsor_action', 'kill_review', 'success_metric'];
export const MAX_WALKTHROUGH_WORDS = 1100;
const EM_DASH = '—';
const COPY_FILES = ['README.md', 'CONTRIBUTING.md', 'docs/WALKTHROUGH.md'];
const COPY_DIRS = ['web/src'];

export function validate(root) {
  const problems = [];
  checkState(root, problems);
  checkEmDashes(root, problems);
  checkWalkthrough(root, problems);
  checkReadmeImages(root, problems);
  checkLicense(root, problems);
  checkFileNames(root, problems);
  return problems;
}

function read(root, path, problems) {
  const full = join(root, path);
  if (!existsSync(full)) {
    problems.push(`${path}: missing`);
    return null;
  }
  return readFileSync(full, 'utf8').replace(/^﻿/, '').replace(/\r\n/g, '\n');
}

export function frontMatter(text) {
  const match = /^---\n([\s\S]*?)\n---(?:\n|$)/.exec(text);
  if (!match) return null;
  const fields = new Map();
  for (const line of match[1].split('\n')) {
    const field = /^([A-Za-z_][A-Za-z0-9_]*):(.*)$/.exec(line);
    if (field) fields.set(field[1], field[2].trim().replace(/^(["'])(.*)\1$/, '$2').trim());
  }
  return fields;
}

function checkState(root, problems) {
  const text = read(root, 'docs/STATE.md', problems);
  if (text === null) return;
  const fields = frontMatter(text);
  if (!fields) {
    problems.push('docs/STATE.md: no YAML front-matter (--- block at the top)');
    return;
  }
  for (const key of STATE_KEYS) {
    if (!fields.has(key)) problems.push(`docs/STATE.md: front-matter key "${key}" is missing`);
    else if (fields.get(key) === '') problems.push(`docs/STATE.md: front-matter key "${key}" is empty`);
  }
}

function filesUnder(root, dir) {
  const full = join(root, dir);
  if (!existsSync(full)) return [];
  const files = [];
  for (const entry of readdirSync(full, { withFileTypes: true })) {
    const path = `${dir}/${entry.name}`;
    if (entry.isDirectory()) files.push(...filesUnder(root, path));
    else if (entry.isFile()) files.push(path);
  }
  return files;
}

function checkEmDashes(root, problems) {
  const paths = [...COPY_FILES, ...COPY_DIRS.flatMap((dir) => filesUnder(root, dir))];
  for (const path of paths) {
    const full = join(root, path);
    if (!existsSync(full)) {
      if (COPY_FILES.includes(path)) problems.push(`${path}: missing`);
      continue;
    }
    const lines = readFileSync(full, 'utf8').split('\n');
    lines.forEach((line, i) => {
      if (line.includes(EM_DASH)) problems.push(`${path}:${i + 1}: em-dash in copy`);
    });
  }
}

export function countWords(markdown) {
  return markdown.split(/\s+/).filter((word) => /[\p{L}\p{N}]/u.test(word)).length;
}

function checkWalkthrough(root, problems) {
  const path = join(root, 'docs/WALKTHROUGH.md');
  if (!existsSync(path)) return; // reported as missing by the em-dash check
  const words = countWords(readFileSync(path, 'utf8'));
  if (words > MAX_WALKTHROUGH_WORDS) {
    problems.push(`docs/WALKTHROUGH.md: ${words} words, more than ${MAX_WALKTHROUGH_WORDS}`);
  }
}

export function imageReferences(markdown) {
  const refs = [];
  for (const m of markdown.matchAll(/!\[[^\]]*\]\(\s*(?:<([^>]+)>|([^)\s]+))(?:\s+"[^"]*")?\s*\)/g)) refs.push(m[1] ?? m[2]);
  for (const m of markdown.matchAll(/<img\b[^>]*?\bsrc\s*=\s*["']([^"']+)["']/gi)) refs.push(m[1]);
  for (const m of markdown.matchAll(/^\s*\[[^\]]+\]:\s*<?(\S+?\.(?:png|jpe?g|gif|svg|webp))>?(?:\s|$)/gim)) refs.push(m[1]);
  return refs;
}

function checkReadmeImages(root, problems) {
  const path = join(root, 'README.md');
  if (!existsSync(path)) return; // reported as missing by the em-dash check
  const refs = imageReferences(readFileSync(path, 'utf8'));
  if (refs.length === 0) problems.push('README.md: no image (screenshots from docs/evidence/ expected)');
  for (const ref of refs) {
    if (/^[a-z][a-z0-9+.-]*:/i.test(ref) || ref.startsWith('//')) {
      problems.push(`README.md: image ${ref} is remote; reference a file in the repository`);
      continue;
    }
    const local = decodeURI(ref.split(/[?#]/)[0]);
    const full = resolve(root, local.replace(/^\//, ''));
    if (relative(root, full).startsWith('..')) problems.push(`README.md: image ${ref} is outside the repository`);
    else if (!existsSync(full) || !statSync(full).isFile()) problems.push(`README.md: image ${ref} does not exist`);
  }
}

const MIT_SENTENCES = [
  'Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:',
  'The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.',
  'THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.',
];

function checkLicense(root, problems) {
  const text = read(root, 'LICENSE', problems);
  if (text === null) return;
  const flat = text.replace(/\s+/g, ' ').trim();
  if (!flat.startsWith('MIT License')) problems.push('LICENSE: does not start with "MIT License"');
  if (!/^Copyright \(c\) \d{4}(-\d{4})? \S.*$/m.test(text)) problems.push('LICENSE: no "Copyright (c) <year> <author>" line');
  if (MIT_SENTENCES.some((sentence) => !flat.includes(sentence))) problems.push('LICENSE: text differs from the MIT licence');
}

function checkFileNames(root, problems) {
  let output;
  try {
    output = execFileSync('git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard'], {
      cwd: root,
      encoding: 'utf8',
      maxBuffer: 64 * 1024 * 1024,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
  } catch (error) {
    problems.push(`git ls-files failed, cannot check file names: ${error.message.split('\n')[0]}`);
    return;
  }
  for (const path of output.split('\0').filter(Boolean)) {
    if (/token|secret/i.test(basename(path))) {
      problems.push(`${path}: file name contains "token" or "secret" (those patterns are reserved for credentials)`);
    }
  }
}

function main(argv) {
  const rootIndex = argv.indexOf('--root');
  const root = rootIndex >= 0 && argv[rootIndex + 1]
    ? resolve(argv[rootIndex + 1])
    : resolve(dirname(fileURLToPath(import.meta.url)), '..');
  const problems = validate(root);
  if (problems.length > 0) {
    for (const problem of problems) console.error(`validate-docs: ${problem}`);
    console.error(`validate-docs: ${problems.length} problem(s)`);
    return 1;
  }
  console.log('validate-docs: ok (STATE front-matter, copy, WALKTHROUGH length, README images, LICENSE, file names)');
  return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
