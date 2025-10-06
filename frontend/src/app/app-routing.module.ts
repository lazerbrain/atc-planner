import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { ScheduleComponent } from './features/schedule/schedule.component';
import { EmployeesComponent } from './features/employees/employees.component';

const routes: Routes = [
  // { path: '', redirectTo: '/schedule', pathMatch: 'full' },
  // { path: 'schedule', component: ScheduleComponent },
  // { path: 'employees', component: EmployeesComponent },
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule],
})
export class AppRoutingModule {}
