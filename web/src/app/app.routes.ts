import { Routes } from '@angular/router';

// One page; the search lives in the query string (?q=&tax=&article=&year=).
export const routes: Routes = [{ path: '**', children: [] }];
