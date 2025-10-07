import { Component, ViewChild, ViewContainerRef } from '@angular/core';
import {
  DialogCloseResult,
  DialogRef,
  DialogService,
} from '@progress/kendo-angular-dialog';
import { BehaviorSubject, throwError, Observable, finalize, map } from 'rxjs';
import { catchError } from 'rxjs/operators';
import {
  ConfigurationEntry,
  PivotedConfigurationEntry,
  PivotedRosterEntry,
  RosterData,
  TimeSlot,
} from 'src/app/models/roster-response.model';
import { RosterService } from 'src/app/services/roster.service';
import {
  OptimizationDialogComponent,
  OptimizationDialogResult,
} from './optimization-dialog/optimization-dialog.component';
import { OptimizationParams } from 'src/app/models/optimization-params.model';
import { OptimizationRequest } from 'src/app/models/optimization-request.model';
import {
  OptimizationResponse,
  OptimizationStatistics,
} from 'src/app/models/optimization-response.model';
import { OptimizeService } from 'src/app/services/optimize.service';
import { OptimizationStatisticsDialogComponent } from './optimization-statistics-dialog/optimization-statistics-dialog.component';
import {
  arrowLeftIcon,
  gearIcon,
  chartColumnClusteredIcon,
  chevronLeftIcon,
  chevronRightIcon,
  playIcon,
  starIcon,
  clockIcon,
} from '@progress/kendo-svg-icons';
import { DrawerService } from 'src/app/shared/services/drawer.service';
import { MessageDialogComponent } from 'src/app/shared/components/message-dialog/message-dialog.component';
import { OrtoolsSessionService } from 'src/app/services/ortools-session.service';
import { OrToolsNavigationInfo } from 'src/app/models/ortools-session.model';
import { OptimizationHistoryDialogComponent } from './optimization-history-dialog/optimization-history-dialog.component';

@Component({
  selector: 'app-schedule',
  templateUrl: './schedule.component.html',
  styleUrls: ['./schedule.component.css'],
})
export class ScheduleComponent {
  initialSchedule: (PivotedRosterEntry | PivotedConfigurationEntry)[] = [];
  optimizedSchedule: (PivotedRosterEntry | PivotedConfigurationEntry)[] = [];
  @ViewChild('dialogContainer', { read: ViewContainerRef })
  dialogContainerRef!: ViewContainerRef;

  public icons = {
    arrowLeft: arrowLeftIcon,
    gear: gearIcon,
    chartColumn: chartColumnClusteredIcon,
    chevronLeft: chevronLeftIcon,
    chevronRight: chevronRightIcon,
    play: playIcon,
    star: starIcon,
    clock: clockIcon,
  };

  selectedDate: Date | null = null;
  selectedShift: string | null = null;

  timeSlots: TimeSlot[] = [];

  showOptimizeButton: boolean = false;
  showTabs: boolean = false;
  selectedTab: number = 0;

  loading$ = new BehaviorSubject<boolean>(false);
  optimizing$ = new BehaviorSubject<boolean>(false);

  private progressSubject$ = new BehaviorSubject<number>(0);
  progress$ = this.progressSubject$
    .asObservable()
    .pipe(map((progress) => Math.max(0, Math.min(100, progress))));

  selectedEmployees: string[] = [];

  hasOptimizedSchedule: boolean = false;
  optimizationStatistics: OptimizationStatistics | null = null;

  slotShortages: { [key: string]: number } = {};

  isOptimizedScheduleDisplayed: boolean = false;

  currentSessionId: string | null = null;
  navigationInfo: OrToolsNavigationInfo | null = null;
  isReoptimizing: boolean = false;

  constructor(
    private rosterService: RosterService,
    private dialogService: DialogService,
    private optimizeService: OptimizeService,
    private drawerService: DrawerService,
    private orToolsSessionService: OrtoolsSessionService
  ) {}

