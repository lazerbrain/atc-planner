using ATCPlanner.Models;
using Google.OrTools.Sat;
using System.Data;

namespace ATCPlanner.Services.Constraints
{
    public class BaseConstraint : IOrToolsConstraint
    {
        private readonly ILogger<BaseConstraint> _logger;

        public BaseConstraint(ILogger<BaseConstraint> logger)
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
            _logger.LogInformation("Applying Base Constraints...");

            // 1. Svaki sektor ima najviše jednog kontrolora
            for (int t = 0; t < timeSlots.Count; t++)
            {
                foreach (var sector in requiredSectors[t])
                {
                    var sectorAssignments = new List<IntVar>();
                    for (int c = 0; c < controllers.Count; c++)
                    {
                        sectorAssignments.Add(assignments[(c, t, sector)]);
                    }
                    model.Add(LinearExpr.Sum(sectorAssignments) <= 1);
                }
            }

            // 2. Svaki kontrolor je dodeljen tačno jednom sektoru ili pauzi
            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 0; t < timeSlots.Count; t++)
                {
                    var controller = controllerInfo[controllers[c]];
                    bool inShift = ConstraintUtils.IsInShift(controller, timeSlots[t], t, timeSlots.Count);

                    if (!inShift)
                    {
                        model.Add(assignments[(c, t, "break")] == 1);
                        foreach (var sector in requiredSectors[t])
                        {
                            model.Add(assignments[(c, t, sector)] == 0);
                        }
                    }
                    else
                    {
                        bool isFlagS = ConstraintUtils.IsFlagS(controllers[c], timeSlots[t], inicijalniRaspored);
                        if (isFlagS)
                        {
                            model.Add(assignments[(c, t, "break")] == 1);
                            foreach (var sector in requiredSectors[t])
                            {
                                model.Add(assignments[(c, t, sector)] == 0);
                            }
                        }
                        else
                        {
                            var taskVars = new List<IntVar> { assignments[(c, t, "break")] };
                            taskVars.AddRange(requiredSectors[t].Select(s => assignments[(c, t, s)]));
                            model.Add(LinearExpr.Sum(taskVars) == 1);
                        }
                    }
                }
            }
            _logger.LogInformation("Base Constraints applied successfully.");
        }
    }
}
