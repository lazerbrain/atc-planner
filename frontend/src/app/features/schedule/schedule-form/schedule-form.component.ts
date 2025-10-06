import { Component, EventEmitter, OnInit, Output } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { IntlService } from '@progress/kendo-angular-intl';

interface ShiftOption {
  text: string;
  value: string;
}

@Component({
  selector: 'app-schedule-form',
  templateUrl: './schedule-form.component.html',
  styleUrls: ['./schedule-form.component.css'],
})
export class ScheduleFormComponent implements OnInit {
  @Output() formSubmit = new EventEmitter<{ date: Date; shift: string }>();
  scheduleForm!: FormGroup;
  shifts: ShiftOption[] = [
    { text: 'J (Jutro)', value: 'J' },
    { text: 'P (Podne)', value: 'P' },
    { text: 'N (Noć)', value: 'N' },
  ];

  constructor(private fb: FormBuilder, private intl: IntlService) {}

  ngOnInit() {
    this.scheduleForm = this.fb.group({
      date: [null, Validators.required],
      shift: [null, Validators.required],
    });
  }

  onSubmit() {
    if (this.scheduleForm.valid) {
      const formValue = this.scheduleForm.value;
      this.formSubmit.emit({
        date: formValue.date,
        shift: formValue.shift.value,
      });
    }
  }

  compareShifts(item: ShiftOption, value: ShiftOption): boolean {
    return item && value && item.value === value.value;
  }
}
