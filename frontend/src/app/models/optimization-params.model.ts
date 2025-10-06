export interface OptimizationParams {
  maxExecutionTime: number;
  useHeuristic: boolean;
  useSimulatedAnnealing: boolean;
  stopAfterOptimalSolutions: number;
  stopAfterZeroShortageSolutions: number;
  useManualAssignments: boolean;
  randomSeed?: number | null;
  useRandomization?: boolean;
}
