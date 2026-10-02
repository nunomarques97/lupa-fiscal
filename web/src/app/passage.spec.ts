import { articleLabel, dateShort, isHttpsUrl, rulingCount } from './format';
import { mergeHighlights, passageView } from './passage';

describe('passageView', () => {
  it('splits the passage into plain and marked segments from offsets', () => {
    const view = passageView('Despesas de educação dos filhos', [{ start: 12, length: 8 }, { start: 0, length: 8 }], null);
    expect(view.segments).toEqual([
      { text: 'Despesas', mark: true },
      { text: ' de ', mark: false },
      { text: 'educação', mark: true },
      { text: ' dos filhos', mark: false },
    ]);
    expect(view.before).toBe(false);
    expect(view.after).toBe(false);
  });

  it('keeps offsets valid across PDF line breaks', () => {
    const view = passageView('linha um\nlinha dois', [{ start: 9, length: 5 }], null);
    expect(view.segments.find(s => s.mark)?.text).toBe('linha');
  });

  it('cuts a long passage at word boundaries and reports the cut', () => {
    const passage = 'palavra '.repeat(100).trim();
    const view = passageView(passage, [], 50);
    const text = view.segments.map(s => s.text).join('');
    expect(text.length).toBeLessThanOrEqual(50);
    expect(text.endsWith('palavra')).toBe(true);
    expect(view.after).toBe(true);
    expect(view.before).toBe(false);
  });

  it('moves the excerpt to a later window holding at least two more highlights', () => {
    const passage = 'a '.repeat(200) + 'IRS IRS IRS ' + 'b '.repeat(50);
    const start = 400;
    const view = passageView(passage, [{ start, length: 3 }, { start: start + 4, length: 3 }, { start: start + 8, length: 3 }], 100);
    expect(view.before).toBe(true);
    expect(view.segments.filter(s => s.mark)).toHaveLength(3);
  });
});

describe('mergeHighlights', () => {
  it('merges overlapping highlights and drops invalid ones', () => {
    expect(
      mergeHighlights(
        [
          { start: 5, length: 5 },
          { start: 0, length: 6 },
          { start: -4, length: 2 },
          { start: 40, length: 10 },
          { start: 20, length: 0 },
          { start: 1.5, length: 2 },
        ],
        30,
      ),
    ).toEqual([{ start: 0, length: 10 }]);
  });

  it('clamps a highlight that runs past the end', () => {
    expect(mergeHighlights([{ start: 8, length: 10 }], 10)).toEqual([{ start: 8, length: 2 }]);
  });
});

describe('format', () => {
  it('formats articles, dates and counts', () => {
    expect(articleLabel('13')).toBe('13.º');
    expect(articleLabel('78-D')).toBe('78.º-D');
    expect(dateShort('2020-04-15')).toBe('15/04/2020');
    expect(rulingCount(1)).toBe('1 informação vinculativa');
    expect(rulingCount(8)).toBe('8 informações vinculativas');
  });

  it('accepts only https links', () => {
    expect(isHttpsUrl('https://info.portaldasfinancas.gov.pt/a.pdf')).toBe(true);
    expect(isHttpsUrl('javascript:alert(1)')).toBe(false);
    expect(isHttpsUrl('http://example.org')).toBe(false);
    expect(isHttpsUrl('not a url')).toBe(false);
  });
});
