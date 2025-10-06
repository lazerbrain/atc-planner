import { OptimizationRequest } from './optimization-request.model';
import {
  InitialAssignment,
  OptimizationStatistics,
  OptimizedResult,
} from './optimization-response.model';

export interface OrToolsNavigationInfo {
  canNavigatePrevious: boolean;
  canNavigateNext: boolean;
  currentRunNumber: number;
  totalRuns: number;
  currentRunDescription: string;
  currentRunTimestamp: string;
  solverStatus: string;
  objectiveValue: number;
  successRate: number;
  slotsWithShortage: number;
}

export interface CreateSessionRequest {
  smena: string;
  datum: string;
}

export interface OptimizeWithSessionRequest {
  sessionId: string;
  optimizationRequest: OptimizationRequest;
  description?: string;
}

export interface OptimizationSessionResponse {
  optimizedResults: OptimizedResult[];
  nonOptimizedResults: OptimizedResult[];
  allResults: OptimizedResult[];
  initialAssignments: InitialAssignment[];
  configurationLabels: { [key: string]: string };
  slotShortages: { [key: string]: number };
  statistics: OptimizationStatistics;
  sessionId: string;
  navigationInfo: OrToolsNavigationInfo;
}

export interface OptimizationHistoryEntry {
  id: number;
  createdAt: string;
  description: string;
  solverStatus: string;
  objectiveValue: number;
  solvingTime: number;
  statistics: {
    successRate: number;
    slotsWithShortage: number;
    slotsWithExcess: number;
    formattedSuccessRate: string;
    solutionStatus: string;
  };
}

export interface OptimizationHistoryResponse {
  sessionId: string;
  history: OptimizationHistoryEntry[];
}

export interface BestRunResponse {
  optimizedResults: OptimizedResult[];
  nonOptimizedResults: OptimizedResult[];
  allResults: OptimizedResult[];
  initialAssignments: InitialAssignment[];
  configurationLabels: { [key: string]: string };
  slotShortages: { [key: string]: number };
  statistics: OptimizationStatistics;
  sessionId: string;
  navigationInfo?: OrToolsNavigationInfo;
  runInfo: {
    id: number;
    createdAt: string;
    description: string;
    solverStatus: string;
    objectiveValue: number;
  };
}