  loadSchedule(formData: { date: Date; shift: string }) {
    this.selectedDate = formData.date;
    this.selectedShift = formData.shift;

    this.drawerService.setScheduleInfo(this.selectedDate, this.selectedShift);

    this.loading$.next(true);
    this.progressSubject$.next(0);

    // Kreiraj novu OR-Tools sesiju
    this.orToolsSessionService
      .createSession(this.selectedShift, this.selectedDate)
      .subscribe({
        next: (response) => {
          this.currentSessionId = response.sessionId;
          this.orToolsSessionService.setCurrentSession(this.currentSessionId);
          console.log('Created OR-Tools session:', this.currentSessionId);
        },
        error: (error) => {
          console.error('Error creating OR-Tools session:', error);
        },
      });

    this.rosterService
      .getRoster(this.selectedDate, this.selectedShift)
      .pipe(
        finalize(() => {
          this.loading$.next(false);
          this.progressSubject$.next(100);
        })
      )
      .subscribe(
        (response) => {
          this.processInitialSchedule(response.initialRoster);
          this.showOptimizeButton = true;
          this.showTabs = true;
          this.progressSubject$.next(100);

          setTimeout(() => {
            this.drawerService.setDrawerState(false);
          }, 0);
        },
        (error) => {
          console.error('Error loading schedule:', error);
          this.progressSubject$.next(0);
        }
      );
  }

  optimizationParams: OptimizationParams = {
    maxExecutionTime: 60,
    useHeuristic: false,
    useSimulatedAnnealing: false,
    stopAfterOptimalSolutions: 0,
    stopAfterZeroShortageSolutions: 0,
    useManualAssignments: true,
  };

  openOptimizationDialog() {
    const dialog: DialogRef = this.dialogService.open({
      title: 'Parametri optimizacije',
      content: OptimizationDialogComponent,
      width: 450,
      height: 550,
      appendTo: this.dialogContainerRef,
    });

    const dialogContent = dialog.content
      .instance as OptimizationDialogComponent;
    dialogContent.params = { ...this.optimizationParams };

    dialog.result.subscribe(
      (result: OptimizationDialogResult | DialogCloseResult) => {
        if (result !== undefined && !(result instanceof DialogCloseResult)) {
          if (
            result.action === 'optimize' &&
            this.isValidOptimizationParams(result.params)
          ) {
            this.optimizationParams = result.params;
            this.optimizeSchedule();
          } else {
            console.error('Invalid optimization parameters:', result);
          }
        }
      }
    );
  }

  public openStatisticsDialog(statistics: OptimizationStatistics) {
    if (!this.optimizationStatistics) {
      console.error('No optimization statistics available');
      return;
    }

    const dialog: DialogRef = this.dialogService.open({
      title: 'Statistika optimizacije',
      content: OptimizationStatisticsDialogComponent,
      width: 500,
      height: 600,
      appendTo: this.dialogContainerRef,
    });

    const dialogContent = dialog.content
      .instance as OptimizationStatisticsDialogComponent;
    dialogContent.statistics = statistics!;
  }

  public showOptimizationStatistics(): void {
    if (!this.optimizationStatistics) {
      console.error('No optimization statistics available');
      return;
    }

    this.openStatisticsDialog(this.optimizationStatistics);
  }

  private isValidOptimizationParams(params: any): params is OptimizationParams {
    return (
      typeof params === 'object' &&
      typeof params.maxExecutionTime === 'number' &&
      params.maxExecutionTime > 0 &&
      typeof params.useHeuristic === 'boolean' &&
      typeof params.stopAfterOptimalSolutions === 'number' &&
      params.stopAfterOptimalSolutions >= 0 &&
      typeof params.stopAfterZeroShortageSolutions === 'number' &&
      params.stopAfterZeroShortageSolutions >= 0 &&
      typeof params.useManualAssignments === 'boolean'
    );
  }

