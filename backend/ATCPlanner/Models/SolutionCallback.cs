using Google.OrTools.Sat;

namespace ATCPlanner.Models
{
    public class SolutionCallback: CpSolverSolutionCallback
    {
        private readonly Action<CpSolverResponse> _onSolution;

        public SolutionCallback(Action<CpSolverResponse> onSolution)
        {
            _onSolution = onSolution;
        }

        public override void OnSolutionCallback()
        {
            // Kreiraj novi CpSolverResponse objekat sa trenutnim vrednostima
            var response = new CpSolverResponse
            {
                ObjectiveValue = ObjectiveValue(), //
                BestObjectiveBound = BestObjectiveBound(),
                WallTime = WallTime()
            };

            _onSolution(response);
        }

        //private int _solutionsFound = 0;
        //private readonly int? _maxSolutions;
        //private readonly int? _maxZeroShortage;
        //private readonly ILogger _logger;
        //private readonly Action<int> _progressCallback;

        //private double _bestObjectiveValue = double.MaxValue;
        //private int _shortagesInSolution = -1;
        //private int _zeroShortageSolutionsFound = 0;

        //private readonly List<string> _controllers;
        //private readonly List<DateTime> _timeSlots;
        //private readonly Dictionary<int, List<string>> _requiredSectors;
        //private readonly Dictionary<(int, int, string), IntVar> _assignments;

        //public SolutionCallback(ILogger logger, int? maxSolutions, int? maxZeroShortage, Action<int> progressCallback, List<string> controllers, List<DateTime> timeSlots,
        //                    Dictionary<int, List<string>> requiredSectors, Dictionary<(int, int, string), IntVar> assignments)
        //{
        //    _maxSolutions = maxSolutions;
        //    _maxZeroShortage = maxZeroShortage;
        //    _logger = logger;
        //    _progressCallback = progressCallback;
        //    _controllers = controllers;
        //    _timeSlots = timeSlots;
        //    _requiredSectors = requiredSectors;
        //    _assignments = assignments;
        //}

        //public override void OnSolutionCallback()
        //{
        //    _solutionsFound++;

        //    double objectiveValue = ObjectiveValue();
        //    double wallTime = WallTime();

        //    _logger.LogInformation($"Solution #{_solutionsFound} found after {wallTime:F2}s with objective: {objectiveValue:F2}");

        //    if (objectiveValue < _bestObjectiveValue)
        //    {
        //        _bestObjectiveValue = objectiveValue;
        //        // Analiza manjka izvršilaca 
        //        _shortagesInSolution = CalculateShortages();

        //        _logger.LogInformation($"New best solution found: {objectiveValue} with {_shortagesInSolution} shortages. Total solutions: {_solutionsFound}");

        //        _progressCallback?.Invoke(_solutionsFound);

        //        if (_shortagesInSolution == 0)
        //        {
        //            _zeroShortageSolutionsFound++;
        //            _logger.LogInformation($"Found zero-shortage solution #{_zeroShortageSolutionsFound}");
        //        }
        //    }

        //    // Provera uslova za ranu terminaciju
        //    bool shouldStop = false;

        //    if (_maxSolutions.HasValue && _solutionsFound >= _maxSolutions.Value)
        //    {
        //        _logger.LogInformation($"Stopping search after finding {_maxSolutions.Value} solutions");
        //        shouldStop = true;
        //    }

        //    if (_maxZeroShortage.HasValue && _zeroShortageSolutionsFound >= _maxZeroShortage.Value)
        //    {
        //        _logger.LogInformation($"Stopping search after finding {_maxZeroShortage.Value} zero-shortage solutions");
        //        shouldStop = true;
        //    }

        //    if (shouldStop)
        //    {
        //        StopSearch();
        //    }
        //}

        //private int CalculateShortages()
        //{
        //    // Brojač ukupnih manjkova sektora
        //    int totalShortages = 0;

        //    // Za svaki vremenski slot
        //    for (int t = 0; t < _timeSlots.Count; t++)
        //    {
        //        // Za svaki sektor potreban u ovom vremenskom slotu
        //        foreach (var sector in _requiredSectors[t])
        //        {
        //            bool sectorCovered = false;

        //            // Proveravamo da li neki kontrolor radi na ovom sektoru
        //            for (int c = 0; c < _controllers.Count; c++)
        //            {
        //                // Ključ za varijablu dodele
        //                var key = (c, t, sector);

        //                // Ako postoji dodela i vrednost rešenja je 1, sektor je pokriven
        //                if (_assignments.ContainsKey(key) && Value(_assignments[key]) > 0.5)
        //                {
        //                    sectorCovered = true;
        //                    break;
        //                }
        //            }

        //            // Ako sektor nije pokriven, povećavamo brojač manjkova
        //            if (!sectorCovered)
        //            {
        //                totalShortages++;
        //            }
        //        }
        //    }

        //    return totalShortages;
        //}

        //public int SolutionsFound => _solutionsFound;
        //public int ZeroShortageSolutionsFound => _zeroShortageSolutionsFound;
        //public double BestObjectiveValue => _bestObjectiveValue;
    }
}
