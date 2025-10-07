import { Component, Input } from '@angular/core';
import { DialogRef } from '@progress/kendo-angular-dialog';
import { OptimizationHistoryEntry } from 'src/app/models/ortools-session.model';
import {
  starIcon,
  checkIcon,
  chevronUpIcon,
  chevronDownIcon,
  questionCircleIcon,
} from '@progress/kendo-svg-icons';

@Component({
  selector: 'app-optimization-history-dialog',
  templateUrl: './optimization-history-dialog.component.html',
  styleUrls: ['./optimization-history-dialog.component.css'],
})
export class OptimizationHistoryDialogComponent {
  @Input() history: OptimizationHistoryEntry[] = [];
  @Input() currentRunId?: number;
  @Input() bestRunId?: number;

  starIcon = starIcon;
  checkIcon = checkIcon;
  chevronUpIcon = chevronUpIcon;
  chevronDownIcon = chevronDownIcon;
  questionCircleIcon = questionCircleIcon;

  showOptimizationGuide: boolean = false;

  constructor(public dialogRef: DialogRef) {}

  toggleOptimizationGuide() {
    this.showOptimizationGuide = !this.showOptimizationGuide;
  }

  onSelectRun(event: any) {
    const run = event.dataItem as OptimizationHistoryEntry;
    this.dialogRef.close({ action: 'select', runId: run.id });
  }

  onClose() {
    this.dialogRef.close({ action: 'cancel' });
  }

  isCurrentRun(run: OptimizationHistoryEntry): boolean {
    return run.id === this.currentRunId;
  }

  isBestRun(run: OptimizationHistoryEntry): boolean {
    return run.id === this.bestRunId;
  }

  getStatusClass(status: string): string {
    switch (status.toLowerCase()) {
      case 'optimal':
        return 'status-optimal';
      case 'feasible':
        return 'status-feasible';
      case 'infeasible':
        return 'status-infeasible';
      default:
        return 'status-unknown';
    }
  }

  formatDuration(seconds: number): string {
    if (seconds < 60) {
      return `${seconds}seconds`;
    }

    const minutes = Math.floor(seconds / 60);
    const remainingSeconds = seconds % 60;
    return `${minutes}m ${remainingSeconds}s`;
  }
}
