import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';
import { environment } from 'src/environments/environment';
import {
  BestRunResponse,
  CreateSessionRequest,
  OptimizationHistoryResponse,
  OptimizationSessionResponse,
  OptimizeWithSessionRequest,
  OrToolsNavigationInfo,
} from '../models/ortools-session.model';

@Injectable({
  providedIn: 'root',
})
export class OrtoolsSessionService {
  private apiUrl = environment.apiUrl;

  // trenutna sesija
  private currentSessionId = new BehaviorSubject<string | null>(null);
  public currentSessionId$ = this.currentSessionId.asObservable();

  // informacije o navigaciji
  private navigationInfo = new BehaviorSubject<OrToolsNavigationInfo | null>(
    null
  );
  public navigationInfo$ = this.navigationInfo.asObservable();

  constructor(private http: HttpClient) {}

  createSession(smena: string, datum: Date): Observable<{ sessionId: string }> {
    const request: CreateSessionRequest = {
      smena,
      datum: this.formatDate(datum),
    };

    return this.http.post<{ sessionId: string }>(
      `${this.apiUrl}/create-optimization-session`,
      request
    );
  }

  optimizeWithSession(
    sessionId: string,
    optimizationRequest: any,
    description?: string
  ): Observable<OptimizationSessionResponse> {
    const request: OptimizeWithSessionRequest = {
      sessionId,
      optimizationRequest,
      description,
    };

    return this.http.post<OptimizationSessionResponse>(
      `${this.apiUrl}/optimize-with-session`,
      request
    );
  }

  getNavigationInfo(sessionId: string): Observable<OrToolsNavigationInfo> {
    return this.http.get<OrToolsNavigationInfo>(
      `${this.apiUrl}/navigation-info/${sessionId}`
    );
  }

  navigatePrevious(sessionId: string): Observable<OptimizationSessionResponse> {
    return this.http.post<OptimizationSessionResponse>(
      `${this.apiUrl}/navigate-previous/${sessionId}`,
      {}
    );
  }

  navigateNext(sessionId: string): Observable<OptimizationSessionResponse> {
    return this.http.post<OptimizationSessionResponse>(
      `${this.apiUrl}/navigate-next/${sessionId}`,
      {}
    );
  }

  getOptimizationHistory(
    sessionId: string
  ): Observable<OptimizationHistoryResponse> {
    return this.http.get<OptimizationHistoryResponse>(
      `${this.apiUrl}/optimization-history/${sessionId}`
    );
  }

  getBestRun(sessionId: string): Observable<BestRunResponse> {
    return this.http.get<BestRunResponse>(
      `${this.apiUrl}/best-run/${sessionId}`
    );
  }

  // Upravljanje stanjem sesije
  setCurrentSession(sessionId: string | null): void {
    this.currentSessionId.next(sessionId);
  }

  getCurrentSessionId(): string | null {
    return this.currentSessionId.value;
  }

  updateNavigationInfo(info: OrToolsNavigationInfo): void {
    this.navigationInfo.next(info);
  }

  getNavigationInfo1(): OrToolsNavigationInfo | null {
    return this.navigationInfo.value;
  }

  clearSession(): void {
    this.currentSessionId.next(null);
    this.navigationInfo.next(null);
  }

  hasActiveSession(): boolean {
    return this.currentSessionId.value !== null;
  }

  hasMultipleRuns(): boolean {
    const navInfo = this.navigationInfo.value;
    return navInfo !== null && navInfo.totalRuns > 1;
  }

  canNavigate(): boolean {
    const navInfo = this.navigationInfo.value;
    return (
      navInfo !== null &&
      (navInfo.canNavigatePrevious || navInfo.canNavigateNext)
    );
  }

  loadOptimizationRun(
    sessionId: string,
    runId: number
  ): Observable<OptimizationSessionResponse> {
    return this.http.post<OptimizationSessionResponse>(
      `${this.apiUrl}/load-run/${sessionId}/${runId}`,
      {}
    );
  }

  private formatDate(date: Date): string {
    const year = date.getFullYear();
    const month = (date.getMonth() + 1).toString().padStart(2, '0');
    const day = date.getDate().toString().padStart(2, '0');
    return `${year}-${month}-${day}`;
  }
}