  optimizeSchedule() {
    if (!this.selectedDate || !this.selectedShift || !this.currentSessionId) {
      console.error('Date, shift, or session not available');
      return;
    }

    const formattedDate = this.selectedDate.toLocaleDateString('en-CA', {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    });

    const request: OptimizationRequest = {
      smena: this.selectedShift,
      datum: formattedDate,
      maxExecTime: this.optimizationParams.maxExecutionTime,
      maxOptimalSolutions: this.optimizationParams.stopAfterOptimalSolutions,
      maxZeroShortageSlots:
        this.optimizationParams.stopAfterZeroShortageSolutions,
      useLNS: this.optimizationParams.useHeuristic,
      useSimulatedAnnealing: this.optimizationParams.useSimulatedAnnealing,
      selectedOperativeWorkplaces: [],
      selectedEmployees: this.selectedEmployees,
      useManualAssignments: this.optimizationParams.useManualAssignments,
    };

    this.optimizing$.next(true);
    this.isReoptimizing = this.hasOptimizedSchedule;

    // this.optimizeService
    //   .optimize(request)
    //   .pipe(
    //     catchError((error: any): Observable<OptimizationResponse> => {
    //       console.error('Caught error:', error);
    //       const errorMessage =
    //         error?.error?.message ||
    //         'Došlo je do greške prilikom optimizacije.';
    //       const solutionStatus = error?.error?.statistics?.solutionStatus;
    //       this.showDialog(errorMessage, 'error', solutionStatus);
    //       return throwError(() => error);
    //     }),
    //     finalize(() => {
    //       this.optimizing$.next(false);
    //     })
    //   )
    //   .subscribe({
    //     next: (response: OptimizationResponse) => {
    //       console.log('Success response:', response);
    //       this.processOptimizedSchedule(response);
    //       this.selectedTab = 1;
    //       this.hasOptimizedSchedule = true;
    //       this.optimizationStatistics = response.statistics;
    //       this.slotShortages = this.formatSlotShortages(response.slotShortages);
    //       this.showOptimizationStatistics();
    //     },
    //     error: (err: any) => {
    //       console.error('Subscribe error handler:', err);
    //     },
    //   });
    // Ako nema sesije, kreiraj je
    if (!this.currentSessionId) {
      this.orToolsSessionService
        .createSession(this.selectedShift, this.selectedDate)
        .subscribe({
          next: (response) => {
            this.currentSessionId = response.sessionId;
            this.orToolsSessionService.setCurrentSession(this.currentSessionId);
            console.log(
              'Created OR-Tools session for optimization:',
              this.currentSessionId
            );
            // Pozovi optimizaciju nakon kreiranja sesije
            this.runOptimization();
          },
          error: (error) => {
            console.error('Error creating OR-Tools session:', error);
            this.showDialog(
              'Greška pri kreiranju sesije optimizacije',
              'error'
            );
          },
        });
    } else {
      // Sesija već postoji, pokreni optimizaciju
      this.runOptimization();
    }
  }

  private runOptimization() {
    if (!this.currentSessionId) return;

    const formattedDate = this.selectedDate!.toLocaleDateString('en-CA', {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    });

    const randomSeed = Math.floor(Math.random() * 1000000);

    // Jednostavno menjanje parametara za različite rezultate
    const params = { ...this.optimizationParams };

    // Varira maxExecutionTime blago za različite rezultate
    const timeVariation = Math.floor(Math.random() * 10) - 5; // -5 do +5 sekundi
    params.maxExecutionTime = Math.max(
      10,
      params.maxExecutionTime + timeVariation
    );

    const request = {
      smena: this.selectedShift!,
      datum: formattedDate,
      maxExecTime: this.optimizationParams.maxExecutionTime,
      maxOptimalSolutions: this.optimizationParams.stopAfterOptimalSolutions,
      maxZeroShortageSlots:
        this.optimizationParams.stopAfterZeroShortageSolutions,
      useLNS: this.optimizationParams.useHeuristic,
      useSimulatedAnnealing: false, // Uvek koristimo OR-Tools
      selectedOperativeWorkplaces: [],
      selectedEmployees: this.selectedEmployees,
      useManualAssignments: this.optimizationParams.useManualAssignments, //true,
      randomSeed: randomSeed,
      useRandomization: true,
    };

    this.optimizing$.next(true);
    this.isReoptimizing = this.hasOptimizedSchedule;

    // KLJUČNO: Koristimo OrToolsSessionService umesto OptimizeService
    this.orToolsSessionService
      .optimizeWithSession(
        this.currentSessionId,
        request,
        this.generateOptimizationDescription()
      )
      .pipe(
        catchError((error: any): Observable<any> => {
          console.error('Caught error:', error);
          const errorMessage =
            error?.error?.message ||
            'Došlo je do greške prilikom optimizacije.';
          const solutionStatus = error?.error?.statistics?.solutionStatus;
          this.showDialog(errorMessage, 'error', solutionStatus);
          return throwError(() => error);
        }),
        finalize(() => {
          this.optimizing$.next(false);
          this.isReoptimizing = false;
        })
      )
      .subscribe({
        next: (response) => {
          console.log('OR-Tools Session optimization response:', response);

          // Ažuriraj komponente
          this.processOptimizedSchedule(response);
          this.selectedTab = 1;
          this.hasOptimizedSchedule = true;
          this.optimizationStatistics = response.statistics;
          this.slotShortages = this.formatSlotShortages(response.slotShortages);

          // KLJUČNO: Ažuriraj navigation info
          this.navigationInfo = response.navigationInfo;
          this.orToolsSessionService.updateNavigationInfo(
            response.navigationInfo
          );

          console.log('Navigation info updated:', this.navigationInfo);

          this.showOptimizationStatistics();
        },
        error: (err: any) => {
          console.error('Subscribe error handler:', err);
        },
      });
  }

