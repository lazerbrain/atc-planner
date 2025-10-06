import { Injectable } from '@angular/core';
import { environment } from 'src/environments/environment';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { catchError, Observable, throwError } from 'rxjs';
import { RosterResponse } from '../models/roster-response.model';

@Injectable({
  providedIn: 'root',
})
export class RosterService {
  private apiUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  getRoster(date: Date, shift: string): Observable<RosterResponse> {
    const formattedDate = this.formatDate(date);
    return this.http
      .get<RosterResponse>(
        `${this.apiUrl}/get-roster?datum=${formattedDate}&smena=${shift}`
      )
      .pipe(
        catchError((error: HttpErrorResponse) => {
          console.error('Error in getRoster:', error);
          return throwError(
            'Raspored nije pronađen ili je došlo do greške pri učitavanju.'
          );
        })
      );
  }

  private formatDate(date: Date): string {
    const year = date.getFullYear();
    const month = (date.getMonth() + 1).toString().padStart(2, '0');
    const day = date.getDate().toString().padStart(2, '0');
    return `${year}-${month}-${day}`;
  }
}
