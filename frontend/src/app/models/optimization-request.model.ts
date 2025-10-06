export interface OptimizationRequest {
  smena: string;
  datum: string;
  maxExecTime: number;
  maxOptimalSolutions: number;
  maxZeroShortageSlots: number;
  useLNS: boolean;
  useSimulatedAnnealing: boolean;
  selectedOperativeWorkplaces: string[];
  selectedEmployees: string[];
  useManualAssignments: boolean;
  randomSeed?: number;
  useRandomization?: boolean;
}
