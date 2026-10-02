import { Component, computed, input, output } from '@angular/core';

import { FacetsResponse } from './api';
import { articleLabel, taxLabel } from './format';
import { Filters } from './search-params';

interface Option {
  value: string;
  label: string;
}

interface Field {
  key: keyof Filters;
  id: string;
  label: string;
  options: Option[];
}

/** The three filter selects (tax, article, year), used in the desktop rail and the mobile sheet. */
@Component({
  selector: 'app-filter-fields',
  template: `
    @for (field of fields(); track field.key) {
      <div class="field">
        <label [for]="field.id">{{ field.label }}</label>
        <select [id]="field.id" [attr.name]="field.key" (change)="select(field.key, $event)">
          @for (option of field.options; track option.value) {
            <option [value]="option.value" [selected]="option.value === value()[field.key]">{{ option.label }}</option>
          }
        </select>
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
    return [
      {
        key: 'tax',
        id: `${prefix}-tax`,
        label: 'Imposto',
        options: withCurrent(
          (facets?.taxes ?? []).map(f => ({ value: f.value, label: counted(taxLabel(f.value), f.count) })),
          value.tax,
          taxLabel,
        ),
      },
      {
        key: 'article',
        id: `${prefix}-article`,
        label: 'Artigo',
        options: withCurrent(
          (facets?.articles ?? []).map(f => ({ value: f.value, label: counted(articleLabel(f.value), f.count) })),
          value.article,
          articleLabel,
        ),
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
      },
    ];
  });

  protected select(key: keyof Filters, event: Event): void {
    const target = event.target as HTMLSelectElement;
    this.changed.emit({ ...this.value(), [key]: target.value });
  }
}

/** "Todos" first, then the facet values; a current value missing from the facets is kept visible. */
function withCurrent(options: Option[], current: string, format: (value: string) => string): Option[] {
  const all = [{ value: '', label: 'Todos' }, ...options];
  if (current && !options.some(o => o.value === current)) all.push({ value: current, label: format(current) });
  return all;
}
