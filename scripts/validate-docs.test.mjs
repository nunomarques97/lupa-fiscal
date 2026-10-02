// Tests for validate-docs.mjs on small temporary repositories. Run: node --test scripts/validate-docs.test.mjs
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, test } from 'node:test';
import { countWords, frontMatter, imageReferences, validate } from './validate-docs.mjs';

const script = join(dirname(fileURLToPath(import.meta.url)), 'validate-docs.mjs');
const repoLicense = readFileSync(join(dirname(script), '..', 'LICENSE'), 'utf8');
const roots = [];

function write(root, path, content) {
  mkdirSync(dirname(join(root, path)), { recursive: true });
  writeFileSync(join(root, path), content);
}

function validRepo() {
  const root = mkdtempSync(join(tmpdir(), 'validate-docs-'));
  roots.push(root);
  execFileSync('git', ['init', '--quiet'], { cwd: root });
  write(root, 'docs/STATE.md', '---\nstatus: done\nsponsor_action: none\nkill_review: passed\nsuccess_metric: recall\n---\n\n# State\n');
  write(root, 'README.md', '# Demo\n\n![Results](docs/evidence/results-1440.png)\n');
  write(root, 'docs/evidence/results-1440.png', 'png');
  write(root, 'CONTRIBUTING.md', '# Contributing - plain hyphens and en-dashes – are fine\n');
  write(root, 'docs/WALKTHROUGH.md', '# Walkthrough\n\nShort.\n');
  write(root, 'web/src/app/app.html', '<p>Pesquisa</p>\n');
  write(root, 'LICENSE', repoLicense);
  write(root, '.gitignore', '*secret*\n');
  return root;
}

afterEach(() => {
  while (roots.length > 0) rmSync(roots.pop(), { recursive: true, force: true });
});

test('a valid repository passes, and the CLI exits 0', () => {
  const root = validRepo();
  assert.deepEqual(validate(root), []);
  const run = spawnSync(process.execPath, [script, '--root', root], { encoding: 'utf8' });
  assert.equal(run.status, 0, run.stderr);
});

test('missing or empty STATE front-matter keys fail', () => {
  const root = validRepo();
  write(root, 'docs/STATE.md', '---\nstatus: done\nsponsor_action:\nkill_review: passed\n---\n');
  const problems = validate(root);
  assert.ok(problems.some((p) => p.includes('"sponsor_action" is empty')), problems.join('\n'));
  assert.ok(problems.some((p) => p.includes('"success_metric" is missing')), problems.join('\n'));
  write(root, 'docs/STATE.md', '# State\n\nstatus: done\n');
  assert.ok(validate(root).some((p) => p.includes('no YAML front-matter')));
});

test('CRLF front-matter is read', () => {
  const fields = frontMatter('---\r\nstatus: done\r\n---\r\n'.replace(/\r\n/g, '\n'));
  assert.equal(fields.get('status'), 'done');
  const root = validRepo();
  write(root, 'docs/STATE.md', '---\r\nstatus: done\r\nsponsor_action: none\r\nkill_review: passed\r\nsuccess_metric: m\r\n---\r\n');
  assert.deepEqual(validate(root), []);
});

test('an em-dash in README, CONTRIBUTING, WALKTHROUGH or web/src fails with its line', () => {
  for (const path of ['README.md', 'CONTRIBUTING.md', 'docs/WALKTHROUGH.md', 'web/src/app/deep/copy.ts']) {
    const root = validRepo();
    const before = path === 'README.md' ? '# Demo\n\n![Results](docs/evidence/results-1440.png)\n' : 'ok\n';
    write(root, path, `${before}Pesquisa — resultados\n`);
    const problems = validate(root);
    assert.ok(problems.some((p) => p.startsWith(`${path}:`) && p.includes('em-dash')), `${path}: ${problems.join('\n')}`);
  }
});

test('a WALKTHROUGH over 1100 words fails', () => {
  const root = validRepo();
  write(root, 'docs/WALKTHROUGH.md', `# W\n\n${'palavra '.repeat(1099)}\n`);
  assert.deepEqual(validate(root), []); // "W" plus 1099 words = 1100
  write(root, 'docs/WALKTHROUGH.md', `# W\n\n${'palavra '.repeat(1100)}\n`);
  assert.ok(validate(root).some((p) => p.includes('1101 words')));
  assert.equal(countWords('| a | b |\n|---|---|\n# Title -'), 3);
});

test('README image problems fail: missing file, remote image, no image', () => {
  const root = validRepo();
  write(root, 'README.md', '![a](docs/evidence/results-1440.png)\n<img src="docs/evidence/nope.png" alt="x">\n![b](https://example.com/x.png)\n');
  const problems = validate(root);
  assert.ok(problems.some((p) => p.includes('docs/evidence/nope.png does not exist')), problems.join('\n'));
  assert.ok(problems.some((p) => p.includes('https://example.com/x.png is remote')), problems.join('\n'));
  write(root, 'README.md', '# No images\n');
  assert.ok(validate(root).some((p) => p.includes('README.md: no image')));
  assert.deepEqual(imageReferences('![x](<a b.png> "t")\n[ref]: docs/y.png\n'), ['a b.png', 'docs/y.png']);
});

test('a LICENSE that is not MIT fails', () => {
  const root = validRepo();
  write(root, 'LICENSE', repoLicense.replace('MERCHANTABILITY', 'MERCHANTABILITY, TITLE'));
  assert.ok(validate(root).some((p) => p.includes('differs from the MIT licence')));
  write(root, 'LICENSE', 'Apache License\nVersion 2.0, January 2004\n');
  const problems = validate(root);
  assert.ok(problems.some((p) => p.includes('does not start with "MIT License"')));
  assert.ok(problems.some((p) => p.includes('no "Copyright (c) <year> <author>" line')));
});

test('tracked or untracked file names with token or secret fail; ignored ones do not', () => {
  const root = validRepo();
  write(root, 'src/my-secret.json', '{}'); // ignored by .gitignore: not reported
  assert.deepEqual(validate(root), []);
  write(root, 'src/Tokenizer.cs', '');
  execFileSync('git', ['add', '-f', 'src/my-secret.json'], { cwd: root }); // tracked despite the ignore rule
  const problems = validate(root);
  assert.ok(problems.some((p) => p.startsWith('src/Tokenizer.cs:')), problems.join('\n'));
  assert.ok(problems.some((p) => p.startsWith('src/my-secret.json:')), problems.join('\n'));
});

test('the CLI exits 1 and lists the problems', () => {
  const root = validRepo();
  rmSync(join(root, 'LICENSE'));
  const run = spawnSync(process.execPath, [script, '--root', root], { encoding: 'utf8' });
  assert.equal(run.status, 1);
  assert.match(run.stderr, /LICENSE: missing/);
});
