using ATCPlanner.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace ATCPlanner.Services.Constraints
{
    public static class ConstraintUtils
    {
        public static bool IsInShift(ControllerInfo controller, DateTime slotTime, int slotIndex, int totalSlots, Dictionary<int, Dictionary<int, string>>? manualAssignmentsByController = null, int? controllerIndex = null)
        {
            bool inShift = slotTime >= controller.ShiftStart && slotTime < controller.ShiftEnd;

            if (inShift && controller.ShiftType == "M" && slotIndex >= totalSlots - 2)
            {
                if (manualAssignmentsByController != null && controllerIndex.HasValue && manualAssignmentsByController.ContainsKey(controllerIndex.Value) && manualAssignmentsByController[controllerIndex.Value].ContainsKey(slotIndex))
                {
                    string manualSector = manualAssignmentsByController[controllerIndex.Value][slotIndex];
                    if (!string.IsNullOrEmpty(manualSector) && manualSector != "break")
                    {
                        return true;
                    }
                }
                return false;
            }

            return inShift;
        }

        public static bool IsFlagS(string controllerCode, DateTime timeSlot, DataTable inicijalniRaspored)
        {
            var controllerShifts = inicijalniRaspored.AsEnumerable().Where(row => row.Field<string>("sifra") == controllerCode).ToList();
            foreach (var shift in controllerShifts)
            {
                DateTime shiftStart = shift.Field<DateTime>("datumOd");
                DateTime shiftEnd = shift.Field<DateTime>("datumDo");
                string flag = shift.Field<string>("Flag")!;
                if (flag == "S" && timeSlot >= shiftStart && timeSlot < shiftEnd)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
