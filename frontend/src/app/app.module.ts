import { NgModule } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';
import { APP_BASE_HREF, CommonModule } from '@angular/common';
import { FormsModule, ReactiveFormsModule } from '@angular/forms';
import { HttpClientModule } from '@angular/common/http';

import { AppRoutingModule } from './app-routing.module';
import { AppComponent } from './app.component';
import { DateInputsModule } from '@progress/kendo-angular-dateinputs';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { DropDownsModule } from '@progress/kendo-angular-dropdowns';
import { GridModule } from '@progress/kendo-angular-grid';
import { LayoutModule, TabStripModule } from '@progress/kendo-angular-layout';
import { NavigationModule } from '@progress/kendo-angular-navigation';
import { IndicatorsModule } from '@progress/kendo-angular-indicators';
import { IconsModule } from '@progress/kendo-angular-icons';
import { IntlModule } from '@progress/kendo-angular-intl';
import { DialogModule } from '@progress/kendo-angular-dialog';
import { InputsModule } from '@progress/kendo-angular-inputs';

import { ScheduleComponent } from './features/schedule/schedule.component';
import { ButtonsModule } from '@progress/kendo-angular-buttons';
import { calendarIcon, userIcon } from '@progress/kendo-svg-icons';
import { EmployeesComponent } from './features/employees/employees.component';
import { RouterModule } from '@angular/router';
import { ScheduleFormComponent } from './features/schedule/schedule-form/schedule-form.component';
import { ScheduleGridComponent } from './features/schedule/schedule-grid/schedule-grid.component';
import { ProgressBarModule } from '@progress/kendo-angular-progressbar';
import { OptimizationDialogComponent } from './features/schedule/optimization-dialog/optimization-dialog.component';
import { OptimizationStatisticsDialogComponent } from './features/schedule/optimization-statistics-dialog/optimization-statistics-dialog.component';
import { ScheduleLegendComponent } from './features/schedule/schedule-legend/schedule-legend.component';
import { DrawerService } from './shared/services/drawer.service';
import { MessageDialogComponent } from './shared/components/message-dialog/message-dialog.component';

export const drawerRoutes = [
  {
    path: '',
    component: ScheduleComponent,
    text: 'Raspored',
    svgIcon: calendarIcon,
  },
  {
    path: 'employees',
    component: EmployeesComponent,
    text: 'Zaposleni',
    svgIcon: userIcon,
  },
];

@NgModule({
  declarations: [
    AppComponent,
    ScheduleComponent,
    ScheduleFormComponent,
    ScheduleGridComponent,
    OptimizationDialogComponent,
    OptimizationStatisticsDialogComponent,
    ScheduleLegendComponent,
    MessageDialogComponent,
  ],
  imports: [
    BrowserModule,
    CommonModule,
    AppRoutingModule,
    ReactiveFormsModule,
    HttpClientModule,
    DateInputsModule,
    BrowserAnimationsModule,
    DropDownsModule,
    GridModule,
    LayoutModule,
    NavigationModule,
    IndicatorsModule,
    IconsModule,
    IntlModule,
    ButtonsModule,
    TabStripModule,
    DialogModule,
    InputsModule,
    FormsModule,
    ProgressBarModule,
    RouterModule.forRoot(drawerRoutes),
    ProgressBarModule,
  ],
  providers: [
    {
      provide: APP_BASE_HREF,
      useValue: window.location.pathname,
    },
    DrawerService,
  ],
  bootstrap: [AppComponent],
})
export class AppModule {}