  private formatSlotShortages(shortages: { [key: string]: number }): {
    [key: string]: number;
  } {
    const formattedShortages: { [key: string]: number } = {};
    for (const [key, value] of Object.entries(shortages)) {
      const formattedKey = this.formatTimeSlot(key);
      formattedShortages[formattedKey] = value;
    }
    return formattedShortages;
  }

  onSelectedEmployeesChange(selectedEmployees: string[]) {
    this.selectedEmployees = selectedEmployees;
  }

  private processOptimizedSchedule(response: OptimizationResponse) {
    const preprocessedResults = this.preprocessRosterData(
      response.optimizedResults
    );
    const rosterEntries = this.processRosterEntries(preprocessedResults);
    const configEntry = this.processOptimizedConfigurationEntries(
      response.configurationLabels
    );

    this.optimizedSchedule = [...rosterEntries, configEntry];
    console.log(rosterEntries);
  }

  private processOptimizedConfigurationEntries(configLabels: {
    [key: string]: string;
  }): PivotedConfigurationEntry {
    const configEntry: PivotedConfigurationEntry = { config: 'Configuration' };
    Object.entries(configLabels).forEach(([timeSlot, label]) => {
      const formattedTimeSlot = this.formatTimeSlot(timeSlot);
      configEntry[formattedTimeSlot] = label;
    });

    return configEntry;
  }

  private formatTimeSlot(timeSlot: string): string {
    const [start, end] = timeSlot.split('|');
    const formatDate = (dateString: string) => {
      return dateString.replace(' ', 'T');
    };
    return `${formatDate(start)}-${formatDate(end)}`;
  }

  private processRosterEntries(rosterEntries: any[]): PivotedRosterEntry[] {
    return rosterEntries.reduce((acc, entry) => {
      const existingEntry = acc.find(
        (e: PivotedRosterEntry) => e.sifra === entry.sifra
      );
      const timeSlotKey = `${entry.datumOd}-${entry.datumDo}`;

      if (existingEntry) {
        existingEntry.timeSlots[timeSlotKey] = {
          value: entry.sektor,
          flag: entry.flag,
          isIntermediateShift: entry.vremeStart !== entry.datumOd,
        };
      } else {
        const newEntry: PivotedRosterEntry = {
          sifra: entry.sifra,
          prezimeIme: entry.prezimeIme,
          smena: entry.smena,
          orm: entry.orm,
          redosled: entry.redosled || 0, // Dodajemo default vrednost ako redosled ne postoji
          timeSlots: {
            [timeSlotKey]: {
              value: entry.sektor,
              flag: entry.flag,
              isIntermediateShift: entry.vremeStart !== entry.datumOd,
            },
          },
        };
        acc.push(newEntry);
      }
      return acc;
    }, [] as PivotedRosterEntry[]);
  }

  private preprocessRosterData(rosterData: any[]): any[] {
    return rosterData.map((entry) => ({
      ...entry,
      flag: entry.flag || null, // Postavi na null ako ne postoji
      redosled: entry.redosled || 0, // Postavi na 0 ako ne postoji
    }));
  }

  private processInitialSchedule(rosterData: RosterData) {
    const preprocessedRoster = this.preprocessRosterData(rosterData.roster);
    this.timeSlots = this.extractTimeSlots(preprocessedRoster);
    const rosterEntries = this.processRosterEntries(preprocessedRoster);
    const configEntry = this.processConfigurationEntries(
      rosterData.configurationSchedule
    );
    this.initialSchedule = [...rosterEntries, configEntry];
  }

  private processConfigurationEntries(
    configEntries: ConfigurationEntry[]
  ): PivotedConfigurationEntry {
    const configEntry: PivotedConfigurationEntry = { config: 'Configuration' };
    configEntries.forEach((entry) => {
      configEntry[`${entry.datumOd}-${entry.datumDo}`] =
        entry.oznakaKonfiguracije;
    });
    return configEntry;
  }

