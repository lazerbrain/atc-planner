using ATCPlanner.Models;
using Google.OrTools.Sat;
using System.Data;
using Microsoft.Extensions.Logging;

namespace ATCPlanner.Services.Constraints
{
    public class BreakAndWorkDurationConstraint : IOrToolsConstraint
    {
        private readonly ILogger<BreakAndWorkDurationConstraint> _logger;

        public BreakAndWorkDurationConstraint(ILogger<BreakAndWorkDurationConstraint> logger)
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
            _logger.LogInformation("Applying Break and Work Duration Constraints...");

            var manualAssignmentsByController = CreateManualAssignmentsByController(inicijalniRaspored, controllers, timeSlots);

            ApplyMaximumWorkingConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController, useManualAssignments);
            ApplyBreakConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController, useManualAssignments);
            ApplyMinimumWorkBlockConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController);

            _logger.LogInformation("Break and Work Duration Constraints applied successfully.");
        }

        private void ApplyMaximumWorkingConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
         Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController,
        bool useManualAssignments)
        {
            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 0; t <= timeSlots.Count - 4; t++)
                {
                    var controller = controllerInfo[controllers[c]];

                    bool allInShift = true;
                    for (int i = 0; i < 4; i++)
                    {
                        if (!ConstraintUtils.IsInShift(controller, timeSlots[t + i], t + i, timeSlots.Count, manualAssignmentsByController, c))
                        {
                            allInShift = false;
                            break;
                        }
                    }

                    if (!allInShift)
                        continue;

                    var workVars = new List<IntVar>();
                    for (int i = 0; i < 4; i++)
                    {
                        var sectorVars = new List<IntVar>();
                        foreach (var sector in requiredSectors[t + i])
                        {
                            sectorVars.Add(assignments[(c, t + i, sector)]);
                        }

                        var isWorking = model.NewBoolVar($"is_working_{c}_{t + i}");
                        if (sectorVars.Count != 0)
                        {
                            model.Add(LinearExpr.Sum(sectorVars) <= 1);
                            model.Add(LinearExpr.Sum(sectorVars) == isWorking);
                        }
                        else
                        {
                            model.Add(isWorking == 0);
                        }
                        workVars.Add(isWorking);
                    }

                    if (t + 4 < timeSlots.Count)
                    {
                        var allWorking = model.NewBoolVar($"all_working_{c}_{t}");
                        model.Add(LinearExpr.Sum(workVars) >= 4).OnlyEnforceIf(allWorking);
                        model.Add(LinearExpr.Sum(workVars) < 4).OnlyEnforceIf(allWorking.Not());

                        if (t + 4 < timeSlots.Count)
                        {
                            model.Add(assignments[(c, t + 4, "break")] == 1).OnlyEnforceIf(allWorking);
                        }
                    }
                }
            }
        }

        private void ApplyBreakConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
        Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController,
       bool useManualAssignments)
        {
            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 0; t < timeSlots.Count - 1; t++)
                {
                    var controller = controllerInfo[controllers[c]];

                    bool inShiftCurrent = ConstraintUtils.IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c);
                    bool inShiftNext = t + 1 < timeSlots.Count && ConstraintUtils.IsInShift(controller, timeSlots[t + 1], t + 1, timeSlots.Count, manualAssignmentsByController, c);

                    if (!inShiftCurrent || !inShiftNext)
                        continue;

                    var workingAtT = model.NewBoolVar($"working_at_{c}_{t}");
                    model.Add(assignments[(c, t, "break")] == 0).OnlyEnforceIf(workingAtT);
                    model.Add(assignments[(c, t, "break")] == 1).OnlyEnforceIf(workingAtT.Not());

                    var pauseAtTPlus1 = model.NewBoolVar($"pause_at_{c}_{t + 1}");
                    model.Add(assignments[(c, t + 1, "break")] == 1).OnlyEnforceIf(pauseAtTPlus1);
                    model.Add(assignments[(c, t + 1, "break")] == 0).OnlyEnforceIf(pauseAtTPlus1.Not());

                    var transitionToPause = model.NewBoolVar($"transition_to_pause_{c}_{t}");
                    model.Add(workingAtT + pauseAtTPlus1 == 2).OnlyEnforceIf(transitionToPause);
                    model.Add(workingAtT + pauseAtTPlus1 < 2).OnlyEnforceIf(transitionToPause.Not());

                    if (t >= 3)
                    {
                        bool allInShift = true;
                        for (int i = 1; i <= 3; i++)
                        {
                            if (!ConstraintUtils.IsInShift(controller, timeSlots[t - i], t - i, timeSlots.Count, manualAssignmentsByController, c))
                            {
                                allInShift = false;
                                break;
                            }
                        }

                        if (allInShift)
                        {
                            var previousWorkVars = new List<IntVar>();
                            for (int i = 1; i <= 3; i++)
                            {
                                var prevWorkingVar = model.NewBoolVar($"prev_working_{c}_{t - i}");
                                model.Add(assignments[(c, t - i, "break")] == 0).OnlyEnforceIf(prevWorkingVar);
                                model.Add(assignments[(c, t - i, "break")] == 1).OnlyEnforceIf(prevWorkingVar.Not());
                                previousWorkVars.Add(prevWorkingVar);
                            }

                            var worked3PrevSlots = model.NewBoolVar($"worked_3_prev_slots_{c}_{t}");
                            model.Add(LinearExpr.Sum(previousWorkVars) == 3).OnlyEnforceIf(worked3PrevSlots);
                            model.Add(LinearExpr.Sum(previousWorkVars) < 3).OnlyEnforceIf(worked3PrevSlots.Not());

                            var longWorkBlock = model.NewBoolVar($"long_work_block_{c}_{t}");
                            model.Add(worked3PrevSlots + workingAtT + pauseAtTPlus1 == 3).OnlyEnforceIf(longWorkBlock);
                            model.Add(worked3PrevSlots + workingAtT + pauseAtTPlus1 < 3).OnlyEnforceIf(longWorkBlock.Not());

                            if (t + 2 < timeSlots.Count && ConstraintUtils.IsInShift(controller, timeSlots[t + 2], t + 2, timeSlots.Count, manualAssignmentsByController, c))
                            {
                                model.Add(assignments[(c, t + 2, "break")] == 1).OnlyEnforceIf(longWorkBlock);
                            }
                        }
                    }
                }
            }

            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 0; t <= timeSlots.Count - 4; t++)
                {
                    var controller = controllerInfo[controllers[c]];
                    bool allInShift = true;
                    for (int i = 0; i < 4; i++)
                    {
                        if (!ConstraintUtils.IsInShift(controller, timeSlots[t + i], t + i, timeSlots.Count, manualAssignmentsByController, c))
                        {
                            allInShift = false;
                            break;
                        }
                    }
                    if (!allInShift) continue;

                    var workVars = new List<IntVar>();
                    for (int i = 0; i < 4; i++)
                    {
                        var isWorkingVar = model.NewBoolVar($"is_working_4_block_{c}_{t + i}");
                        model.Add(assignments[(c, t + i, "break")] == 0).OnlyEnforceIf(isWorkingVar);
                        model.Add(assignments[(c, t + i, "break")] == 1).OnlyEnforceIf(isWorkingVar.Not());
                        workVars.Add(isWorkingVar);
                    }

                    var works4Slots = model.NewBoolVar($"works_4_slots_{c}_{t}");
                    model.Add(LinearExpr.Sum(workVars) == 4).OnlyEnforceIf(works4Slots);
                    model.Add(LinearExpr.Sum(workVars) < 4).OnlyEnforceIf(works4Slots.Not());

                    if (t + 5 < timeSlots.Count && ConstraintUtils.IsInShift(controller, timeSlots[t + 4], t + 4, timeSlots.Count, manualAssignmentsByController, c) && ConstraintUtils.IsInShift(controller, timeSlots[t + 5], t + 5, timeSlots.Count, manualAssignmentsByController, c))
                    {
                        model.Add(assignments[(c, t + 4, "break")] + assignments[(c, t + 5, "break")] >= 2).OnlyEnforceIf(works4Slots);
                    }
                    else if (t + 4 < timeSlots.Count && ConstraintUtils.IsInShift(controller, timeSlots[t + 4], t + 4, timeSlots.Count, manualAssignmentsByController, c))
                    {
                        model.Add(assignments[(c, t + 4, "break")] == 1).OnlyEnforceIf(works4Slots);
                    }
                }
            }
        }

        private void ApplyMinimumWorkBlockConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
            Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController)
        {
            const int MIN_WORK_BLOCK = 1;

            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                for (int t = 0; t < timeSlots.Count - 1; t++)
                {
                    if (!ConstraintUtils.IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c) || !ConstraintUtils.IsInShift(controller, timeSlots[t + 1], t + 1, timeSlots.Count, manualAssignmentsByController, c))
                        continue;

                    var onBreakT = assignments[(c, t, "break")];
                    var workingT1 = model.NewBoolVar($"working_{c}_{t + 1}");
                    var sectorVarsT1 = requiredSectors[t + 1].Select(sector => assignments[(c, t + 1, sector)]).ToList();

                    if (sectorVarsT1.Any())
                    {
                        model.Add(LinearExpr.Sum(sectorVarsT1) >= 1).OnlyEnforceIf(workingT1);
                        model.Add(LinearExpr.Sum(sectorVarsT1) == 0).OnlyEnforceIf(workingT1.Not());
                    }
                    else
                    {
                        model.Add(workingT1 == 0);
                    }

                    var startingWork = model.NewBoolVar($"starting_work_{c}_{t + 1}");
                    model.Add(onBreakT + workingT1 >= 2).OnlyEnforceIf(startingWork);
                    model.Add(onBreakT + workingT1 <= 1).OnlyEnforceIf(startingWork.Not());

                    bool canEnforceMinBlock = true;
                    for (int len = 0; len < MIN_WORK_BLOCK && t + 1 + len < timeSlots.Count; len++)
                    {
                        if (!ConstraintUtils.IsInShift(controller, timeSlots[t + 1 + len], t + 1 + len, timeSlots.Count, manualAssignmentsByController, c) || GetManualAssignment(c, t + 1 + len, manualAssignmentsByController) == "break")
                        {
                            canEnforceMinBlock = false;
                            break;
                        }
                    }

                    if (canEnforceMinBlock)
                    {
                        for (int len = 0; len < MIN_WORK_BLOCK && t + 1 + len < timeSlots.Count; len++)
                        {
                            var futureBreak = assignments[(c, t + 1 + len, "break")];
                            model.Add(futureBreak == 0).OnlyEnforceIf(startingWork);
                        }
                    }
                }
            }
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

        private string? GetManualAssignment(int controllerIndex, int timeSlot, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController)
        {
            if (manualAssignmentsByController.ContainsKey(controllerIndex) && manualAssignmentsByController[controllerIndex].ContainsKey(timeSlot))
            {
                return manualAssignmentsByController[controllerIndex][timeSlot];
            }
            return null;
        }
    }
}
