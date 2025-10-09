using ATCPlanner.Models;
using Google.OrTools.Sat;
using System.Data;
using Microsoft.Extensions.Logging;

namespace ATCPlanner.Services.Constraints
{
    public class GuaranteedWorkConstraint : IOrToolsConstraint
    {
        private readonly ILogger<GuaranteedWorkConstraint> _logger;

        public GuaranteedWorkConstraint(ILogger<GuaranteedWorkConstraint> logger)
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
            _logger.LogInformation("Applying Guaranteed Work Constraints...");

            var ssControllers = new List<int>();
            var supControllers = new List<int>();
            var regularControllers = new List<int>();

            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];
                if (controller.IsShiftLeader) ssControllers.Add(c);
                else if (controller.IsSupervisor) supControllers.Add(c);
                else regularControllers.Add(c);
            }

            // PRAVILO 1: SVI kontrolori MORAJU raditi bar minimum slotova
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];
                var workSlots = new List<IntVar>();

                for (int t = 0; t < timeSlots.Count; t++)
                {
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count)) continue;

                    var isWorking = model.NewBoolVar($"is_working_{c}_{t}");
                    var sectorVars = requiredSectors[t].Select(sector => assignments[(c, t, sector)]).ToList();

                    if (sectorVars.Any())
                    {
                        model.Add(LinearExpr.Sum(sectorVars) >= 1).OnlyEnforceIf(isWorking);
                        model.Add(LinearExpr.Sum(sectorVars) == 0).OnlyEnforceIf(isWorking.Not());
                        workSlots.Add(isWorking);
                    }
                }

                if (workSlots.Any())
                {
                    int minWork = Math.Max(1, workSlots.Count / 4);
                    model.Add(LinearExpr.Sum(workSlots) >= minWork);
                }
            }

            var regularWorkloadVars = new List<IntVar>();
            var ssWorkloadVars = new List<IntVar>();
            var supWorkloadVars = new List<IntVar>();

            // Kreiraj varijable za ukupno radno vreme
            foreach (int c in regularControllers)
            {
                var controller = controllerInfo[controllers[c]];
                var workSlots = new List<IntVar>();
                for (int t = 0; t < timeSlots.Count; t++)
                {
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count)) continue;
                    workSlots.AddRange(requiredSectors[t].Select(sector => assignments[(c, t, sector)]));
                }
                if (workSlots.Any())
                {
                    var totalWork = model.NewIntVar(0, workSlots.Count, $"regular_work_{c}");
                    model.Add(totalWork == LinearExpr.Sum(workSlots));
                    regularWorkloadVars.Add(totalWork);
                }
            }

            // Similar loops for ssControllers and supControllers...

            _logger.LogInformation("Guaranteed Work Constraints applied successfully.");
        }

        private bool IsInShift(ControllerInfo controller, DateTime slotTime, int slotIndex, int totalSlots)
        {
            bool inShift = slotTime >= controller.ShiftStart && slotTime < controller.ShiftEnd;
            if (inShift && controller.ShiftType == "M" && slotIndex >= totalSlots - 2)
            {
                inShift = false;
            }
            return inShift;
        }
    }
}
