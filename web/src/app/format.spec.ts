import { articleCitation, articleLabel, articlePhrase, sectionLabel, taxLabel } from './format';

describe('format', () => {
  it('labels every tax code of the index in European Portuguese', () => {
    const labels = ['CIRS', 'CIRC', 'CIVA', 'CIMI', 'CIMT', 'CIUC', 'SELO', 'EBF', 'RITI', 'LGT', 'DSRI', 'CESE', 'CSB'].map(taxLabel);
    expect(labels).toEqual([
      'IRS',
      'IRC',
      'IVA',
      'IMI',
      'IMT',
      'IUC',
      'Imposto do Selo',
      'Estatuto dos Benefícios Fiscais',
      'RITI',
      'LGT',
      'Relações internacionais',
      'CESE',
      'CSB',
    ]);
  });

  it('falls back to the code for an unknown tax, including object property names', () => {
    expect(taxLabel('CIEC')).toBe('CIEC');
    expect(taxLabel('constructor')).toBe('constructor');
    expect(taxLabel('toString')).toBe('toString');
    expect(sectionLabel('constructor')).toBe('constructor');
  });

  it('adds the ordinal sign only to numbered articles', () => {
    expect(articleLabel('13')).toBe('13.º');
    expect(articleLabel('78-D')).toBe('78.º-D');
    expect(articleLabel('Verba 1.12')).toBe('Verba 1.12');
    expect(articleLabel('Não aplicável')).toBe('Não aplicável');
    expect(articleLabel('43(Revogado)')).toBe('43(Revogado)');
  });

  it('cites numbered articles as "Art." and others as published', () => {
    expect(articleCitation('13')).toBe('Art. 13.º');
    expect(articleCitation('99-F')).toBe('Art. 99.º-F');
    expect(articleCitation('Verba 2.10')).toBe('Verba 2.10');
    expect(articlePhrase('78-D')).toBe('artigo 78.º-D');
    expect(articlePhrase('Verba 1.12')).toBe('Verba 1.12');
  });
});
