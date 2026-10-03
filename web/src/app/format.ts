// Display formats for the European Portuguese UI.

const SECTIONS: Record<string, string> = {
  header: 'Identificação',
  request: 'Pedido',
  facts: 'Factos',
  'legal-framework': 'Enquadramento',
  conclusion: 'Conclusão',
  content: 'Texto',
};

/** Display names of the tax codes of the index; an unknown code is shown as it is. */
const TAXES: Record<string, string> = {
  CIRS: 'IRS',
  CIRC: 'IRC',
  CIVA: 'IVA',
  CIMI: 'IMI',
  CIMT: 'IMT',
  CIUC: 'IUC',
  SELO: 'Imposto do Selo',
  EBF: 'Estatuto dos Benefícios Fiscais',
  RITI: 'RITI',
  LGT: 'LGT',
  DSRI: 'Relações internacionais',
  CESE: 'CESE',
  CSB: 'CSB',
};

/** A plain article number with optional letter suffixes, such as "13" or "78-D". */
const NUMBERED_ARTICLE = /^\d+(-[A-Za-z]+)*$/;

/** "78-D" becomes "78.º-D"; other values (such as "Verba 1.12") are shown as published. */
export function articleLabel(value: string): string {
  if (!NUMBERED_ARTICLE.test(value)) return value;
  const [number, ...suffix] = value.split('-');
  return number + '.º' + (suffix.length ? '-' + suffix.join('-') : '');
}

/** The article in a citation: "Art. 78.º-D" for a numbered article, otherwise the published value. */
export function articleCitation(value: string): string {
  return NUMBERED_ARTICLE.test(value) ? `Art. ${articleLabel(value)}` : value;
}

/** The article in running text: "artigo 78.º-D", otherwise the published value. */
export function articlePhrase(value: string): string {
  return NUMBERED_ARTICLE.test(value) ? `artigo ${articleLabel(value)}` : value;
}

/** "CIRS" becomes "IRS", "SELO" becomes "Imposto do Selo"; an unknown code is kept. */
export function taxLabel(value: string): string {
  return Object.hasOwn(TAXES, value) ? TAXES[value] : value;
}

export function sectionLabel(value: string): string {
  return Object.hasOwn(SECTIONS, value) ? SECTIONS[value] : value;
}

/** ISO "2020-04-15" becomes "15/04/2020". */
export function dateShort(iso: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  return match ? `${match[3]}/${match[2]}/${match[1]}` : iso;
}

/** "1 informação vinculativa" or "8 informações vinculativas". */
export function rulingCount(count: number): string {
  return count === 1 ? '1 informação vinculativa' : `${count} informações vinculativas`;
}

/** Live announcement for a completed search. */
export function foundAnnouncement(count: number): string {
  if (count === 0) return 'Nenhuma informação vinculativa encontrada.';
  return count === 1 ? '1 informação vinculativa encontrada.' : `${count} informações vinculativas encontradas.`;
}

/** Only https links are shown; anything else is not a link to the official document. */
export function isHttpsUrl(value: string): boolean {
  try {
    return new URL(value).protocol === 'https:';
  } catch {
    return false;
  }
}
