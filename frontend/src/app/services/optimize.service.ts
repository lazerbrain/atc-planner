import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { environment } from 'src/environments/environment';
import { OptimizationRequest } from '../models/optimization-request.model';
import { OptimizationResponse } from '../models/optimization-response.model';
import { Observable } from 'rxjs';

@Injectable({
  providedIn: 'root',
})
export class OptimizeService {
  private apiUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  optimize(request: OptimizationRequest): Observable<OptimizationResponse> {
    return this.http.post<OptimizationResponse>(
      `${this.apiUrl}/optimize`,
      request
    );
  }
}