  private extractTimeSlots(rosterEntries: any[]): TimeSlot[] {
    return [
      ...new Set(
        rosterEntries.map((entry) => `${entry.datumOd}-${entry.datumDo}`)
      ),
    ].sort();
  }

  onTabSelect(e: any) {
    this.selectedTab = e.index;
  }

  resetSelection() {
    this.selectedDate = null;
    this.selectedShift = null;
    this.showOptimizeButton = false;
    this.showTabs = false;
    this.initialSchedule = [];
    this.optimizedSchedule = [];
    this.selectedTab = 0;
    this.hasOptimizedSchedule = false;
    this.optimizationStatistics = null;
    this.selectedEmployees = [];

    // Očisti OR-Tools sesiju
    this.currentSessionId = null;
    this.navigationInfo = null;
    this.orToolsSessionService.clearSession();

    this.drawerService.setScheduleInfo(null, null);
  }
  private showDialog(
    message: string,
    type: 'error' | 'success' | 'warning' | 'info' = 'error',
    solutionStatus?: string
  ): void {
    const dialog = this.dialogService.open({
      title: type.charAt(0).toUpperCase() + type.slice(1),
      content: MessageDialogComponent,
      width: 450,
      height: 250,
      appendTo: this.dialogContainerRef,
      cssClass: 'custom-dialog',
    });

    const dialogContent = dialog.content.instance as MessageDialogComponent;
    dialogContent.message = message;
    dialogContent.type = type;
    dialogContent.solutionStatus = solutionStatus;
  }

  reoptimize() {
    this.optimizeSchedule();
  }

  navigateToPrevious() {
    if (!this.currentSessionId) return;

    this.loading$.next(true);

    this.orToolsSessionService
      .navigatePrevious(this.currentSessionId)
      .pipe(finalize(() => this.loading$.next(false)))
      .subscribe({
        next: (response) => {
          this.processOptimizedSchedule(response);
          this.optimizationStatistics = response.statistics;
          this.slotShortages = this.formatSlotShortages(response.slotShortages);
          this.navigationInfo = response.navigationInfo;
          this.orToolsSessionService.updateNavigationInfo(
            response.navigationInfo
          );
          console.log('Navigated to previous optimization');
        },
        error: (error) => {
          console.error('Error navigating to previous:', error);
          this.showDialog(
            'Greška pri navigaciji na prethodnu optimizaciju',
            'error'
          );
        },
      });
  }

  navigateToNext() {
    if (!this.currentSessionId) return;

    this.loading$.next(true);

    this.orToolsSessionService
      .navigateNext(this.currentSessionId)
      .pipe(finalize(() => this.loading$.next(false)))
      .subscribe({
        next: (response) => {
          this.processOptimizedSchedule(response);
          this.optimizationStatistics = response.statistics;
          this.slotShortages = this.formatSlotShortages(response.slotShortages);
          this.navigationInfo = response.navigationInfo;
          this.orToolsSessionService.updateNavigationInfo(
            response.navigationInfo
          );
          console.log('Navigated to next optimization');
        },
        error: (error) => {
          console.error('Error navigating to next:', error);
          this.showDialog(
            'Greška pri navigaciji na sledeću optimizaciju',
            'error'
          );
        },
      });
  }

  switchToBestRun() {
    if (!this.currentSessionId) return;

    this.loading$.next(true);

    this.orToolsSessionService
      .getBestRun(this.currentSessionId)
      .pipe(finalize(() => this.loading$.next(false)))
      .subscribe({
        next: (response) => {
          this.processOptimizedSchedule(response);
          this.optimizationStatistics = response.statistics;
          this.slotShortages = this.formatSlotShortages(response.slotShortages);

          if (response.navigationInfo) {
            this.navigationInfo = response.navigationInfo;
            this.orToolsSessionService.updateNavigationInfo(
              response.navigationInfo
            );
          } else {
            this.orToolsSessionService
              .getNavigationInfo(this.currentSessionId!)
              .subscribe((navInfo) => {
                this.navigationInfo = navInfo;
                this.orToolsSessionService.updateNavigationInfo(navInfo);
              });
            console.log('Switched to best run:', response.runInfo);
          }
        },
        error: (error) => {
          console.error('Error switching to best run:', error);
          this.showDialog(
            'Greška pri prebacivanju na najbolju optimizaciju',
            'error'
          );
        },
      });
  }

