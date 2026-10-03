import { Component, computed, input, output } from '@angular/core';

import { FacetsResponse } from './api';
import { articleLabel, taxLabel } from './format';
import { Filters, withTax } from './search-params';

interface Option {
  value: string;
  label: string;
}

interface Field {
  key: keyof Filters;
  id: string;
  label: string;
  options: Option[];
  /** Shown under a disabled select and linked to it with aria-describedby. */
  hint: string | null;
}

export const ARTICLE_HINT = 'Escolha primeiro um imposto.';

/**
 * The three filter selects (tax, article, year), used in the desktop rail and the mobile sheet. An
 * article number is only meaningful within one tax code, so the article select is disabled until a
 * tax is chosen, lists only that tax's articles, and is cleared when the tax changes.
 */
@Component({
  selector: 'app-filter-fields',
  template: `
    @for (field of fields(); track field.key) {
      <div class="field">
        <label [for]="field.id">{{ field.label }}</label>
        <select
          [id]="field.id"
          [attr.name]="field.key"
          [disabled]="!!field.hint"
          [attr.aria-describedby]="field.hint ? field.id + '-hint' : null"
          (change)="select(field.key, $event)"
        >
          @for (option of field.options; track option.value) {
            <option [value]="option.value" [selected]="option.value === value()[field.key]">{{ option.label }}</option>
          }
        </select>
        @if (field.hint) {
          <p class="field-hint" [id]="field.id + '-hint'">{{ field.hint }}</p>
        }
      </div>
    }
  `,
})
export class FilterFields {
  readonly idPrefix = input.required<string>();
  readonly facets = input<FacetsResponse | null>(null);
  readonly value = input.required<Filters>();
  readonly changed = output<Filters>();

  protected readonly fields = computed<Field[]>(() => {
    const facets = this.facets();
    const value = this.value();
    const prefix = this.idPrefix();
    const counted = (label: string, count: number) => `${label} (${count})`;
    const taxes = [...(facets?.taxes ?? [])].sort((a, b) => taxLabel(a.value).localeCompare(taxLabel(b.value), 'pt'));
    const articles = value.tax ? (facets?.articles ?? []).filter(f => f.tax === value.tax) : [];
    return [
      {
        key: 'tax',
        id: `${prefix}-tax`,
        label: 'Imposto',
        options: withCurrent(
          taxes.map(f => ({ value: f.value, label: counted(taxLabel(f.value), f.count) })),
          value.tax,
          taxLabel,
        ),
        hint: null,
      },
      {
        key: 'article',
        id: `${prefix}-article`,
        label: 'Artigo',
        options: value.tax
          ? withCurrent(
              articles.map(f => ({ value: f.value, label: counted(articleLabel(f.value), f.count) })),
              value.article,
              articleLabel,
            )
          : withCurrent([], '', articleLabel),
        hint: value.tax ? null : ARTICLE_HINT,
      },
      {
        key: 'year',
        id: `${prefix}-year`,
        label: 'Ano de publicação',
        options: withCurrent(
          (facets?.years ?? []).map(f => ({ value: String(f.value), label: counted(String(f.value), f.count) })),
          value.year,
          v => v,
        ),
        hint: null,
      },
    ];
  });

  protected select(key: keyof Filters, event: Event): void {
    const selected = (event.target as HTMLSelectElement).value;
    const value = this.value();
    this.changed.emit(key === 'tax' ? withTax(value, selected) : { ...value, [key]: selected });
  }
}

/** "Todos" first, then the facet values; a current value missing from the facets is kept visible. */
function withCurrent(options: Option[], current: string, format: (value: string) => string): Option[] {
  const all = [{ value: '', label: 'Todos' }, ...options];
  if (current && !options.some(o => o.value === current)) all.push({ value: current, label: format(current) });
  return all;
}
