import { Component, Input } from '@angular/core';
import { DialogRef } from '@progress/kendo-angular-dialog';

@Component({
  selector: 'app-message-dialog',
  templateUrl: './message-dialog.component.html',
  styleUrls: ['./message-dialog.component.css'],
})
export class MessageDialogComponent {
  @Input() message: string = '';
  @Input() type: 'error' | 'success' | 'warning' | 'info' = 'info';
  @Input() buttonText: string = 'Zatvori';
  @Input() solutionStatus?: string;

  constructor(private dialogRef: DialogRef) {}

  getIconClass(): string {
    const icons = {
      error: 'k-i-error-circle',
      success: 'k-i-check-circle',
      warning: 'k-i-warning',
      info: 'k-i-information',
    };
    return icons[this.type];
  }

  close() {
    this.dialogRef.close();
  }
}
