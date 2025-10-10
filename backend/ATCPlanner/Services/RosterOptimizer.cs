using ATCPlanner.Data;
using ATCPlanner.Models;
using ATCPlanner.Services.Constraints;
using ATCPlanner.Utils;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace ATCPlanner.Services
{
    public class RosterOptimizer
    {
        private readonly ILogger<RosterOptimizer> _logger;
        private readonly DataTableFilter _dataTableFilter;
        private readonly DatabaseHandler _databaseHandler;
        private readonly SimulatedAnnealingOptimizer _simulatedAnnealingOptimizer;
        private readonly OrToolsOptimizer _orToolsOptimizer;
        private int _slotDurationMinutes = 30;

        public RosterOptimizer(ILogger<RosterOptimizer> logger, DataTableFilter dataTableFilter, DatabaseHandler databaseHandler, IConfiguration configuration, IEnumerable<IOrToolsConstraint> constraints)
        {
            _logger = logger;
            _dataTableFilter = dataTableFilter;
            _databaseHandler = databaseHandler;
            _simulatedAnnealingOptimizer = new SimulatedAnnealingOptimizer(logger, dataTableFilter, _slotDurationMinutes);
            _orToolsOptimizer = new OrToolsOptimizer(logger, _dataTableFilter, databaseHandler, _slotDurationMinutes, configuration, constraints);
        }

        // Metoda za postavku dužine slota
        public void SetSlotDuration(int durationMinutes)
        {
            if (durationMinutes > 0 && durationMinutes <= 60)
            {
                _slotDurationMinutes = durationMinutes;
            }
            else
            {
                _logger.LogWarning($"Invalid slot duration: {durationMinutes} minutes. Using default value of 30 minutes.");
            }
        }

        public async Task<OptimizationResponse> OptimizeRoster(string smena, DateTime datum, DataTable konfiguracije, DataTable inicijalniRaspored, List<DateTime> timeSlots, int maxExecTime,
                        int? maxOptimalSolutions, int? maxZeroShortageSlots, bool useLNS, List<string>? selectedOperativeWorkplaces, List<string>? selectedEmployees, bool useSimulatedAnnealing = false, bool useManualAssignments = true,
                        int? randomSeed = null, bool useRandomization = true)
        {
            try
            {
                _logger.LogInformation("Starting OptimizeRoster method");

                if (useSimulatedAnnealing)
                {

                    _logger.LogInformation("Using Simulated Annealing optimization");

                    return _simulatedAnnealingOptimizer.OptimizeRosterWithSimulatedAnnealing(smena, datum, konfiguracije, inicijalniRaspored, timeSlots, maxExecTime, maxOptimalSolutions, maxZeroShortageSlots,
                        useLNS, selectedOperativeWorkplaces, selectedEmployees!);
                } else
                {
                    _logger.LogInformation("Using OR-Tools optimization");

                    return await _orToolsOptimizer.OptimizeRosterWithOrTools(smena, datum, konfiguracije, inicijalniRaspored, timeSlots,maxExecTime, maxOptimalSolutions, maxZeroShortageSlots,
                       selectedOperativeWorkplaces, selectedEmployees, useManualAssignments, randomSeed, useRandomization);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in OptimizeRoster method");
                return new OptimizationResponse
                {
                    Statistics = new OptimizationStatistics
                    {
                        SolutionStatus = "Error",
                        ObjectiveValue = 0
                    }
                };
            }

        }
    }
}
