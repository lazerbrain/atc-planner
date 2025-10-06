import { Component, Input } from '@angular/core';
import { DialogContentBase, DialogRef } from '@progress/kendo-angular-dialog';
import { OptimizationParams } from 'src/app/models/optimization-params.model';

export interface OptimizationDialogResult {
  action: 'optimize' | 'cancel';
  params?: OptimizationParams;
}

@Component({
  selector: 'app-optimization-dialog',
  templateUrl: './optimization-dialog.component.html',
  styleUrls: ['./optimization-dialog.component.css'],
})
export class OptimizationDialogComponent extends DialogContentBase {
  @Input() params!: OptimizationParams;

  constructor(public override dialog: DialogRef) {
    super(dialog);
  }

  onSubmit() {
    this.dialog.close({
      action: 'optimize',
      params: this.params,
    } as OptimizationDialogResult);
  }

  onCancel() {
    this.dialog.close({ action: 'cancel' } as OptimizationDialogResult);
  }

  onMaxExecutionTimeChange(value: number) {
    this.params.maxExecutionTime = value;
  }

  onStopAfterOptimalSolutionsChange(value: number) {
    this.params.stopAfterOptimalSolutions = value;
  }

  onStopAfterZeroShortageSolutionsChange(value: number) {
    this.params.stopAfterZeroShortageSolutions = value;
  }
}
