import { convertToParamMap } from '@angular/router';

import { paramsFromUrl, paramsToUrl, withTax } from './search-params';

describe('search params', () => {
  it('reads a tax-scoped article from the URL', () => {
    expect(paramsFromUrl(convertToParamMap({ q: ' iva ', tax: 'CIVA', article: 'Verba 1.12', year: '2020' }))).toEqual({
      q: 'iva',
      tax: 'CIVA',
      article: 'Verba 1.12',
      year: '2020',
    });
  });

  it('drops an article without a tax', () => {
    expect(paramsFromUrl(convertToParamMap({ q: 'x', article: '13' }))).toEqual({ q: 'x', tax: '', article: '', year: '' });
    expect(paramsFromUrl(convertToParamMap({ q: 'x', tax: '  ', article: '13' })).article).toBe('');
  });

  it('writes only the filters that are set', () => {
    expect(paramsToUrl({ q: 'x', tax: 'CIRS', article: '13', year: '' })).toEqual({ q: 'x', tax: 'CIRS', article: '13', year: null });
  });

  it('clears the article when the tax changes, and only then', () => {
    const filters = { tax: 'CIRS', article: '13', year: '2020' };
    expect(withTax(filters, 'CIVA')).toEqual({ tax: 'CIVA', article: '', year: '2020' });
    expect(withTax(filters, '')).toEqual({ tax: '', article: '', year: '2020' });
    expect(withTax(filters, 'CIRS')).toBe(filters);
  });
});
