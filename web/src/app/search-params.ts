import { ParamMap, Params } from '@angular/router';

/** A submitted search: the question and the filters. Empty strings mean no filter. */
export interface SearchParams {
  q: string;
  tax: string;
  article: string;
  year: string;
}

export const MAX_QUERY_LENGTH = 500;

export const NO_FILTERS = { tax: '', article: '', year: '' } as const;

export type Filters = Pick<SearchParams, 'tax' | 'article' | 'year'>;

/**
 * Reads a search from the URL. Unknown parameters are ignored; a year must be four digits; an article
 * is only meaningful within one tax code, so an article without a tax is dropped.
 */
export function paramsFromUrl(map: ParamMap): SearchParams {
  const text = (name: string) => (map.get(name) ?? '').trim();
  const year = text('year');
  const tax = text('tax');
  return {
    q: text('q').slice(0, MAX_QUERY_LENGTH),
    tax,
    article: tax ? text('article') : '',
    year: /^\d{4}$/.test(year) ? year : '',
  };
}

/** Filters after a change of the tax: the article belongs to the previous tax, so it is cleared. */
export function withTax(filters: Filters, tax: string): Filters {
  return tax === filters.tax ? filters : { ...filters, tax, article: '' };
}

/** Query parameters for the URL; absent filters are removed rather than left empty. */
export function paramsToUrl(params: SearchParams): Params {
  return {
    q: params.q || null,
    tax: params.tax || null,
    article: params.article || null,
    year: params.year || null,
  };
}

export function sameParams(a: SearchParams | null, b: SearchParams | null): boolean {
  if (!a || !b) return a === b;
  return a.q === b.q && a.tax === b.tax && a.article === b.article && a.year === b.year;
}