  showOptimizationHistory() {
    if (!this.currentSessionId) return;

    this.orToolsSessionService
      .getOptimizationHistory(this.currentSessionId)
      .subscribe({
        next: (response) => {
          console.log('Optimization history:', response);

          // pronadji ID najbolje optimizacije
          const bestRun = response.history
            .filter(
              (r) =>
                r.solverStatus === 'Optimal' || r.solverStatus === 'Feasible'
            )
            .sort((a, b) => {
              // Sortiraj po success rate, pa po manjku, pa po objective
              if (b.statistics.successRate !== a.statistics.successRate) {
                return b.statistics.successRate - a.statistics.successRate;
              }
              if (
                a.statistics.slotsWithShortage !==
                b.statistics.slotsWithShortage
              ) {
                return (
                  a.statistics.slotsWithShortage -
                  b.statistics.slotsWithShortage
                );
              }
              return a.objectiveValue - b.objectiveValue;
            })[0];

          const dialog = this.dialogService.open({
            title: 'Istorija optimizacija',
            content: OptimizationHistoryDialogComponent,
            width: 1100,
            height: 750,
            appendTo: this.dialogContainerRef,
          });

          const dialogContent = dialog.content
            .instance as OptimizationHistoryDialogComponent;
          dialogContent.history = response.history;
          dialogContent.currentRunId = this.navigationInfo?.currentRunNumber;
          dialogContent.bestRunId = bestRun?.id;

          dialog.result.subscribe((result: any) => {
            if (result?.action === 'select' && result.runId) {
              this.loadOptimizationRun(result.runId);
            }
          });
        },
        error: (error) => {
          console.error('Error getting optimization history:', error);
        },
      });
  }

  private loadOptimizationRun(runId: number) {
    if (!this.currentSessionId) {
      console.error('No current session ID');
      return;
    }

    console.log('Loading optimization run:', runId);
    this.loading$.next(true);

    this.orToolsSessionService
      .loadOptimizationRun(this.currentSessionId, runId)
      .pipe(finalize(() => this.loading$.next(false)))
      .subscribe({
        next: (response) => {
          console.log('Loaded optimization run:', runId, response);

          // Procesuj raspored
          this.processOptimizedSchedule(response);
          this.optimizationStatistics = response.statistics;
          this.slotShortages = this.formatSlotShortages(response.slotShortages);

          // Azuriraj navigation info
          this.navigationInfo = response.navigationInfo;
          this.orToolsSessionService.updateNavigationInfo(
            response.navigationInfo
          );

          console.log('Successfully loaded run:', runId);
          console.log('Updated navigation info:', this.navigationInfo);
        },
        error: (error) => {
          console.error('Error loading optimization run:', error);
          this.showDialog(
            `Greška pri učitavanju optimizacije #${runId}`,
            'error'
          );
        },
      });
  }

  private generateOptimizationDescription(): string {
    const params = this.optimizationParams;
    const parts: string[] = [];

    if (this.isReoptimizing) {
      parts.push('Re-opt');
    }

    parts.push(`${params.maxExecutionTime}s`);

    if (params.stopAfterOptimalSolutions > 0) {
      parts.push(`opt:${params.stopAfterOptimalSolutions}`);
    }

    if (params.stopAfterZeroShortageSolutions > 0) {
      parts.push(`zero:${params.stopAfterZeroShortageSolutions}`);
    }

    if (params.useHeuristic) {
      parts.push('LNS');
    }

    if (this.selectedEmployees.length > 0) {
      parts.push(`${this.selectedEmployees.length} emp`);
    }

    return parts.join(', ');
  }

  get hasMultipleOptimizations(): boolean {
    return (this.navigationInfo?.totalRuns ?? 0) > 1;
  }

  get canNavigatePrevious(): boolean {
    return this.navigationInfo?.canNavigatePrevious ?? false;
  }

  get canNavigateNext(): boolean {
    return this.navigationInfo?.canNavigateNext ?? false;
  }

  get currentOptimizationInfo(): string {
    if (!this.navigationInfo) return '';
    return `${this.navigationInfo.currentRunNumber} od ${this.navigationInfo.totalRuns}`;
  }

  get currentOptimizationDescription(): string {
    return this.navigationInfo?.currentRunDescription ?? '';
  }
}
