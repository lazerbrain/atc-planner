import { Component, Input } from '@angular/core';
import { OptimizationStatistics } from 'src/app/models/optimization-response.model';

@Component({
  selector: 'app-optimization-statistics-dialog',
  templateUrl: './optimization-statistics-dialog.component.html',
  styleUrls: ['./optimization-statistics-dialog.component.css'],
})
export class OptimizationStatisticsDialogComponent {
  @Input() statistics!: OptimizationStatistics;

  showMaxWorkHourDifference: boolean = false;
  showBreakComplianceInfo: boolean = false;
  showSolutionStatusInfo: boolean = false;
  showObjectiveValueInfo: boolean = false;

  toggleMaxWorkHourDifference() {
    this.showMaxWorkHourDifference = !this.showMaxWorkHourDifference;
  }

  toggleBreakComplianceInfo() {
    this.showBreakComplianceInfo = !this.showBreakComplianceInfo;
  }

  toggleSolutionStatusInfo() {
    this.showSolutionStatusInfo = !this.showSolutionStatusInfo;
  }

  toggleObjectiveValueInfo() {
    this.showObjectiveValueInfo = !this.showObjectiveValueInfo;
  }

  getSolutionStatusExplanation(): string {
    switch (this.statistics.solutionStatus) {
      case 'Optimal':
        return 'Pronađeno je najbolje moguće rešenja za dati problem.';
      case 'Suboptimal':
        return 'Pronađeno je dobro rešenje, ali nije garantovano da je najbolje moguće.';
      case 'Feasible':
        return 'Pronađeno je rešenje koje zadovoljava sva ograničenja, ali nije nužno optimalno.';
      case 'Infeasible':
        return 'Nije pronađeno rešenje koje zadovoljava sva ograničenja.';
      case 'Time limit reached':
        return 'Optimizacija je zaustavljena pre pronalaska optimalnog rešenja zbog isteka zadatog vremena.';
      case 'Target objective achieved':
        return 'Optimizacija je zaustavljena jer je pronađeno rešenje koje zadovoljava unapred definisani cilj.';
      default:
        return 'Nepoznat status rešenja.';
    }
  }
}
