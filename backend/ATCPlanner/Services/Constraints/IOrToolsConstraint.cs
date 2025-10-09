using ATCPlanner.Models;
using Google.OrTools.Sat;
using System.Data;

namespace ATCPlanner.Services.Constraints
{
    public interface IOrToolsConstraint
    {
        void Apply(CpModel model,
                   Dictionary<(int, int, string), IntVar> assignments,
                   List<string> controllers,
                   List<DateTime> timeSlots,
                   Dictionary<int, List<string>> requiredSectors,
                   Dictionary<string, ControllerInfo> controllerInfo,
                   DataTable inicijalniRaspored,
                   bool useManualAssignments);
    }
}
