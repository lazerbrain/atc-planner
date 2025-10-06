export interface OptimizationResponse {
  optimizedResults: OptimizedResult[];
  nonOptimizedResults: OptimizedResult[];
  initialAssignments: InitialAssignment[];
  configurationLabels: { [key: string]: string };
  slotShortages: { [key: string]: number };
  statistics: OptimizationStatistics;
}

export interface OptimizedResult {
  sifra: string;
  prezimeIme: string;
  smena: string;
  datum: string;
  datumOd: string;
  datumDo: string;
  sektor: string;
  orm: string;
  flag: string;
}

export interface InitialAssignment {
  sifra: string;
  smena: string;
  flag: string | null;
  datumOd: string;
  datumDo: string;
}

export interface OptimizationStatistics {
  successRate: number;
  employeesWithShortage: number;
  slotsWithShortage: number;
  slotsWithExcess: number;
  breakCompliance: number;
  rotationCompliance: number;
  maxWorkHourDifference: number;
  solutionStatus: string;
  objectiveValue: number;
  missingExecutors: number;
  formattedSuccessRate: string;
  formattedBreakCompliance: string;
  formattedRotationCompliance: string;
  formattedMaxWorkHourDifference: string;
  formattedObjectiveValue: string;
  formattedWallTime: number;
}
