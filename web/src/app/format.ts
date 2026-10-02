// Display formats for the European Portuguese UI.

const SECTIONS: Record<string, string> = {
  header: 'Identificação',
  request: 'Pedido',
  facts: 'Factos',
  'legal-framework': 'Enquadramento',
  conclusion: 'Conclusão',
  content: 'Texto',
};

const TAXES: Record<string, string> = { CIRS: 'IRS' };

/** "78-D" becomes "78.º-D". */
export function articleLabel(value: string): string {
  const [number, ...suffix] = value.split('-');
  return number + '.º' + (suffix.length ? '-' + suffix.join('-') : '');
}

/** "CIRS" becomes "IRS"; other taxes keep their code. */
export function taxLabel(value: string): string {
  return TAXES[value] ?? value;
}

export function sectionLabel(value: string): string {
  return SECTIONS[value] ?? value;
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
