using ATCPlanner.Models;
using Google.OrTools.Sat;
using System.Data;
using Microsoft.Extensions.Logging;

namespace ATCPlanner.Services.Constraints
{
    public class SupervisorShiftLeaderConstraint : IOrToolsConstraint
    {
        private readonly ILogger<SupervisorShiftLeaderConstraint> _logger;

        public SupervisorShiftLeaderConstraint(ILogger<SupervisorShiftLeaderConstraint> logger)
        {
            _logger = logger;
        }

        public void Apply(CpModel model,
                           Dictionary<(int, int, string), IntVar> assignments,
                           List<string> controllers,
                           List<DateTime> timeSlots,
                           Dictionary<int, List<string>> requiredSectors,
                           Dictionary<string, ControllerInfo> controllerInfo,
                           DataTable inicijalniRaspored,
                           bool useManualAssignments)
        {
            _logger.LogInformation("Applying Supervisor/Shift Leader Constraints...");

            var manualAssignmentsByController = CreateManualAssignmentsByController(inicijalniRaspored, controllers, timeSlots);

            var ssControllers = new List<int>();
            var supControllers = new List<int>();

            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];
                if (controller.IsShiftLeader) ssControllers.Add(c);
                else if (controller.IsSupervisor) supControllers.Add(c);
            }

            if (!ssControllers.Any() && !supControllers.Any())
            {
                _logger.LogInformation("No Supervisors or Shift Leaders found, skipping constraint.");
                return;
            }

            for (int t = 0; t < timeSlots.Count; t++)
            {
                var ssWorkingVars = new List<IntVar>();
                var supWorkingVars = new List<IntVar>();

                foreach (int ssC in ssControllers)
                {
                    if (!ConstraintUtils.IsInShift(controllerInfo[controllers[ssC]], timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, ssC)) continue;
                    var ssIsWorking = model.NewBoolVar($"ss_{ssC}_working_{t}");
                    var sectorVars = requiredSectors[t].Select(sector => assignments[(ssC, t, sector)]).ToList();
                    if (sectorVars.Any())
                    {
                        model.Add(LinearExpr.Sum(sectorVars) >= 1).OnlyEnforceIf(ssIsWorking);
                        model.Add(LinearExpr.Sum(sectorVars) == 0).OnlyEnforceIf(ssIsWorking.Not());
                        ssWorkingVars.Add(ssIsWorking);
                    }
                }

                foreach (int supC in supControllers)
                {
                    if (!ConstraintUtils.IsInShift(controllerInfo[controllers[supC]], timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, supC)) continue;
                    var supIsWorking = model.NewBoolVar($"sup_{supC}_working_{t}");
                    var sectorVars = requiredSectors[t].Select(sector => assignments[(supC, t, sector)]).ToList();
                    if (sectorVars.Any())
                    {
                        model.Add(LinearExpr.Sum(sectorVars) >= 1).OnlyEnforceIf(supIsWorking);
                        model.Add(LinearExpr.Sum(sectorVars) == 0).OnlyEnforceIf(supIsWorking.Not());
                        supWorkingVars.Add(supIsWorking);
                    }
                }

                if (ssWorkingVars.Any() && supWorkingVars.Any())
                {
                    var allSpecialControllers = new List<IntVar>();
                    allSpecialControllers.AddRange(ssWorkingVars);
                    allSpecialControllers.AddRange(supWorkingVars);
                    model.Add(LinearExpr.Sum(allSpecialControllers) <= 1);
                }
                else if (ssWorkingVars.Any())
                {
                    model.Add(LinearExpr.Sum(ssWorkingVars) <= 1);
                }
                else if (supWorkingVars.Any())
                {
                    model.Add(LinearExpr.Sum(supWorkingVars) <= 1);
                }
            }
            _logger.LogInformation("Supervisor/Shift Leader Constraints applied successfully.");
        }

        // Helper methods
        private Dictionary<int, Dictionary<int, string>> CreateManualAssignmentsByController(DataTable inicijalniRaspored, List<string> controllers, List<DateTime> timeSlots)
        {
            var manualAssignmentsByController = new Dictionary<int, Dictionary<int, string>>();
            var manualAssignments = IdentifyManualAssignments(inicijalniRaspored, controllers, timeSlots);
            foreach (var (controllerCode, timeSlotIndex, sector) in manualAssignments)
            {
                int controllerIndex = controllers.IndexOf(controllerCode);
                if (controllerIndex < 0) continue;
                if (!manualAssignmentsByController.ContainsKey(controllerIndex))
                {
                    manualAssignmentsByController[controllerIndex] = new Dictionary<int, string>();
                }
                manualAssignmentsByController[controllerIndex][timeSlotIndex] = sector;
            }
            return manualAssignmentsByController;
        }

        private List<(string controllerCode, int timeSlotIndex, string sector)> IdentifyManualAssignments(DataTable inicijalniRaspored, List<string> controllers, List<DateTime> timeSlots)
        {
            var manualAssignments = new List<(string controllerCode, int timeSlotIndex, string sector)>();
            foreach (DataRow row in inicijalniRaspored.Rows)
            {
                string controllerCode = row.Field<string>("sifra");
                string sector = row.Field<string>("sektor");
                if (!string.IsNullOrEmpty(sector))
                {
                    DateTime datumOd = row.Field<DateTime>("datumOd");
                    int timeSlotIndex = timeSlots.FindIndex(ts => ts == datumOd);
                    if (timeSlotIndex >= 0)
                    {
                        manualAssignments.Add((controllerCode, timeSlotIndex, sector));
                    }
                }
            }
            return manualAssignments;
        }
    }
}
