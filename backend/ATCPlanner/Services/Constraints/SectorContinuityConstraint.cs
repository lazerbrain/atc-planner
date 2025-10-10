using ATCPlanner.Models;
using Google.OrTools.Sat;
using System.Data;
using Microsoft.Extensions.Logging;

namespace ATCPlanner.Services.Constraints
{
    public class SectorContinuityConstraint : IOrToolsConstraint
    {
        private readonly ILogger<SectorContinuityConstraint> _logger;

        public SectorContinuityConstraint(ILogger<SectorContinuityConstraint> logger)
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
            _logger.LogInformation("Applying Sector Continuity Constraints...");

            var manualAssignmentsByController = CreateManualAssignmentsByController(inicijalniRaspored, controllers, timeSlots);

            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 1; t < timeSlots.Count; t++) // pocinjemo od drugog slota
                {
                    var controller = controllerInfo[controllers[c]];

                    // proveri da li je kl u smeni u oba slota
                    bool inShiftPrev = ConstraintUtils.IsInShift(controller, timeSlots[t - 1], t - 1, timeSlots.Count, manualAssignmentsByController, c);
                    bool inShiftCurr = ConstraintUtils.IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c);

                    if (!inShiftPrev || !inShiftCurr)
                        continue;

                    if (useManualAssignments)
                    {
                        bool hasManualPrev = HasManualAssignment(c, t - 1, manualAssignmentsByController);
                        bool hasManualCurr = HasManualAssignment(c, t, manualAssignmentsByController);

                        if (hasManualPrev && hasManualCurr)
                        {
                            string prevAssignment = manualAssignmentsByController[c][t - 1];
                            string currAssignment = manualAssignmentsByController[c][t];

                            // ako su oba manuelno dodeljena, proveri da li krse kontinuitet
                            if (prevAssignment != "break" && currAssignment != "break")
                            {
                                string prevBase = prevAssignment.Length >= 2 ? prevAssignment.Substring(0, 2) : prevAssignment;
                                string currBase = currAssignment.Length >= 2 ? currAssignment.Substring(0, 2) : currAssignment;

                                if (prevBase != currBase)
                                {
                                    _logger.LogWarning($"Manual assignments violate sector continuity for controller {controllers[c]} " +
                                             $"at slots {t - 1}-{t}: {prevAssignment} -> {currAssignment}. " +
                                             $"Skipping continuity constraint.");
                                    continue;
                                }
                            }
                        }
                    }

                    // kreiraj varijable za pracenje rada
                    var workingPrev = model.NewBoolVar($"working_prev_{c}_{t - 1}");
                    model.Add(assignments[(c, t - 1, "break")] == 0).OnlyEnforceIf(workingPrev);
                    model.Add(assignments[(c, t - 1, "break")] == 1).OnlyEnforceIf(workingPrev.Not());

                    var workingCurr = model.NewBoolVar($"working_curr_{c}_{t}");
                    model.Add(assignments[(c, t, "break")] == 0).OnlyEnforceIf(workingCurr);
                    model.Add(assignments[(c, t, "break")] == 1).OnlyEnforceIf(workingCurr.Not());

                    var workingConsecutive = model.NewBoolVar($"working_consecutive_{c}_{t}");
                    model.Add(workingPrev == 1).OnlyEnforceIf(workingConsecutive);
                    model.Add(workingCurr == 1).OnlyEnforceIf(workingConsecutive);
                    model.Add(workingPrev + workingCurr < 2).OnlyEnforceIf(workingConsecutive.Not());

                    // primeni ogranicenje kontinuiteta sektora
                    foreach (var prevSector in requiredSectors[t - 1])
                    {
                        string prevBaseSector = prevSector.Length >= 2 ? prevSector.Substring(0, 2) : prevSector;

                        foreach (var currSector in requiredSectors[t])
                        {
                            string prevCurrSector = currSector.Length >= 2 ? currSector.Substring(0, 2) : currSector;

                            if (prevBaseSector != prevCurrSector)
                            {
                                bool canApplyConstraint = true;

                                if (useManualAssignments)
                                {
                                    bool isManualPrev = IsManuallyAssignedToSector(c, t - 1, prevSector, manualAssignmentsByController);
                                    bool isManualCurr = IsManuallyAssignedToSector(c, t, currSector, manualAssignmentsByController);

                                    if (isManualPrev && isManualCurr)
                                    {
                                        canApplyConstraint = false;
                                    }
                                }

                                if (canApplyConstraint)
                                {
                                    var onPrevSector = model.NewBoolVar($"on_prev_sector_{c}_{t - 1}_{prevSector}");
                                    model.Add(assignments[(c, t - 1, prevSector)] == 1).OnlyEnforceIf(onPrevSector);
                                    model.Add(assignments[(c, t - 1, prevSector)] == 0).OnlyEnforceIf(onPrevSector.Not());

                                    var onCurrSector = model.NewBoolVar($"on_curr_sector_{c}_{t}_{currSector}");
                                    model.Add(assignments[(c, t, currSector)] == 1).OnlyEnforceIf(onCurrSector);
                                    model.Add(assignments[(c, t, currSector)] == 0).OnlyEnforceIf(onCurrSector.Not());

                                    var sectorSwitch = model.NewBoolVar($"sector_switch_{c}_{t}_{prevSector}_{currSector}");
                                    model.Add(onPrevSector == 1).OnlyEnforceIf(sectorSwitch);
                                    model.Add(onCurrSector == 1).OnlyEnforceIf(sectorSwitch);
                                    model.Add(onPrevSector + onCurrSector < 2).OnlyEnforceIf(sectorSwitch.Not());

                                    model.Add(workingConsecutive + sectorSwitch <= 1);
                                }
                            }
                        }
                    }
                }
            }
            _logger.LogInformation("Sector Continuity Constraints applied successfully.");
        }

        // Helper methods copied from OrToolsOptimizer
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

        private bool HasManualAssignment(int controllerIndex, int timeSlot, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController)
        {
            return manualAssignmentsByController.ContainsKey(controllerIndex) && manualAssignmentsByController[controllerIndex].ContainsKey(timeSlot);
        }

        private bool IsManuallyAssignedToSector(int controllerIndex, int timeSlot, string sector, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController)
        {
            if (!HasManualAssignment(controllerIndex, timeSlot, manualAssignmentsByController))
                return false;
            return manualAssignmentsByController[controllerIndex][timeSlot] == sector;
        }
    }
}
