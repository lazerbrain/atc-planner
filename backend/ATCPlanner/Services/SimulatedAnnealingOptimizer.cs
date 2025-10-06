using ATCPlanner.Models;
using ATCPlanner.Utils;
using Microsoft.AspNetCore.Mvc;
using System.Data;
using System.Globalization;
using System.Numerics;
using System.Runtime;
using System.Security.Cryptography.X509Certificates;

namespace ATCPlanner.Services
{
    public class SimulatedAnnealingOptimizer(ILogger logger, DataTableFilter dataTableFilter, int SlotDurationMinutes = 30)
    {
        private readonly ILogger _logger = logger;
        private readonly DataTableFilter _dataTableFilter = dataTableFilter;
        private readonly int _slotDurationMinutes = SlotDurationMinutes;

        private double _initialTemperature = 1000.0;
        private double _coolingRate = 0.95;
        private int _maxIterations = 10000;
        private int _maxIterationsAtTemperature = 100;


        // parametri za algoritam
        private Random _random = new Random();
        private List<string>? _controllers;
        private List<DateTime>? _timeSlots;
        private DataTable? _configurations;
        private DataTable? _initialSchedule;

        // struktura resenja za simulirano kaljenje
        public class Solution
        {
            public string[,,] Assignments { get; set; } // [controller, timeSlot, position]
            public int NumControllers { get; private set; }
            public int NumTimeSlots { get; private set; }

            public Solution(int numControllers, int numTimeSlots)
            {
                NumControllers = numControllers;
                NumTimeSlots = numTimeSlots;
                Assignments = new string[numControllers, numTimeSlots, 1]; // Koristimo treću dimenziju veličine 1 za jednostavniji pristup

                // Inicijalizacija svih polja kao pauze
                for (int c = 0; c < numControllers; c++)
                {
                    for (int t = 0; t < numTimeSlots; t++)
                    {
                        Assignments[c, t, 0] = "111"; // Oznaka za pauzu
                    }
                }
            }

            public Solution Clone()
            {
                Solution clone = new Solution(NumControllers, NumTimeSlots);

                for (int c = 0; c < NumControllers; c++)
                {
                    for (int t = 0; t < NumTimeSlots; t++)
                    {
                        clone.Assignments[c, t, 0] = this.Assignments[c, t, 0];
                    }
                }

                return clone;
            }

        }

        private List<SectorConfiguration> LoadSectorConfigurations(DataTable konfiguracije, List<DateTime> timeSlots)
        {
            var configurations = new List<SectorConfiguration>();

            // Prvo logirajmo sve sektore iz konfiguracije za debugiranje
            var allSectors = konfiguracije.AsEnumerable()
                .Select(row => row.Field<string>("sektor"))
                .Distinct()
                .ToList();

            _logger.LogInformation($"Total unique sectors in configuration: {string.Join(", ", allSectors)}");

            foreach (var timeSlot in timeSlots)
            {
                // Pronađi jedinstvene TX konfiguracije za ovaj vremenski slot
                var txConfigs = konfiguracije.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= timeSlot &&
                        row.Field<DateTime>("datumDo") > timeSlot &&
                        row.Field<string>("ConfigType") == "TX")
                    .Select(row => row.Field<string>("Konfiguracija"))
                    .Distinct()
                    .ToList();

                // Pronađi jedinstvene LU konfiguracije za ovaj vremenski slot
                var luConfigs = konfiguracije.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= timeSlot &&
                        row.Field<DateTime>("datumDo") > timeSlot &&
                        row.Field<string>("ConfigType") == "LU")
                    .Select(row => row.Field<string>("Konfiguracija"))
                    .Distinct()
                    .ToList();

                // Logirajmo sve konfiguracije za ovaj vremenski slot
                _logger.LogDebug($"Time slot {timeSlot}: TX configs={string.Join(",", txConfigs)}, LU configs={string.Join(",", luConfigs)}");

                // Dodaj TX konfiguracije
                foreach (var configCode in txConfigs)
                {
                    var sectors = GetSectorsForConfiguration(configCode!, "TX", konfiguracije, timeSlot);
                    _logger.LogDebug($"TX Config {configCode} has sectors: {string.Join(", ", sectors)}");

                    if (sectors.Any())
                    {
                        configurations.Add(new SectorConfiguration
                        {
                            ConfigCode = configCode!,
                            Start = timeSlot,
                            End = timeSlot.AddMinutes(_slotDurationMinutes),
                            Type = "TX",
                            Sectors = sectors
                        });
                    }
                }

                // Dodaj LU konfiguracije
                foreach (var configCode in luConfigs)
                {
                    var sectors = GetSectorsForConfiguration(configCode!, "LU", konfiguracije, timeSlot);
                    _logger.LogDebug($"LU Config {configCode} has sectors: {string.Join(", ", sectors)}");

                    if (sectors.Any())
                    {
                        configurations.Add(new SectorConfiguration
                        {
                            ConfigCode = configCode!,
                            Start = timeSlot,
                            End = timeSlot.AddMinutes(_slotDurationMinutes),
                            Type = "LU",
                            Sectors = sectors
                        });
                    }
                }
            }

            return configurations;
        }

        private List<string> GetSectorsForConfiguration(string configCode, string configType, DataTable konfiguracije, DateTime timeSlot)
        {
            // Ovde bi moglo biti problema - proverimo logikom
            var configRows = konfiguracije.AsEnumerable()
                .Where(row =>
                    row.Field<DateTime>("datumOd") <= timeSlot &&
                    row.Field<DateTime>("datumDo") > timeSlot &&
                    row.Field<string>("ConfigType") == configType &&
                    row.Field<string>("Konfiguracija") == configCode)
                .ToList();

            _logger.LogDebug($"Found {configRows.Count} rows for config {configCode} type {configType} at time {timeSlot}");

            var sectors = configRows
                .Select(row => row.Field<string>("sektor"))
                .Where(s => !string.IsNullOrEmpty(s)) // Proveravamo da li je sektor prazan
                .ToList();

            return sectors;
        }

        public OptimizationResponse OptimizeRosterWithSimulatedAnnealing(string smena, DateTime datum, DataTable konfiguracije, DataTable inicijalniRaspored, List<DateTime> timeSlots,
    int maxExecTime, int? maxOptimalSolution, int? maxZeroShortageSlots, bool useLNS, List<string>? selectedOperativeWorkplaces, List<string> selectedEmployees)
        {
            try
            {
                _logger.LogInformation("Starting SimulatedAnnealing optimization");

                TimeUtils.ConvertDatesToDateTime(konfiguracije, "datumOd", "datumDo");
                TimeUtils.ConvertDatesToDateTime(inicijalniRaspored, "datumOd", "datumDo", "VremeStart");

                // Ispiši početak smene za kontrolore
                foreach (var row in inicijalniRaspored.AsEnumerable())
                {
                    string sifra = row.Field<string>("sifra");
                    DateTime vremeStart = row.Field<DateTime>("VremeStart");
                    string smenaType = row.Field<string>("smena");
                    _logger.LogInformation($"Controller {sifra}: Shift {smenaType} starts at {vremeStart}");
                }

                // Inicijalizacija parametara
                _timeSlots = timeSlots;
                _configurations = konfiguracije;
                _initialSchedule = inicijalniRaspored;

                // Filtriranje podataka
                if (selectedOperativeWorkplaces != null && selectedOperativeWorkplaces.Count > 0)
                {
                    inicijalniRaspored = _dataTableFilter.FilterBySelectedOperationWorkplaces(inicijalniRaspored, selectedOperativeWorkplaces);
                    _logger.LogInformation("Filtered initial schedule by workplaces count: {Count}", inicijalniRaspored.Rows.Count);
                }

                if (selectedEmployees != null && selectedEmployees.Count > 0)
                {
                    inicijalniRaspored = _dataTableFilter.FilterBySelectedEmployees(inicijalniRaspored, selectedEmployees);
                    _logger.LogInformation("Filtered initial schedule by employees count: {Count}", inicijalniRaspored.Rows.Count);
                }

                // Dobijanje liste kontrolora
                _controllers = selectedEmployees != null && selectedEmployees.Count > 0 ? selectedEmployees : inicijalniRaspored.AsEnumerable().Select(row => row.Field<string>("sifra")).Distinct().ToList();

                _logger.LogInformation("Number of controllers: {Count}", _controllers.Count);

                // Učitaj konfiguracije i sektore za vremenski period
                List<SectorConfiguration> configurations = LoadSectorConfigurations(konfiguracije, timeSlots);

                // DODATNA PROVERA: Ispiši sve konfiguracije
                foreach (var config in configurations)
                {
                    _logger.LogInformation($"Configuration {config.ConfigCode} ({config.Type}) at time {config.Start}: sectors={string.Join(", ", config.Sectors)}");
                }

                // Kreiranje i optimizacija rešenja korišćenjem simuliranog kaljenja
                Solution initialSolution = this.CreateInitialSolution(_controllers, _timeSlots, konfiguracije, inicijalniRaspored);

                // Dodatna provera inicijalnog rešenja
                CheckSolutionForVremeStartViolations(initialSolution, inicijalniRaspored);

                Solution optimizedSolution = this.RunSimulatedAnnealing(initialSolution, configurations, maxExecTime);

                // Proveri kontinuitet sektora
                if (!CheckSectorContinuityForAllControllers(optimizedSolution))
                {
                    _logger.LogWarning("Detected sector continuity violations in optimized solution. Fixing...");
                    FixSectorContinuityViolations(optimizedSolution);
                }

                // Dodatna provera optimizovanog rešenja
                CheckSolutionForVremeStartViolations(optimizedSolution, inicijalniRaspored);

                // Dodatna provera da vidimo da li su svi kontrolori raspoređeni
                int unassigned = this.CheckUnassignedControllers(optimizedSolution);
                if (unassigned > 0)
                {
                    _logger.LogWarning($"After optimization, found {unassigned} unassigned controllers. Fixing...");
                    EnsureAllControllersAssigned(optimizedSolution);
                }

                // Primeni pravila o pauzama za konačno rešenje
                EnforceBreakRules(optimizedSolution);

                // Ponovo primenimo pravila o pauzama nakon maksimizacije
                EnforceBreakRules(optimizedSolution);

                // Dodajemo maksimizaciju iskorišćenosti kontrolora između Flag="S" perioda
                _logger.LogInformation("Maximizing controller utilization between Flag=S periods...");
                MaximizeUtilizationBetweenFlagSPeriods(optimizedSolution);

                // Ponovo proveri kontinuitet sektora nakon maksimizacije
                if (!CheckSectorContinuityForAllControllers(optimizedSolution))
                {
                    _logger.LogWarning("Detected sector continuity violations after maximization. Fixing...");
                    FixSectorContinuityViolations(optimizedSolution);
                }

                // Finalna provera da se ne krši VremeStart
                bool anyViolations = CheckSolutionForVremeStartViolations(optimizedSolution, inicijalniRaspored);
                if (anyViolations)
                {
                    _logger.LogWarning("Found VremeStart violations in final solution. Fixing...");
                    FixVremeStartViolations(optimizedSolution, inicijalniRaspored);
                }

                ApplyFlagSRules(optimizedSolution);

                bool flagSRulesOk = TestFlagSRules(optimizedSolution);
                if (!flagSRulesOk)
                {
                    _logger.LogError("Final solution violates Flag S rules! Applying fix...");
                    ApplyFlagSRules(optimizedSolution);
                }

                return this.ConvertToResponse(optimizedSolution, _controllers, timeSlots, inicijalniRaspored, datum);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in SimulatedAnnealing optimization");
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

        private bool CheckSolutionForVremeStartViolations(Solution solution, DataTable inicijalniRaspored)
        {
            bool hasViolations = false;

            // Za svakog kontrolora
            for (int c = 0; c < solution.NumControllers; c++)
            {
                // Dobavi informacije o smeni
                var shift = inicijalniRaspored.AsEnumerable()
                    .FirstOrDefault(row => row.Field<string>("sifra") == _controllers![c]);

                if (shift == null) continue;

                DateTime vremeStart = shift.Field<DateTime>("VremeStart");
                string smenaType = shift.Field<string>("smena")!;

                // Proveri sve slotove
                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    DateTime slotTime = _timeSlots[t];
                    string assignment = solution.Assignments[c, t, 0];

                    // Ako kontrolor radi
                    if (assignment != "111")
                    {
                        // Proveri VremeStart
                        if (slotTime < vremeStart)
                        {
                            _logger.LogWarning($"VremeStart violation: Controller {c} ({_controllers![c]}) working at {slotTime} but shift starts at {vremeStart}");
                            hasViolations = true;
                        }

                        // Proveri M smenu u poslednjem satu
                        if (smenaType == "M" && t >= solution.NumTimeSlots - 2)
                        {
                            _logger.LogWarning($"M shift violation: Controller {c} ({_controllers![c]}) with M shift working in last hour at slot {t}");
                            hasViolations = true;
                        }
                    }
                }
            }

            return hasViolations;
        }

        private void FixVremeStartViolations(Solution solution, DataTable inicijalniRaspored)
        {
            // Za svaki vremenski slot
            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                DateTime slotTime = _timeSlots![t];

                // Za svakog kontrolora u ovom slotu
                for (int c = 0; c < solution.NumControllers; c++)
                {
                    // Ako kontrolor radi
                    if (solution.Assignments[c, t, 0] != "111")
                    {
                        // Dobavi informacije o smeni
                        var shift = inicijalniRaspored.AsEnumerable()
                            .FirstOrDefault(row => row.Field<string>("sifra") == _controllers![c]);

                        if (shift == null) continue;

                        DateTime vremeStart = shift.Field<DateTime>("VremeStart");
                        string smenaType = shift.Field<string>("smena")!;

                        // Proveri VremeStart
                        if (slotTime < vremeStart || (smenaType == "M" && t >= solution.NumTimeSlots - 2))
                        {
                            string sector = solution.Assignments[c, t, 0];
                            _logger.LogInformation($"Fixing VremeStart violation: Controller {c} ({_controllers[c]}) removed from sector {sector} at slot {t} (time {slotTime})");

                            // Postavi na pauzu
                            solution.Assignments[c, t, 0] = "111";

                            // Pokušaj naći drugog kontrolora koji bi mogao pokrivati ovaj sektor
                            TryReassignSector(solution, inicijalniRaspored, sector, t);
                        }
                    }
                }
            }
        }

        private void TryReassignSector(Solution solution, DataTable inicijalniRaspored, string sector, int timeSlot)
        {
            DateTime slotTime = _timeSlots[timeSlot];

            // Pronađi kontrolore koji su u smeni i slobodni
            List<int> availableControllers = new List<int>();

            for (int c = 0; c < solution.NumControllers; c++)
            {
                // Ako je kontrolor slobodan
                if (solution.Assignments[c, timeSlot, 0] == "111")
                {
                    // Proveri da li je u smeni
                    var shift = inicijalniRaspored.AsEnumerable()
                        .FirstOrDefault(row => row.Field<string>("sifra") == _controllers[c]);

                    if (shift != null)
                    {
                        DateTime vremeStart = shift.Field<DateTime>("VremeStart");
                        string smenaType = shift.Field<string>("smena");

                        if (slotTime >= vremeStart && (smenaType != "M" || timeSlot < solution.NumTimeSlots - 2))
                        {
                            availableControllers.Add(c);
                        }
                    }
                }
            }

            // Ako ima dostupnih kontrolora, izaberi jednog i dodeli mu sektor
            if (availableControllers.Any())
            {
                int selectedController = availableControllers[_random.Next(availableControllers.Count)];
                solution.Assignments[selectedController, timeSlot, 0] = sector;
                _logger.LogInformation($"Reassigned sector {sector} at slot {timeSlot} to controller {selectedController} ({_controllers[selectedController]})");
            }
            else
            {
                _logger.LogWarning($"Could not find available controller for sector {sector} at slot {timeSlot}");
            }
        }

        private Solution CreateInitialSolution(List<string> controllers, List<DateTime> timeSlots, DataTable konfiguracije, DataTable inicijalniRaspored)
        {
            Solution solution = new Solution(controllers.Count, timeSlots.Count);
            Random rand = new Random();

            _logger.LogInformation($"Creating initial solution for shift with {timeSlots.Count} slots");

            // Dobavi informacije o vremenima početka smene za sve kontrolore
            var controllerStartTimes = new Dictionary<int, DateTime>();
            var controllerShiftTypes = new Dictionary<int, string>();

            for (int c = 0; c < controllers.Count; c++)
            {
                var shift = inicijalniRaspored.AsEnumerable()
                    .FirstOrDefault(row => row.Field<string>("sifra") == controllers[c]);

                if (shift != null)
                {
                    // VRLO VAŽNO: Koristimo VremeStart za početak smene kontrolora
                    controllerStartTimes[c] = shift.Field<DateTime>("VremeStart")!;
                    controllerShiftTypes[c] = shift.Field<string>("smena")!;
                    _logger.LogDebug($"Controller {controllers[c]} starts at {controllerStartTimes[c]} with shift type {controllerShiftTypes[c]}");
                }
                else
                {
                    _logger.LogWarning($"No shift data found for controller {controllers[c]}");
                    // Postavi podrazumevane vrednosti
                    controllerStartTimes[c] = timeSlots.FirstOrDefault();
                    controllerShiftTypes[c] = "J";
                }
            }

            // Za svaki vremenski slot
            for (int t = 0; t < timeSlots.Count; t++)
            {
                DateTime slotTime = timeSlots[t];

                // KLJUČNA IZMENA: Direktno dobavi sektore iz konfiguracije za ovaj slot
                var validSectors = konfiguracije.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= slotTime &&
                        row.Field<DateTime>("datumDo") > slotTime)
                    .Select(row => row.Field<string>("sektor"))
                    .Distinct()
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                _logger.LogDebug($"Valid sectors for slot {t} at time {slotTime}: {string.Join(", ", validSectors)}");

                if (!validSectors.Any())
                {
                    _logger.LogWarning($"No valid sectors found for slot {t} at time {slotTime}");
                    continue;
                }

                // Hash set da pratimo koji sektori su već dodeljeni u ovom slotu
                var assignedSectorsInSlot = new HashSet<string>();

                var availableControllers = new List<int>();
                for (int c = 0; c < controllers.Count; c++)
                {
                    // STROŽIJA PROVERA: Kontrolor mora biti u svojoj smeni
                    DateTime controllerStart = controllerStartTimes[c];
                    string smena = controllerShiftTypes[c];

                    // NOVA PROVERA: Proveri da li kontrolor ima Flag="S" za ovaj slot
                    bool isFlagS = IsFlagS(controllers[c], slotTime, inicijalniRaspored);

                    // KLJUČNA IZMENA: Strožija provera da li je kontrolor u smeni, uključujući i Flag="S" proveru
                    bool isInShift = slotTime >= controllerStart; // Mora biti nakon početka smene
                    bool isNotLastHourM = smena != "M" || t < timeSlots.Count - 2; // M smena ne radi poslednji sat
                    bool isFree = solution.Assignments[c, t, 0] == "111" || string.IsNullOrEmpty(solution.Assignments[c, t, 0]); // Mora biti slobodan
                    bool isNotFlagS = !isFlagS; // Ne sme imati Flag="S" za ovaj slot

                    if (isInShift && isNotLastHourM && isFree && isNotFlagS)
                    {
                        availableControllers.Add(c);
                    }
                    else if (isFlagS)
                    {
                        _logger.LogDebug($"Controller {controllers[c]} has Flag=S at slot {t} (time {slotTime})");
                    }
                    else if (!isInShift && isFree)
                    {
                        _logger.LogDebug($"Controller {controllers[c]} not available at slot {t} (time {slotTime}) - shift starts at {controllerStart}");
                    }
                }

                // Izmešaj listu kontrolora za slučajnost
                availableControllers = availableControllers.OrderBy(x => rand.Next()).ToList();

                foreach (var sector in validSectors)
                {
                    // Proveri da li je sektor već dodeljen
                    if (assignedSectorsInSlot.Contains(sector))
                    {
                        _logger.LogDebug($"Sector {sector} already assigned in slot {t}, skipping");
                        continue;
                    }

                    if (!availableControllers.Any())
                    {
                        _logger.LogDebug($"No available controllers for sector {sector} at slot {t}");
                        break; // Dozvoljavamo nepokriven sektor ako nema kontrolora
                    }

                    int controller = availableControllers.First();
                    availableControllers.RemoveAt(0);

                    // Dodaj sektor u hash set dodeljenih sektora
                    assignedSectorsInSlot.Add(sector);

                    // Dodeli kontrolora na sektor
                    solution.Assignments[controller, t, 0] = sector;
                    _logger.LogDebug($"Assigned controller {controller} ({controllers[controller]}) to sector {sector} at slot {t} (time {slotTime})");
                }
            }

            ApplyFlagSRules(solution);
            return solution;
        }

        public Solution RunSimulatedAnnealing(Solution initialSolution, List<SectorConfiguration> configurations, int maxExecTime)
        {
            // Prvo popravi početno rešenje da bude validno
            if (!IsValidSolution(initialSolution))
            {
                _logger.LogWarning("Initial solution is not valid. Fixing...");
                FixSectorRepetitions(initialSolution);
            }

            ApplyFlagSRules(initialSolution);

            Solution currentSolution = initialSolution.Clone();
            Solution bestSolution = initialSolution.Clone();

            double currentEnergy = CalculateEnergy(currentSolution, configurations);
            double bestEnergy = currentEnergy;
            double previousBestEnergy = bestEnergy; // Inicijalizuj pre petlje

            double temperature = _initialTemperature;
            DateTime startTime = DateTime.Now;

            _logger.LogInformation("Starting Simulated Annealing with initial energy: {Energy}", currentEnergy);

            for (int iter = 0; iter < _maxIterations; iter++)
            {
                // Proveri vremensko ograničenje
                if ((DateTime.Now - startTime).TotalSeconds > maxExecTime)
                {
                    _logger.LogInformation("Stopping due to time limit after {Iterations} iterations", iter);
                    break;
                }

                for (int innerIter = 0; innerIter < _maxIterationsAtTemperature; innerIter++)
                {
                    Solution neighborSolution = GenerateNeighborSolution(currentSolution, configurations);

                    // Dodatna provera da li generisana komšijska solucija ima ponavljanja sektora
                    if (!IsValidSolution(neighborSolution))
                    {
                        FixSectorRepetitions(neighborSolution);
                    }

                    double neighborEnergy = CalculateEnergy(neighborSolution, configurations);

                    double deltaE = neighborEnergy - currentEnergy;

                    if (deltaE < 0 || _random.NextDouble() < Math.Exp(-deltaE / temperature))
                    {
                        currentSolution = neighborSolution;
                        currentEnergy = neighborEnergy;

                        if (currentEnergy < bestEnergy)
                        {
                            bestSolution = currentSolution.Clone();
                            bestEnergy = currentEnergy;
                            _logger.LogInformation("New best solution found with energy: {Energy} at iteration {Iteration}", bestEnergy, iter);
                        }
                    }
                }

                // Provera za raniji prekid zbog konvergencije
                if (iter > 100 && Math.Abs(bestEnergy - previousBestEnergy) < 0.01)
                {
                    _logger.LogInformation("Early stopping due to convergence at iteration {Iteration}", iter);
                    break;
                }
                previousBestEnergy = bestEnergy; // Ažuriraj prethodnu energiju

                // Smanji temperaturu
                temperature *= _coolingRate;

                // Povremeno loguj napredak
                if (iter % 100 == 0)
                {
                    _logger.LogInformation("Iteration {Iteration}: Current temperature = {Temperature}, Best energy = {Energy}", iter, temperature, bestEnergy);
                }

                if (temperature < 0.01)
                {
                    _logger.LogInformation("Stopping due to low temperature after {Iterations} iterations", iter);
                    break;
                }
            }

            if (!IsValidSolution(bestSolution))
            {
                FixSectorRepetitions(bestSolution);
                _logger.LogWarning("Fixed duplications in final solution");
            }

            _logger.LogInformation("Simulated Annealing completed. Final best energy: {Energy}", bestEnergy);
            return bestSolution;
        }

        private double CalculateEnergy(Solution solution, List<SectorConfiguration> configurations)
        {
            double energy = 0.0;

            const double SECTOR_REPETITION_PENALTY = 1000000.0;
            const double SECTOR_COVERAGE_PENALTY = 10000.0;
            const double SECTOR_CHANGE_PENALTY = 100000.0;
            const double SHORT_BLOCK_PENALTY = 1000.0;
            const double WORK_BREAK_PENALTY = 100.0;
            const double STABILITY_BONUS = -50.0;
            const double OUT_OF_SHIFT_PENALTY = 1000000.0;
            const double VREMME_START_PENALTY = 1000000.0;
            const double SS_SUP_SAME_SLOT_PENALTY = 500000.0;
            const double SS_WORK_PENALTY = 100.0;
            const double FLAG_S_PENALTY = 1000000.0; // Novi penal za Flag="S"
            const double UTILIZATION_BETWEEN_FLAG_S_BONUS = -2000.0; // veći bonus za iskorišćenost između Flag="S" perioda
            const int MIN_WORK_BLOCK = 2;
            const int MAX_WORK_NO_BREAK = 4;

            var workload = new int[solution.NumControllers];
            var lastSector = new string[solution.NumControllers];
            var blockLength = new int[solution.NumControllers];

            for (int c = 0; c < solution.NumControllers; c++) lastSector[c] = "";

            var controllerStartTimes = new Dictionary<int, DateTime>();
            var controllerShiftTypes = new Dictionary<int, string>();
            for (int c = 0; c < solution.NumControllers; c++)
            {
                var shift = _initialSchedule.AsEnumerable()
                    .FirstOrDefault(row => row.Field<string>("sifra") == _controllers[c]);

                if (shift != null)
                {
                    controllerStartTimes[c] = shift.Field<DateTime>("VremeStart");
                    controllerShiftTypes[c] = shift.Field<string>("smena")!;
                }
                else
                {
                    controllerStartTimes[c] = _timeSlots.FirstOrDefault();
                    controllerShiftTypes[c] = "J";
                }
            }

            var (ssControllers, supControllers) = IdentifySpecialControllers(_controllers!, _initialSchedule!);

            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                DateTime slotTime = _timeSlots[t];
                var requiredSectors = _configurations.AsEnumerable()
                    .Where(row => row.Field<DateTime>("datumOd") <= slotTime && row.Field<DateTime>("datumDo") > slotTime)
                    .Select(row => row.Field<string>("sektor"))
                    .Distinct()
                    .ToList();

                var coveredSectors = new HashSet<string>();

                // Provera da li SS i SUP rade istovremeno
                bool ssWorking = ssControllers.Any(ssIndex => solution.Assignments[ssIndex, t, 0] != "111");
                bool supWorking = supControllers.Any(supIndex => solution.Assignments[supIndex, t, 0] != "111");

                if (ssWorking && supWorking)
                {
                    energy += SS_SUP_SAME_SLOT_PENALTY;
                    _logger.LogDebug($"Penalty: SS and SUP working in the same slot {t}");
                }

                for (int c = 0; c < solution.NumControllers; c++)
                {
                    string assignment = solution.Assignments[c, t, 0];
                    DateTime shiftStart = controllerStartTimes[c];
                    string smena = controllerShiftTypes[c];

                    // NOVA PROVERA: Flag="S" za kontrolora u ovom slotu
                    bool isFlagS = IsFlagS(_controllers[c], slotTime, _initialSchedule);

                    if (assignment != "111")
                    {
                        // Izvlačimo osnovni sektor (bez E/P oznake)
                        string baseSector = assignment.Length > 1 ? assignment.Substring(0, assignment.Length - 1) : assignment;
                        string position = assignment.Length > 1 ? assignment[assignment.Length - 1].ToString() : "";

                        coveredSectors.Add(assignment);
                        workload[c]++;
                        blockLength[c]++;

                        // Kazna za promenu osnovnog sektora, ne i pozicije
                        if (lastSector[c] != "" && blockLength[c] > 1)
                        {
                            string lastBaseSector = lastSector[c].Length > 1 ?
                                                   lastSector[c].Substring(0, lastSector[c].Length - 1) :
                                                   lastSector[c];

                            if (baseSector != lastBaseSector)
                            {
                                energy += SECTOR_CHANGE_PENALTY;
                            }
                        }

                        lastSector[c] = assignment;

                        // DODATO: Kazna za kontrolore sa Flag="S" koji rade
                        if (isFlagS)
                        {
                            energy += FLAG_S_PENALTY;
                            _logger.LogDebug($"Flag S penalty: Controller {c} ({_controllers[c]}) working at {slotTime} with Flag=S");
                        }

                        // DODATO: Strožija kazna za kontrolore koji rade pre VremeStart
                        if (slotTime < shiftStart)
                        {
                            energy += VREMME_START_PENALTY;
                            _logger.LogDebug($"VremeStart penalty: Controller {c} ({_controllers[c]}) working at {slotTime} but shift starts at {shiftStart}");
                        }

                        // Kazna za M smenu u poslednjem satu
                        if (smena == "M" && t >= solution.NumTimeSlots - 2)
                        {
                            energy += OUT_OF_SHIFT_PENALTY;
                            _logger.LogDebug($"M shift penalty: Controller {c} ({_controllers[c]}) with M shift working in last hour");
                        }

                        // Dodaj blagi penal ako SS radi
                        if (ssControllers.Contains(c))
                        {
                            energy += SS_WORK_PENALTY;
                        }

                        coveredSectors.Add(assignment);
                        workload[c]++;
                        blockLength[c]++;
                        if (assignment != lastSector[c] && lastSector[c] != "" && blockLength[c] > 1)
                        {
                            energy += SECTOR_CHANGE_PENALTY;
                        }
                        lastSector[c] = assignment;
                        if (blockLength[c] > MAX_WORK_NO_BREAK) energy += WORK_BREAK_PENALTY;
                    }
                    else
                    {
                        if (blockLength[c] > 0 && blockLength[c] < MIN_WORK_BLOCK) energy += SHORT_BLOCK_PENALTY;
                        if (blockLength[c] >= MIN_WORK_BLOCK) energy += STABILITY_BONUS * (blockLength[c] / MIN_WORK_BLOCK);
                        blockLength[c] = 0;
                        lastSector[c] = "";
                    }
                }

                int uncovered = requiredSectors.Except(coveredSectors).Count();
                energy += SECTOR_COVERAGE_PENALTY * uncovered;

                // DODATNO: Kazna za svaki sektor koji se ponavlja u istom slotu
                int duplicates = coveredSectors.Count - coveredSectors.Select(s => s.ToUpper()).Distinct().Count();
                energy += SECTOR_REPETITION_PENALTY * duplicates;
            }

            double meanWorkload = workload.Average();
            double variance = workload.Sum(w => Math.Pow(w - meanWorkload, 2)) / solution.NumControllers;
            energy += Math.Sqrt(variance);

            // Računaj bonus za iskorišćenost između Flag="S" perioda
            for (int c = 0; c < solution.NumControllers; c++)
            {
                string controllerId = _controllers[c];

                // Pronađi periode kada kontrolor ima flag="S"
                List<(int startSlot, int endSlot)> flagSPeriods = FindFlagSPeriods(controllerId);

                // Fokusiraj se na periode između Flag="S"
                if (flagSPeriods.Count > 1)
                {
                    for (int i = 0; i < flagSPeriods.Count - 1; i++)
                    {
                        int periodStart = flagSPeriods[i].endSlot;
                        int periodEnd = flagSPeriods[i + 1].startSlot;
                        int periodLength = periodEnd - periodStart;

                        if (periodLength > 0)
                        {
                            // Izračunaj koliko je kontrolor angažovan u ovom periodu
                            int workingSlots = 0;
                            for (int t = periodStart; t < periodEnd; t++)
                            {
                                if (solution.Assignments[c, t, 0] != "111")
                                {
                                    workingSlots++;
                                }
                            }

                            // Dodaj bonus proporcionalan procentu iskorišćenosti 
                            double utilizationPercentage = (double)workingSlots / periodLength;
                            energy += UTILIZATION_BETWEEN_FLAG_S_BONUS * utilizationPercentage;
                        }
                    }
                }
            }

            return energy;
        }

        private void FillUncoveredSectors(Solution solution, List<SectorConfiguration> configurations)
        {
            Random rand = new Random();

            var controllerStartTimes = new Dictionary<int, DateTime>();
            var controllerShiftTypes = new Dictionary<int, string>();

            for (int c = 0; c < solution.NumControllers; c++)
            {
                var shift = _initialSchedule.AsEnumerable()
                    .FirstOrDefault(row => row.Field<string>("sifra") == _controllers[c]);

                if (shift != null)
                {
                    // VAŽNO: Koristimo VremeStart umesto datumOd
                    controllerStartTimes[c] = shift.Field<DateTime>("VremeStart");
                    controllerShiftTypes[c] = shift.Field<string>("smena")!;
                    _logger.LogDebug($"Controller {_controllers[c]} starts at {controllerStartTimes[c]} with shift type {controllerShiftTypes[c]}");
                }
                else
                {
                    // Postavi podrazumevane vrednosti
                    controllerStartTimes[c] = _timeSlots.FirstOrDefault();
                    controllerShiftTypes[c] = "J";
                }
            }

            var (ssControllers, supControllers) = IdentifySpecialControllers(_controllers, _initialSchedule);

            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                DateTime slotTime = _timeSlots[t];

                // Dobavi validne sektore za ovaj slot
                var validSectors = _configurations.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= slotTime &&
                        row.Field<DateTime>("datumDo") > slotTime)
                    .Select(row => row.Field<string>("sektor"))
                    .Distinct()
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                if (!validSectors.Any()) continue;

                var coveredSectors = new HashSet<string>();

                // Identifikuj sektore koji su već pokriveni
                for (int c = 0; c < solution.NumControllers; c++)
                {
                    if (solution.Assignments[c, t, 0] != "111")
                    {
                        coveredSectors.Add(solution.Assignments[c, t, 0]);
                    }
                }

                // Pronađi nepokrivene sektore - samo među validnim sektorima
                var uncoveredSectors = validSectors.Except(coveredSectors).ToList();

                if (uncoveredSectors.Any())
                {
                    _logger.LogDebug($"Uncovered sectors in slot {t}: {string.Join(", ", uncoveredSectors)}");

                    // Prvo pokušaj sa običnim kontrolorima
                    var availableControllers = new List<int>();
                    for (int c = 0; c < solution.NumControllers; c++)
                    {
                        if (!ssControllers.Contains(c) && !supControllers.Contains(c)) // Samo obični kontrolori
                        {
                            // VAŽNO: Strožija provera VremeStart
                            DateTime shiftStart = controllerStartTimes[c];
                            string smena = controllerShiftTypes[c];

                            // Kontrolor je dostupan samo ako:
                            // 1. Trenutno nije raspoređen (ima pauzu)
                            // 2. Vreme slota je nakon početka njegove smene
                            // 3. Nije M smena u poslednjem satu
                            if (solution.Assignments[c, t, 0] == "111" &&
                                slotTime >= shiftStart &&
                                (smena != "M" || t < solution.NumTimeSlots - 2))
                            {
                                availableControllers.Add(c);
                            }
                        }
                    }

                    // Ako nema običnih kontrolora, pokušaj sa SS (ako SUP ne radi)
                    bool supWorkingInSlot = supControllers.Any(sup => solution.Assignments[sup, t, 0] != "111");
                    if (!availableControllers.Any() && !supWorkingInSlot)
                    {
                        for (int c = 0; c < solution.NumControllers; c++)
                        {
                            if (ssControllers.Contains(c))
                            {
                                // VAŽNO: Strožija provera VremeStart
                                DateTime shiftStart = controllerStartTimes[c];
                                string smena = controllerShiftTypes[c];

                                if (solution.Assignments[c, t, 0] == "111" &&
                                    slotTime >= shiftStart &&
                                    (smena != "M" || t < solution.NumTimeSlots - 2))
                                {
                                    availableControllers.Add(c);
                                }
                            }
                        }
                    }

                    // Izmešaj kontrolore za slučajnost
                    availableControllers = availableControllers.OrderBy(x => rand.Next()).ToList();

                    foreach (var sector in uncoveredSectors)
                    {
                        if (!availableControllers.Any())
                        {
                            _logger.LogDebug($"No available controllers for uncovered sector {sector} at slot {t}");
                            break;
                        }

                        int controller = availableControllers.First();
                        availableControllers.RemoveAt(0);

                        // Dodeli samo jedan slot
                        solution.Assignments[controller, t, 0] = sector!;
                        _logger.LogDebug($"Filled: Assigned controller {controller} ({_controllers[controller]}) to sector {sector} at slot {t} (time {slotTime})");
                    }
                }
            }
        }

        // Funkcija za dobavljanje nepokrivenih sektora u određenom vremenskom slotu
        private List<string> GetUncoveredSectors(Solution solution, int timeSlot)
        {
            DateTime slotTime = _timeSlots[timeSlot];

            // Dobavi sve sektore koji treba da budu pokriveni u ovom slotu
            var requiredSectors = _configurations.AsEnumerable()
                .Where(row =>
                    row.Field<DateTime>("datumOd") <= slotTime &&
                    row.Field<DateTime>("datumDo") > slotTime)
                .Select(row => row.Field<string>("sektor"))
                .Distinct()
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            // Pronađi koji sektori su već pokriveni
            var coveredSectors = new HashSet<string>();
            for (int c = 0; c < solution.NumControllers; c++)
            {
                string assignment = solution.Assignments[c, timeSlot, 0];
                if (assignment != "111") // ako kontrolor radi
                {
                    coveredSectors.Add(assignment);
                }
            }

            // Vrati nepokrivene sektore
            return requiredSectors.Except(coveredSectors).ToList();
        }

        // Funkcija koja proverava da li dodela neće narušiti kontinuitet sektora
        private bool CanAssignWithoutBreakingSectorContinuity(Solution solution, int controllerIndex, int timeSlot, string sector)
        {
            // Dobavi osnovni sektor bez označene pozicije (E/P)
            string baseSector = sector.Length > 1 ? sector.Substring(0, sector.Length - 1) : sector;

            // Proveri da li bi dodela narušila kontinuitet sektora

            // 1. Provera prethodnog slota - ako kontrolor radi, mora biti na istom baznom sektoru
            if (timeSlot > 0)
            {
                string prevAssignment = solution.Assignments[controllerIndex, timeSlot - 1, 0];
                if (prevAssignment != "111") // Ako kontrolor radi u prethodnom slotu
                {
                    string prevBaseSector = prevAssignment.Length > 1 ?
                                           prevAssignment.Substring(0, prevAssignment.Length - 1) :
                                           prevAssignment;

                    // Kontrolor mora ostati na istom baznom sektoru
                    if (prevBaseSector != baseSector)
                    {
                        _logger.LogDebug($"Cannot assign controller {controllerIndex} to sector {sector} at slot {timeSlot} because it would break continuity with previous slot (sector {prevAssignment})");
                        return false; // Narušilo bi kontinuitet sa prethodnim slotom
                    }
                }
            }

            // 2. Provera sledećeg slota - ako kontrolor radi, mora biti na istom baznom sektoru
            if (timeSlot < solution.NumTimeSlots - 1)
            {
                string nextAssignment = solution.Assignments[controllerIndex, timeSlot + 1, 0];
                if (nextAssignment != "111") // Ako kontrolor radi u sledećem slotu
                {
                    string nextBaseSector = nextAssignment.Length > 1 ?
                                           nextAssignment.Substring(0, nextAssignment.Length - 1) :
                                           nextAssignment;

                    // Kontrolor mora ostati na istom baznom sektoru
                    if (nextBaseSector != baseSector)
                    {
                        _logger.LogDebug($"Cannot assign controller {controllerIndex} to sector {sector} at slot {timeSlot} because it would break continuity with next slot (sector {nextAssignment})");
                        return false; // Narušilo bi kontinuitet sa sledećim slotom
                    }
                }
            }

            // Ako smo došli do ovde, dodela ne bi narušila kontinuitet sektora
            return true;
        }

        private bool CheckSectorContinuityForAllControllers(Solution solution)
        {
            for (int c = 0; c < solution.NumControllers; c++)
            {
                string currentSector = null;

                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    string assignment = solution.Assignments[c, t, 0];

                    if (assignment != "111") // Ako kontrolor radi
                    {
                        string baseSector = assignment.Substring(0, assignment.Length - 1);

                        if (currentSector != null && baseSector != currentSector)
                        {
                            _logger.LogWarning($"Sector continuity violation: Controller {c} changes sector from {currentSector} to {baseSector} without break at slot {t}");
                            return false;
                        }

                        currentSector = baseSector;
                    }
                    else // Ako kontrolor ima pauzu
                    {
                        currentSector = null; // Resetuj trenutni sektor
                    }
                }
            }

            return true;
        }

        private void FixSectorContinuityViolations(Solution solution)
        {
            for (int c = 0; c < solution.NumControllers; c++)
            {
                string currentSector = null;
                int sectorStartSlot = -1;

                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    string assignment = solution.Assignments[c, t, 0];

                    if (assignment != "111") // Ako kontrolor radi
                    {
                        string baseSector = assignment.Substring(0, assignment.Length - 1);

                        if (currentSector == null) // Početak novog radnog bloka
                        {
                            currentSector = baseSector;
                            sectorStartSlot = t;
                        }
                        else if (baseSector != currentSector) // Promena sektora bez pauze
                        {
                            // Popravka: postavi kontrolora na pauzu
                            _logger.LogInformation($"Fixing sector continuity: Controller {c} was changing from {currentSector} to {baseSector} at slot {t} - setting to break");
                            solution.Assignments[c, t, 0] = "111";

                            // Pokušaj naći drugog kontrolora za ovaj sektor
                            TryReassignSector(solution, _initialSchedule, assignment, t);

                            // Resetuj praćenje sektora
                            currentSector = null;
                            sectorStartSlot = -1;
                        }
                    }
                    else // Pauza
                    {
                        currentSector = null;
                        sectorStartSlot = -1;
                    }
                }
            }
        }

        private int CheckUnassignedControllers(Solution solution)
        {
            int unassignedCount = 0;

            // Proveri da li svaki kontrolor ima barem jedno zaduženje
            for (int c = 0; c < solution.NumControllers; c++)
            {
                bool isAssigned = false;

                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    if (solution.Assignments[c, t, 0] != "111") // Ako nije pauza
                    {
                        isAssigned = true;
                        break;
                    }
                }

                if (!isAssigned)
                {
                    unassignedCount++;
                    _logger.LogWarning($"Controller at index {c} is not assigned to any sector");
                }
            }

            return unassignedCount;
        }

        private Solution GenerateNeighborSolution(Solution current, List<SectorConfiguration> configurations)
        {
            Solution neighbor = current.Clone();
            Random rand = new Random();
            int operation = rand.Next(6);

            bool validNeighbor = false;
            int attempts = 0;
            const int MAX_ATTEMPTS = 30; // Povećajmo broj pokušaja za generisanje komšije

            while (!validNeighbor && attempts < MAX_ATTEMPTS)
            {
                // Prvo vratimo na trenutno stanje pre nove modifikacije
                if (attempts > 0)
                {
                    neighbor = current.Clone();
                }

                switch (operation)
                {
                    case 0: SwapControllersInSlot(neighbor); break;
                    case 1: MoveBreak(neighbor); break;
                    case 2: SwapPositions(neighbor); break;
                    case 3: SwapTimeBlock(neighbor); break;
                    case 4: FillUncoveredSectors(neighbor, configurations); break;
                    case 5: SwitchPositionOnSameSector(neighbor); break; // Nova operacija
                }

                // Provera validnosti - dodajmo eksplicitnu proveru za ponavljanje sektora i Flag="S"
                validNeighbor = IsValidSolution(neighbor);
                attempts++;

                // Ako nije uspelo sa ovom operacijom, probaj drugu
                if (!validNeighbor && attempts % 6 == 0)
                {
                    operation = (operation + 1) % 6;
                }
            }

            if (attempts >= MAX_ATTEMPTS)
            {
                _logger.LogDebug($"Failed to generate valid neighbor after {MAX_ATTEMPTS} attempts.");
                return current;
            }

            // Dodatna sigurnosna provera i popravka ponavljanja sektora
            FixSectorRepetitions(neighbor);

            // Dodatna provera za Flag="S"
            FixFlagSViolations(neighbor);
            ApplyFlagSRules(neighbor);

            return neighbor;
        }

        // Nova funkcija za zamenu pozicije kontrolora na istom sektoru
        private void SwitchPositionOnSameSector(Solution solution)
        {
            // Izaberi slučajnog kontrolora
            int c = _random.Next(solution.NumControllers);

            // Pronađi neprekidne blokove rada na istom sektoru
            List<(int startSlot, int endSlot, string sectorKey)> sectorBlocks = new List<(int, int, string)>();
            string currentSectorKey = "";
            int blockStart = -1;

            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                string assignment = solution.Assignments[c, t, 0];

                if (assignment != "111") // Ako kontrolor radi
                {
                    // Promenjeno: koristimo drugačije nazive varijabli
                    string sectorKey = assignment.Length > 1 ? assignment.Substring(0, assignment.Length - 1) : "";
                    string positionCode = assignment.Length > 1 ? assignment.Substring(assignment.Length - 1) : "";

                    if (sectorKey != currentSectorKey) // Novi blok
                    {
                        if (blockStart != -1) // Zatvori prethodni blok
                        {
                            sectorBlocks.Add((blockStart, t - 1, currentSectorKey));
                        }

                        blockStart = t;
                        currentSectorKey = sectorKey;
                    }
                }
                else if (blockStart != -1) // Kraj bloka
                {
                    sectorBlocks.Add((blockStart, t - 1, currentSectorKey));
                    blockStart = -1;
                    currentSectorKey = "";
                }
            }

            // Zatvori poslednji blok ako postoji
            if (blockStart != -1)
            {
                sectorBlocks.Add((blockStart, solution.NumTimeSlots - 1, currentSectorKey));
            }

            // Ako nema blokova, izađi
            if (sectorBlocks.Count == 0)
            {
                return;
            }

            // Izaberi slučajni blok
            var selectedBlock = sectorBlocks[_random.Next(sectorBlocks.Count)];

            // Izaberi slučajni slot u bloku za promenu pozicije
            int selectedSlot = selectedBlock.startSlot + _random.Next(selectedBlock.endSlot - selectedBlock.startSlot + 1);

            // Zameni poziciju (E -> P ili P -> E)
            string currentAssignment = solution.Assignments[c, selectedSlot, 0];
            string sectorName = currentAssignment.Length > 1 ? currentAssignment.Substring(0, currentAssignment.Length - 1) : "";
            string positionChar = currentAssignment.Length > 1 ? currentAssignment.Substring(currentAssignment.Length - 1) : "";

            string newPosition = positionChar == "E" ? "P" : "E";
            solution.Assignments[c, selectedSlot, 0] = sectorName + newPosition;
        }

        private void FixFlagSViolations(Solution solution)
        {
            // Za svaki vremenski slot
            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                DateTime slotTime = _timeSlots[t];

                // Za svakog kontrolora u ovom slotu
                for (int c = 0; c < solution.NumControllers; c++)
                {
                    // Ako kontrolor radi
                    if (solution.Assignments[c, t, 0] != "111")
                    {
                        // Proveri da li ima Flag="S"
                        if (IsFlagS(_controllers[c], slotTime, _initialSchedule))
                        {
                            _logger.LogWarning($"Fixed Flag=S violation: Controller {c} ({_controllers[c]}) removed from sector {solution.Assignments[c, t, 0]} at slot {t} (time {slotTime})");

                            // Postavi kontrolora na pauzu
                            solution.Assignments[c, t, 0] = "111";

                            // Pokušaj naći drugog kontrolora koji bi mogao pokrivati ovaj sektor
                            TryReassignSector(solution, _initialSchedule, solution.Assignments[c, t, 0], t);
                        }
                    }
                }
            }
        }

        private bool IsValidSolution(Solution solution)
        {
            // Za svaki vremenski slot
            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                DateTime slotTime = _timeSlots[t];

                // Dobavi validne sektore za ovaj slot
                var validSectors = _configurations.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= slotTime &&
                        row.Field<DateTime>("datumDo") > slotTime)
                    .Select(row => row.Field<string>("sektor"))
                    .Distinct()
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                // Hash set za praćenje dodeljenih sektora
                var assignedSectors = new HashSet<string>();

                for (int c = 0; c < solution.NumControllers; c++)
                {
                    string assignment = solution.Assignments[c, t, 0];

                    if (assignment != "111") // Ako nije pauza
                    {
                        // Dobavi vreme početka smene za kontrolora
                        var shift = _initialSchedule.AsEnumerable()
                            .FirstOrDefault(row => row.Field<string>("sifra") == _controllers[c]);

                        if (shift != null)
                        {
                            DateTime controllerStart = shift.Field<DateTime>("VremeStart");
                            string smena = shift.Field<string>("smena");

                            // Proveri da li kontrolor ima Flag="S" za ovaj slot
                            bool isFlagS = IsFlagS(_controllers[c], slotTime, _initialSchedule);
                            if (isFlagS)
                            {
                                _logger.LogWarning($"Invalid assignment: Controller {c} ({_controllers[c]}) with Flag=S assigned at slot {t} (time {slotTime})");
                                return false;
                            }

                            // Proveri da li je kontrolor u smeni
                            if (slotTime < controllerStart)
                            {
                                _logger.LogWarning($"Invalid assignment: Controller {c} ({_controllers[c]}) assigned before shift start. Slot time: {slotTime}, Shift start: {controllerStart}");
                                return false;
                            }

                            // Proveri za M smenu u poslednjem satu
                            if (smena == "M" && t >= solution.NumTimeSlots - 2)
                            {
                                _logger.LogWarning($"Invalid assignment: Controller {c} ({_controllers[c]}) with M shift assigned in last hour");
                                return false;
                            }
                        }

                        // Proveri da li je sektor validan za ovaj slot
                        if (!validSectors.Contains(assignment))
                        {
                            _logger.LogWarning($"Invalid assignment: Controller {c} assigned to invalid sector {assignment} in slot {t}");
                            return false;
                        }

                        // Proveri da li je sektor već dodeljen
                        if (assignedSectors.Contains(assignment))
                        {
                            _logger.LogWarning($"Duplicate assignment: Sector {assignment} assigned multiple times in slot {t}");
                            return false;
                        }

                        assignedSectors.Add(assignment);
                    }
                }
            }

            // Ako nema duplikata i nevalidnih sektora, rešenje je validno
            return true;
        }

        private void FixSectorRepetitions(Solution solution)
        {
            // Pripremi rečnik za informacije o smeni kontrolora
            var controllerStartTimes = new Dictionary<int, DateTime>();
            var controllerShiftTypes = new Dictionary<int, string>();

            for (int c = 0; c < solution.NumControllers; c++)
            {
                var shift = _initialSchedule.AsEnumerable()
                    .FirstOrDefault(row => row.Field<string>("sifra") == _controllers[c]);

                if (shift != null)
                {
                    controllerStartTimes[c] = shift.Field<DateTime>("VremeStart");
                    controllerShiftTypes[c] = shift.Field<string>("smena")!;
                }
                else
                {
                    // Postavi podrazumevane vrednosti
                    controllerStartTimes[c] = _timeSlots.FirstOrDefault();
                    controllerShiftTypes[c] = "J";
                }
            }

            // Za svaki vremenski slot
            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                DateTime slotTime = _timeSlots[t];

                // Dobavi validne sektore za ovaj slot
                var validSectors = _configurations.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= slotTime &&
                        row.Field<DateTime>("datumDo") > slotTime)
                    .Select(row => row.Field<string>("sektor"))
                    .Distinct()
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                // Rečnik za praćenje dodeljenih sektora i kontrolora
                var sectorAssignments = new Dictionary<string, int>();

                // Prvi prolaz - identifikuj ponavljanja, nevalidne sektore i kontrolore van smene
                for (int c = 0; c < solution.NumControllers; c++)
                {
                    string assignment = solution.Assignments[c, t, 0];

                    if (assignment != "111") // Ako nije pauza
                    {
                        // Proveri da li je kontrolor u smeni
                        DateTime controllerStart = controllerStartTimes[c];
                        string smena = controllerShiftTypes[c];

                        // VAŽNO: Kontrolor se briše iz rasporeda ako radi pre početka smene
                        if (slotTime < controllerStart || (smena == "M" && t >= solution.NumTimeSlots - 2))
                        {
                            _logger.LogWarning($"Fixed out-of-shift: Controller {c} ({_controllers[c]}) removed from sector {assignment} in slot {t} (time {slotTime}). Shift starts at {controllerStart}");
                            solution.Assignments[c, t, 0] = "111"; // Postavi na pauzu
                            continue;
                        }

                        // Proveri da li je sektor validan
                        if (!validSectors.Contains(assignment))
                        {
                            _logger.LogWarning($"Fixed invalid sector: Controller {c} ({_controllers[c]}) removed from invalid sector {assignment} in slot {t}");
                            solution.Assignments[c, t, 0] = "111"; // Postavi na pauzu
                            continue;
                        }

                        if (!sectorAssignments.ContainsKey(assignment))
                        {
                            sectorAssignments[assignment] = c;
                        }
                        else
                        {
                            // Pronađeno ponavljanje - postavi na pauzu
                            solution.Assignments[c, t, 0] = "111";
                            _logger.LogDebug($"Fixed repetition: Controller {c} ({_controllers[c]}) removed from sector {assignment} in slot {t}");
                        }
                    }
                }

                // Drugi prolaz - pokušaj dodeliti kontrolore na nepokrivene sektore
                if (sectorAssignments.Count < validSectors.Count)
                {
                    var uncoveredSectors = validSectors.Except(sectorAssignments.Keys).ToList();
                    var freeControllers = new List<int>();

                    // Pronađi slobodne kontrolore koji su U SMENI u ovom slotu
                    for (int c = 0; c < solution.NumControllers; c++)
                    {
                        if (solution.Assignments[c, t, 0] == "111")
                        {
                            // VAŽNO: Kontrolor je slobodan samo ako je u smeni
                            DateTime controllerStart = controllerStartTimes[c];
                            string smena = controllerShiftTypes[c];

                            if (slotTime >= controllerStart && (smena != "M" || t < solution.NumTimeSlots - 2))
                            {
                                freeControllers.Add(c);
                            }
                        }
                    }

                    // Ako imamo slobodne kontrolore, dodeli ih na nepokrivene sektore
                    if (freeControllers.Any() && uncoveredSectors.Any())
                    {
                        int controllersToAssign = Math.Min(freeControllers.Count, uncoveredSectors.Count);

                        for (int i = 0; i < controllersToAssign; i++)
                        {
                            solution.Assignments[freeControllers[i], t, 0] = uncoveredSectors[i]!;
                            _logger.LogDebug($"Fixed coverage: Controller {freeControllers[i]} ({_controllers[freeControllers[i]]}) assigned to uncovered sector {uncoveredSectors[i]} in slot {t} (time {slotTime})");
                        }
                    }
                }
            }
        }

        private void EnforceBreakRules(Solution solution)
        {
            // Pripremi informacije o smeni kontrolora
            var controllerStartTimes = new Dictionary<int, DateTime>();
            var controllerShiftTypes = new Dictionary<int, string>();

            for (int c = 0; c < solution.NumControllers; c++)
            {
                var shift = _initialSchedule.AsEnumerable()
                    .FirstOrDefault(row => row.Field<string>("sifra") == _controllers[c]);

                if (shift != null)
                {
                    controllerStartTimes[c] = shift.Field<DateTime>("VremeStart");
                    controllerShiftTypes[c] = shift.Field<string>("smena")!;
                }
                else
                {
                    controllerStartTimes[c] = _timeSlots.FirstOrDefault();
                    controllerShiftTypes[c] = "J";
                }
            }

            // Za svakog kontrolora
            for (int c = 0; c < solution.NumControllers; c++)
            {
                int consecutiveWork = 0;
                DateTime controllerStart = controllerStartTimes[c];
                string smena = controllerShiftTypes[c];

                // Proveri uzastopni rad i dodaj obavezne pauze
                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    DateTime slotTime = _timeSlots[t];

                    // VAŽNO: Prvo proveri da li je kontrolor u smeni
                    bool inShift = slotTime >= controllerStart && (smena != "M" || t < solution.NumTimeSlots - 2);

                    if (!inShift)
                    {
                        // Ako nije u smeni, mora biti na pauzi
                        if (solution.Assignments[c, t, 0] != "111")
                        {
                            solution.Assignments[c, t, 0] = "111";
                            _logger.LogDebug($"EnforceBreakRules: Controller {c} ({_controllers[c]}) removed from slot {t} (time {slotTime}) - not in shift");
                        }
                        consecutiveWork = 0;
                        continue;
                    }

                    if (solution.Assignments[c, t, 0] != "111")
                    {
                        consecutiveWork++;

                        // Nakon 2 sata rada (4 slota) mora biti pauza
                        if (consecutiveWork >= 4 && t + 1 < solution.NumTimeSlots)
                        {
                            // Proveri da li je kontrolor u smeni u sledećim slotovima
                            bool nextSlotInShift = t + 1 < solution.NumTimeSlots &&
                                                 _timeSlots[t + 1] >= controllerStart &&
                                                 (smena != "M" || t + 1 < solution.NumTimeSlots - 2);

                            bool nextNextSlotInShift = t + 2 < solution.NumTimeSlots &&
                                                     _timeSlots[t + 2] >= controllerStart &&
                                                     (smena != "M" || t + 2 < solution.NumTimeSlots - 2);

                            // Dodaj pauzu od 1 sat (2 slota) ako je kontrolor u smeni
                            if (nextSlotInShift)
                            {
                                solution.Assignments[c, t + 1, 0] = "111";
                                if (nextNextSlotInShift)
                                    solution.Assignments[c, t + 2, 0] = "111";

                                consecutiveWork = 0;
                                t += nextNextSlotInShift ? 2 : 1; // Preskoči pauzu
                            }
                        }
                    }
                    else
                    {
                        consecutiveWork = 0;
                    }
                }
            }
        }

        private void SwapControllersInSlot(Solution solution)
        {
            // Izaberi slučajni vremenski slot
            int t = _random.Next(solution.NumTimeSlots);
            DateTime slotTime = _timeSlots[t];

            // Pripremi informacije o smeni kontrolora
            var controllerStartTimes = new Dictionary<int, DateTime>();
            var controllerShiftTypes = new Dictionary<int, string>();

            for (int c = 0; c < solution.NumControllers; c++)
            {
                var shift = _initialSchedule.AsEnumerable()
                    .FirstOrDefault(row => row.Field<string>("sifra") == _controllers[c]);

                if (shift != null)
                {
                    controllerStartTimes[c] = shift.Field<DateTime>("VremeStart");
                    controllerShiftTypes[c] = shift.Field<string>("smena")!;
                }
                else
                {
                    controllerStartTimes[c] = _timeSlots.FirstOrDefault();
                    controllerShiftTypes[c] = "J";
                }
            }

            // Izaberi dva različita kontrolora koji rade u tom slotu
            List<int> workingControllers = new List<int>();

            for (int c = 0; c < solution.NumControllers; c++)
            {
                if (solution.Assignments[c, t, 0] != "111")
                {
                    workingControllers.Add(c);
                }
            }

            if (workingControllers.Count < 2)
            {
                return; // Ne možemo zameniti ako nemamo bar dva kontrolora
            }

            int c1Idx = _random.Next(workingControllers.Count);
            int c2Idx = _random.Next(workingControllers.Count);

            // Osiguraj da su kontrolori različiti
            int attempts = 0;
            while (c2Idx == c1Idx && attempts < 10)
            {
                c2Idx = _random.Next(workingControllers.Count);
                attempts++;
            }

            if (c1Idx == c2Idx)
            {
                return; // Odustani ako ne možemo dobiti dva različita kontrolora
            }

            int c1 = workingControllers[c1Idx];
            int c2 = workingControllers[c2Idx];

            // VAŽNO: Proveri da li su oba kontrolora u smeni u ovom slotu
            bool c1InShift = slotTime >= controllerStartTimes[c1] &&
                             (controllerShiftTypes[c1] != "M" || t < solution.NumTimeSlots - 2);
            bool c2InShift = slotTime >= controllerStartTimes[c2] &&
                             (controllerShiftTypes[c2] != "M" || t < solution.NumTimeSlots - 2);

            if (!c1InShift || !c2InShift)
            {
                return; // Odustani ako neki od kontrolora nije u smeni
            }

            // Zameni dodele
            string temp = solution.Assignments[c1, t, 0];
            solution.Assignments[c1, t, 0] = solution.Assignments[c2, t, 0];
            solution.Assignments[c2, t, 0] = temp;
        }


        private void MoveBreak(Solution solution)
        {
            // Izaberi slučajnog kontrolora
            int c = _random.Next(solution.NumControllers);

            // Dobavi informacije o smeni kontrolora
            var shift = _initialSchedule.AsEnumerable()
                .FirstOrDefault(row => row.Field<string>("sifra") == _controllers[c]);

            if (shift == null) return;

            DateTime controllerStart = shift.Field<DateTime>("VremeStart");
            string smena = shift.Field<string>("smena");

            // Pronađi jednu pauzu
            List<int> breakStartSlots = new List<int>();
            for (int t = 0; t < solution.NumTimeSlots - 1; t++)
            {
                if (solution.Assignments[c, t, 0] != "111" && solution.Assignments[c, t + 1, 0] == "111")
                    breakStartSlots.Add(t + 1);
            }

            if (breakStartSlots.Count == 0)
                return; // Nema pauza

            int breakStart = breakStartSlots[_random.Next(breakStartSlots.Count)];

            // Odredi dužinu pauze
            int breakLength = 0;
            for (int t = breakStart; t < solution.NumTimeSlots; t++)
            {
                if (solution.Assignments[c, t, 0] == "111")
                    breakLength++;
                else
                    break;
            }

            // Izaberi novi početak pauze - VAŽNO: Mora biti u smeni!
            List<int> possibleNewStarts = new List<int>();
            for (int t = 0; t < solution.NumTimeSlots - breakLength + 1; t++)
            {
                // Proveri da li je cela pauza u smeni
                bool allInShift = true;
                for (int i = 0; i < breakLength; i++)
                {
                    DateTime slotTime = _timeSlots[t + i];
                    if (slotTime < controllerStart || (smena == "M" && t + i >= solution.NumTimeSlots - 2))
                    {
                        allInShift = false;
                        break;
                    }
                }

                if (allInShift)
                {
                    possibleNewStarts.Add(t);
                }
            }

            if (possibleNewStarts.Count == 0)
                return; // Nema mogućih novih početaka

            int newBreakStart = possibleNewStarts[_random.Next(possibleNewStarts.Count)];

            // Sačuvaj trenutni raspored
            string[] temp = new string[solution.NumTimeSlots];
            for (int t = 0; t < solution.NumTimeSlots; t++)
                temp[t] = solution.Assignments[c, t, 0];

            // Ukloni staru pauzu
            for (int t = breakStart; t < breakStart + breakLength; t++)
            {
                if (t > 0 && temp[t - 1] != "111")
                    solution.Assignments[c, t, 0] = temp[t - 1];
                else if (t + breakLength < solution.NumTimeSlots && temp[t + breakLength] != "111")
                    solution.Assignments[c, t, 0] = temp[t + breakLength];
                else
                    solution.Assignments[c, t, 0] = "111";
            }

            // Dodaj novu pauzu
            for (int t = newBreakStart; t < newBreakStart + breakLength; t++)
                solution.Assignments[c, t, 0] = "111";

            // Dodatna provera da svi slotovi poštuju VremeStart
            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                DateTime slotTime = _timeSlots[t];
                bool inShift = slotTime >= controllerStart && (smena != "M" || t < solution.NumTimeSlots - 2);

                if (!inShift && solution.Assignments[c, t, 0] != "111")
                {
                    solution.Assignments[c, t, 0] = "111";
                }
            }
        }

        private void SwapPositions(Solution solution)
        {
            // Izaberi slučajni vremenski slot
            int t = _random.Next(solution.NumTimeSlots);

            // Pronađi parove kontrolora koji rade kao EXE i PLAN na istom sektoru
            List<(int execIdx, int planIdx, string sector)> pairs = new List<(int, int, string)>();

            for (int c1 = 0; c1 < solution.NumControllers; c1++)
            {
                if (solution.Assignments[c1, t, 0] != "111" && solution.Assignments[c1, t, 0].EndsWith("E"))
                {
                    string baseSector = solution.Assignments[c1, t, 0].Substring(0, solution.Assignments[c1, t, 0].Length - 1);

                    for (int c2 = 0; c2 < solution.NumControllers; c2++)
                    {
                        if (c2 != c1 && solution.Assignments[c2, t, 0] == baseSector + "P")
                        {
                            // Dodatna provera za kontinuitet sektora
                            bool c1Continues = true;
                            bool c2Continues = true;

                            // Provera kontinuiteta za prethodni slot
                            if (t > 0)
                            {
                                if (solution.Assignments[c1, t - 1, 0] != "111")
                                {
                                    string prevBaseSector1 = solution.Assignments[c1, t - 1, 0].Substring(0, solution.Assignments[c1, t - 1, 0].Length - 1);
                                    if (prevBaseSector1 != baseSector)
                                        c1Continues = false;
                                }

                                if (solution.Assignments[c2, t - 1, 0] != "111")
                                {
                                    string prevBaseSector2 = solution.Assignments[c2, t - 1, 0].Substring(0, solution.Assignments[c2, t - 1, 0].Length - 1);
                                    if (prevBaseSector2 != baseSector)
                                        c2Continues = false;
                                }
                            }

                            // Provera kontinuiteta za sledeći slot
                            if (t < solution.NumTimeSlots - 1)
                            {
                                if (solution.Assignments[c1, t + 1, 0] != "111")
                                {
                                    string nextBaseSector1 = solution.Assignments[c1, t + 1, 0].Substring(0, solution.Assignments[c1, t + 1, 0].Length - 1);
                                    if (nextBaseSector1 != baseSector)
                                        c1Continues = false;
                                }

                                if (solution.Assignments[c2, t + 1, 0] != "111")
                                {
                                    string nextBaseSector2 = solution.Assignments[c2, t + 1, 0].Substring(0, solution.Assignments[c2, t + 1, 0].Length - 1);
                                    if (nextBaseSector2 != baseSector)
                                        c2Continues = false;
                                }
                            }

                            // Dodaj par samo ako zamena neće narušiti kontinuitet
                            if (c1Continues && c2Continues)
                            {
                                pairs.Add((c1, c2, baseSector));
                                break;
                            }
                        }
                    }
                }
            }

            if (pairs.Count == 0)
                return; // Nema parova za zamenu

            // Izaberi slučajni par
            var pair = pairs[_random.Next(pairs.Count)];

            // Zameni pozicije
            solution.Assignments[pair.execIdx, t, 0] = pair.sector + "P";
            solution.Assignments[pair.planIdx, t, 0] = pair.sector + "E";
        }

        private void SwapTimeBlock(Solution solution)
        {
            // Izaberi dva različita kontrolora
            int c1 = _random.Next(solution.NumControllers);
            int c2 = _random.Next(solution.NumControllers);
            int attempts = 0;

            while (c2 == c1 && attempts < 10)
            {
                c2 = _random.Next(solution.NumControllers);
                attempts++;
            }

            if (c1 == c2)
            {
                return; // Odustani ako ne možemo dobiti dva različita kontrolora
            }

            // Pripremi informacije o smeni kontrolora
            var c1Shift = _initialSchedule.AsEnumerable().FirstOrDefault(row => row.Field<string>("sifra") == _controllers[c1]);
            var c2Shift = _initialSchedule.AsEnumerable().FirstOrDefault(row => row.Field<string>("sifra") == _controllers[c2]);

            if (c1Shift == null || c2Shift == null)
            {
                return; // Odustani ako nemamo informacije o smeni
            }

            DateTime c1Start = c1Shift.Field<DateTime>("VremeStart");
            DateTime c2Start = c2Shift.Field<DateTime>("VremeStart");
            string c1ShiftType = c1Shift.Field<string>("smena");
            string c2ShiftType = c2Shift.Field<string>("smena");

            // Izaberi početak i dužinu vremenskog bloka
            int start = _random.Next(solution.NumTimeSlots - 1);
            int length = _random.Next(1, Math.Min(4, solution.NumTimeSlots - start));

            // Proveri da li su oba kontrolora u smeni u izabranom bloku
            bool bothInShift = true;

            for (int i = 0; i < length; i++)
            {
                int currentSlot = start + i;
                DateTime slotTime = _timeSlots[currentSlot];

                bool c1InShift = slotTime >= c1Start && (c1ShiftType != "M" || currentSlot < solution.NumTimeSlots - 2);
                bool c2InShift = slotTime >= c2Start && (c2ShiftType != "M" || currentSlot < solution.NumTimeSlots - 2);

                if (!c1InShift || !c2InShift)
                {
                    bothInShift = false;
                    break;
                }
            }

            if (!bothInShift)
            {
                return; // Odustani ako neki od kontrolora nije u smeni u celom bloku
            }

            // Kreiraj privremeni niz da sačuvamo dodele
            string[] c1Assignments = new string[length];
            string[] c2Assignments = new string[length];

            // Sačuvaj trenutne dodele
            for (int i = 0; i < length; i++)
            {
                c1Assignments[i] = solution.Assignments[c1, start + i, 0];
                c2Assignments[i] = solution.Assignments[c2, start + i, 0];
            }

            // Zameni dodele
            for (int i = 0; i < length; i++)
            {
                solution.Assignments[c1, start + i, 0] = c2Assignments[i];
                solution.Assignments[c2, start + i, 0] = c1Assignments[i];
            }
        }

        private OptimizationResponse ConvertToResponse(Solution solution, List<string> controllers, List<DateTime> timeSlots, DataTable initialSchedule, DateTime datum)
        {
            var optimizedResults = new List<OptimizationResultDTO>();

            // Konvertuj rešenje u listu OptimizationResultDTO
            for (int c = 0; c < solution.NumControllers; c++)
            {
                string controllerId = controllers[c];
                var controllerData = initialSchedule.AsEnumerable()
                    .FirstOrDefault(row => row.Field<string>("sifra") == controllerId);

                if (controllerData == null) continue;

                // Za svaki vremenski slot
                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    DateTime slotTime = timeSlots[t];

                    // Dobavi informacije o smeni kontrolora unutar petlje za svaki slot
                    DateTime controllerVremeStart = controllerData.Field<DateTime>("VremeStart");
                    string controllerSmena = controllerData.Field<string>("smena");

                    // Proveri da li je kontrolor u smeni u ovom slotu
                    bool inShift = slotTime >= controllerVremeStart;

                    if (inShift)
                    {
                        // Dodaj slot bez obzira da li kontrolor radi ili ima pauzu
                        optimizedResults.Add(new OptimizationResultDTO
                        {
                            Sifra = controllerId,
                            PrezimeIme = controllerData.Field<string>("PrezimeIme"),
                            Smena = controllerData.Field<string>("smena"),
                            Datum = datum,
                            DatumOd = slotTime,
                            DatumDo = slotTime.AddMinutes(_slotDurationMinutes),
                            Sektor = solution.Assignments[c, t, 0] == "111" ? null : solution.Assignments[c, t, 0], 
                            ORM = controllerData.Field<string>("ORM"),
                            Flag = IsFlagS(controllerId, slotTime, initialSchedule) ? "S" : null, //controllerData.Field<string>("Flag"),
                            VremeStart = controllerData.Field<DateTime>("VremeStart")
                        });
                    }
                }
            }

            // Izračunaj statistiku, manjkove sektora i druge informacije
            var statistics = CalculateStatistics(solution, controllers, timeSlots);
            var shortages = CalculateSlotShortages(solution, controllers, timeSlots);
            var configLabels = BuildConfigurationLabels(timeSlots);

            return new OptimizationResponse
            {
                OptimizedResults = optimizedResults,
                NonOptimizedResults = new List<OptimizationResultDTO>(),
                AllResults = optimizedResults,
                Statistics = statistics,
                SlotShortages = shortages,
                ConfigurationLabels = configLabels,
                InitialAssignments = BuildInitialAssignments(initialSchedule)
            };
        }

        private bool IsFlagS(string controllerId, DateTime slotTime, DataTable initialSchedule)
        {
            var controllerShifts = initialSchedule.AsEnumerable()
                .Where(row => row.Field<string>("sifra") == controllerId)
                .ToList();

            foreach (var shift in controllerShifts)
            {
                DateTime shiftStart = shift.Field<DateTime>("datumOd");
                DateTime shiftEnd = shift.Field<DateTime>("datumDo");
                string flag = shift.Field<string>("Flag");

                if (flag == "S" && slotTime >= shiftStart && slotTime < shiftEnd)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TestFlagSRules(Solution solution)
        {
            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                DateTime slotTime = _timeSlots[t];

                for (int c = 0; c < solution.NumControllers; c++)
                {
                    if (solution.Assignments[c, t, 0] != "111" && IsFlagS(_controllers[c], slotTime, _initialSchedule))
                    {
                        _logger.LogError($"Flag S violation detected: Controller {c} ({_controllers[c]}) working on {solution.Assignments[c, t, 0]} at slot {t} (time {slotTime})");
                        return false;
                    }
                }
            }

            return true;
        }

        private void ApplyFlagSRules(Solution solution)
        {
            // Za svaki vremenski slot
            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                DateTime slotTime = _timeSlots[t];

                // Za svakog kontrolora u ovom slotu
                for (int c = 0; c < solution.NumControllers; c++)
                {
                    // Ako kontrolor ima flag "S" u ovom slotu, mora biti na pauzi
                    if (IsFlagS(_controllers[c], slotTime, _initialSchedule))
                    {
                        if (solution.Assignments[c, t, 0] != "111")
                        {
                            string previousAssignment = solution.Assignments[c, t, 0];
                            solution.Assignments[c, t, 0] = "111"; // Postavi na pauzu

                            _logger.LogInformation($"Applied Flag=S rule: Controller {c} ({_controllers[c]}) removed from sector {previousAssignment} at slot {t} (time {slotTime})");

                            // Pokušaj da pronađeš drugog kontrolora za ovaj sektor
                            TryReassignSector(solution, _initialSchedule, previousAssignment, t);
                        }
                    }
                }
            }
        }

        private void MaximizeUtilizationBetweenFlagSPeriods(Solution solution)
        {
            _logger.LogInformation("Starting maximizing utilization between Flag=S periods");

            // Proći kroz sve kontrolore
            for (int c = 0; c < solution.NumControllers; c++)
            {
                string controllerId = _controllers[c];

                // Pronađi periode kada kontrolor ima flag="S"
                List<(int startSlot, int endSlot)> flagSPeriods = FindFlagSPeriods(controllerId);

                // Ako kontrolor ima više od jednog flag="S" perioda, fokusiraj se na periode između njih
                if (flagSPeriods.Count > 1)
                {
                    for (int i = 0; i < flagSPeriods.Count - 1; i++)
                    {
                        int periodStart = flagSPeriods[i].endSlot; // kraj prvog flag="S" perioda
                        int periodEnd = flagSPeriods[i + 1].startSlot; // početak sledećeg flag="S" perioda

                        _logger.LogDebug($"Optimizing controller {controllerId} between Flag=S periods: slots {periodStart}-{periodEnd}");

                        // Fokusiraj se na maksimalnu iskorišćenost kontrolora u ovom periodu
                        MaximizeControllerInPeriod(solution, c, periodStart, periodEnd);
                    }
                }

                // Takođe optimizuj period nakon poslednjeg flag="S" i pre prvog flag="S"
                if (flagSPeriods.Any())
                {
                    // Period pre prvog flag="S"
                    if (flagSPeriods.First().startSlot > 0)
                    {
                        MaximizeControllerInPeriod(solution, c, 0, flagSPeriods.First().startSlot);
                    }

                    // Period nakon poslednjeg flag="S"
                    if (flagSPeriods.Last().endSlot < solution.NumTimeSlots)
                    {
                        MaximizeControllerInPeriod(solution, c, flagSPeriods.Last().endSlot, solution.NumTimeSlots);
                    }
                }
            }
        }

        private List<(int startSlot, int endSlot)> FindFlagSPeriods(string controllerId)
        {
            List<(int startSlot, int endSlot)> flagSPeriods = new List<(int, int)>();
            bool inFlagSPeriod = false;
            int currentPeriodStart = -1;

            for (int t = 0; t < _timeSlots.Count; t++)
            {
                DateTime slotTime = _timeSlots[t];
                bool hasFlagS = IsFlagS(controllerId, slotTime, _initialSchedule);

                // Početak novog flag="S" perioda
                if (hasFlagS && !inFlagSPeriod)
                {
                    inFlagSPeriod = true;
                    currentPeriodStart = t;
                }
                // Kraj flag="S" perioda
                else if (!hasFlagS && inFlagSPeriod)
                {
                    inFlagSPeriod = false;
                    flagSPeriods.Add((currentPeriodStart, t));
                }
            }

            // Ako flag="S" period traje do kraja smene
            if (inFlagSPeriod)
            {
                flagSPeriods.Add((currentPeriodStart, _timeSlots.Count));
            }

            return flagSPeriods;
        }

        private void MaximizeControllerInPeriod(Solution solution, int controllerIndex, int startSlot, int endSlot)
        {
            // Proveri trenutno radno vreme u periodu
            int currentWorkingSlots = 0;
            for (int t = startSlot; t < endSlot; t++)
            {
                if (solution.Assignments[controllerIndex, t, 0] != "111")
                {
                    currentWorkingSlots++;
                }
            }

            _logger.LogDebug($"Controller {_controllers[controllerIndex]} is currently working {currentWorkingSlots} out of {endSlot - startSlot} slots in period");

            // Ako kontrolor već radi >= 75% vremena u periodu, nema potrebe za optimizacijom
            double currentUtilization = (double)currentWorkingSlots / (endSlot - startSlot);
            if (currentUtilization >= 0.75)
            {
                _logger.LogDebug($"Controller {_controllers[controllerIndex]} already has high utilization ({currentUtilization:P}) in period - skipping");
                return;
            }

            // Prolazimo kroz slotove i tražimo nepokrivene sektore
            for (int t = startSlot; t < endSlot; t++)
            {
                // Ako kontrolor odmara u ovom slotu
                if (solution.Assignments[controllerIndex, t, 0] == "111")
                {
                    // Proveri da poštuje pravilo o pauzama - ne više od 4 uzastopna slota rada
                    if (!CanAssignWithoutBreakingWorkingTimeRules(solution, controllerIndex, t))
                    {
                        continue;
                    }

                    // Pronađi nepokrivene sektore za ovaj slot
                    var uncoveredSectors = GetUncoveredSectors(solution, t);

                    if (uncoveredSectors.Any())
                    {
                        // Dodatna provera: nađi sektor koji neće narušiti kontinuitet sektora
                        string sectorToAssign = null;

                        foreach (var sector in uncoveredSectors)
                        {
                            if (CanAssignWithoutBreakingSectorContinuity(solution, controllerIndex, t, sector))
                            {
                                sectorToAssign = sector;
                                break;
                            }
                        }

                        if (sectorToAssign != null)
                        {
                            solution.Assignments[controllerIndex, t, 0] = sectorToAssign;
                            _logger.LogInformation($"Assigned controller {_controllers[controllerIndex]} to sector {sectorToAssign} at slot {t} in optimization period");
                        }
                    }
                }
            }
        }

        // Provera da li dodela neće narušiti pravila o radnom vremenu
        private bool CanAssignWithoutBreakingWorkingTimeRules(Solution solution, int controllerIndex, int timeSlot)
        {
            const int MAX_CONSECUTIVE_WORK = 4; // maksimalno 2 sata rada (4 slota po 30 min)

            // Proveri unazad koliko uzastopnih slotova kontrolor radi
            int consecutiveWorkBefore = 0;
            for (int t = timeSlot - 1; t >= 0 && consecutiveWorkBefore < MAX_CONSECUTIVE_WORK; t--)
            {
                if (solution.Assignments[controllerIndex, t, 0] != "111")
                {
                    consecutiveWorkBefore++;
                }
                else
                {
                    break; // naišli smo na pauzu
                }
            }

            // Proveri unapred koliko uzastopnih slotova kontrolor radi
            int consecutiveWorkAfter = 0;
            for (int t = timeSlot + 1; t < solution.NumTimeSlots && consecutiveWorkAfter < MAX_CONSECUTIVE_WORK; t++)
            {
                if (solution.Assignments[controllerIndex, t, 0] != "111")
                {
                    consecutiveWorkAfter++;
                }
                else
                {
                    break; // naišli smo na pauzu
                }
            }

            // Ako bi dodela prekršila maksimalno vreme rada, ne možemo dodeliti
            return (consecutiveWorkBefore + consecutiveWorkAfter + 1) <= MAX_CONSECUTIVE_WORK;
        }

        private OptimizationStatistics CalculateStatistics(Solution solution, List<string> controllers, List<DateTime> timeSlots)
        {
            var stats = new OptimizationStatistics();

            // Ukupan broj potrebnih sektora i pokrivenih sektora
            int totalRequiredSectors = 0;
            int coveredSectors = 0;

            // Izračunaj statistiku pokrivenosti sektora za svaki timeslot
            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                DateTime slotTime = timeSlots[t];

                // Dobavi sektore koji bi trebalo da budu pokriveni u ovom slotu
                var txConfigs = _configurations.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= slotTime &&
                        row.Field<DateTime>("datumDo") > slotTime &&
                        row.Field<string>("ConfigType") == "TX")
                    .ToList();

                var luConfigs = _configurations.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= slotTime &&
                        row.Field<DateTime>("datumDo") > slotTime &&
                        row.Field<string>("ConfigType") == "LU")
                    .ToList();

                // Ukupan broj potrebnih sektora (svaki sektor treba da ima E i P poziciju)
                totalRequiredSectors += (txConfigs.Count + luConfigs.Count);

                // Brojanje pokrivenih sektora
                foreach (var config in txConfigs.Concat(luConfigs))
                {
                    string sector = config.Field<string>("sektor");
                    string sectorBase = sector.Substring(0, sector.Length - 1); // osnova sektora bez E/P
                    string position = sector.Substring(sector.Length - 1); // E ili P

                    bool isPositionCovered = false;

                    // Proveri da li je ovaj sektor i pozicija pokrivena u rešenju
                    for (int c = 0; c < solution.NumControllers; c++)
                    {
                        if (solution.Assignments[c, t, 0] == sector)
                        {
                            isPositionCovered = true;
                            coveredSectors++;
                            break;
                        }
                    }

                    // Ako pozicija nije pokrivena, onda imamo manjak
                    if (!isPositionCovered)
                    {
                        stats.SlotsWithShortage++;
                    }
                }

                // Proveri da li ima viška kontrolora na sektorima
                Dictionary<string, int> sectorAssignmentCount = new Dictionary<string, int>();

                // Broji koliko kontrolora je dodeljeno svakom sektoru
                for (int c = 0; c < solution.NumControllers; c++)
                {
                    string assignment = solution.Assignments[c, t, 0];

                    if (assignment != "111") // Ako nije pauza
                    {
                        string sectorBase = assignment.Substring(0, assignment.Length - 1);

                        if (!sectorAssignmentCount.ContainsKey(sectorBase))
                        {
                            sectorAssignmentCount[sectorBase] = 0;
                        }

                        sectorAssignmentCount[sectorBase]++;
                    }
                }

                // Ako neki sektor ima više od 2 dodeljena kontrolora (E i P), to je višak
                foreach (var kvp in sectorAssignmentCount)
                {
                    if (kvp.Value > 2)
                    {
                        stats.SlotsWithExcess++;
                    }
                }
            }

            // Izračunaj procenat uspešnosti
            if (totalRequiredSectors > 0)
            {
                stats.SuccessRate = (double)coveredSectors / totalRequiredSectors * 100;
            }
            else
            {
                stats.SuccessRate = 100;
            }

            // Izračunaj distribuciju radnog vremena kontrolora
            Dictionary<string, int> controllerWorkload = new Dictionary<string, int>();

            for (int c = 0; c < solution.NumControllers; c++)
            {
                string controllerId = controllers[c];
                int workMinutes = 0;

                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    if (solution.Assignments[c, t, 0] != "111") // Ako kontrolor radi
                    {
                        workMinutes += _slotDurationMinutes;
                    }
                }

                controllerWorkload[controllerId] = workMinutes;
            }

            // Izračunaj maksimalnu razliku u radnom vremenu kontrolora
            if (controllerWorkload.Count > 0)
            {
                int maxWorkload = controllerWorkload.Values.Max();
                int minWorkload = controllerWorkload.Values.Min();

                stats.MaxWorkHourDifference = (double)(maxWorkload - minWorkload) / 60.0; // Konvertuj u sate
            }

            // Izračunaj poštovanje pravila o pauzama (25% radnog vremena mora biti odmor)
            double totalRestTime = 0;
            double totalTime = solution.NumTimeSlots * _slotDurationMinutes * solution.NumControllers;

            for (int c = 0; c < solution.NumControllers; c++)
            {
                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    if (solution.Assignments[c, t, 0] == "111") // Ako kontrolor odmara
                    {
                        totalRestTime += _slotDurationMinutes;
                    }
                }
            }

            double actualRestPercentage = (totalRestTime / totalTime) * 100;
            double targetRestPercentage = 25.0; // 25% vremena treba da bude odmor

            stats.BreakCompliance = Math.Min(100, (actualRestPercentage / targetRestPercentage) * 100);

            // Izračunaj poštovanje pravila o rotaciji pozicija (balans između E i P pozicija)
            Dictionary<string, (int ExecutiveTime, int PlannerTime)> positionBalance = new Dictionary<string, (int, int)>();

            for (int c = 0; c < solution.NumControllers; c++)
            {
                string controllerId = controllers[c];
                int executiveTime = 0;
                int plannerTime = 0;

                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    string assignment = solution.Assignments[c, t, 0];

                    if (assignment != "111") // Ako kontrolor radi
                    {
                        string position = assignment.Substring(assignment.Length - 1);

                        if (position == "E")
                        {
                            executiveTime += _slotDurationMinutes;
                        }
                        else if (position == "P")
                        {
                            plannerTime += _slotDurationMinutes;
                        }
                    }
                }

                positionBalance[controllerId] = (executiveTime, plannerTime);
            }

            // Izračunaj procenat kontrolora koji imaju dobar balans između E i P pozicija (40-60%)
            int controllersWithGoodBalance = 0;

            foreach (var kvp in positionBalance)
            {
                int totalWorkTime = kvp.Value.ExecutiveTime + kvp.Value.PlannerTime;

                if (totalWorkTime > 0)
                {
                    double executivePercentage = (double)kvp.Value.ExecutiveTime / totalWorkTime * 100;

                    if (executivePercentage >= 40 && executivePercentage <= 60)
                    {
                        controllersWithGoodBalance++;
                    }
                }
            }

            if (positionBalance.Count > 0)
            {
                stats.RotationCompliance = (double)controllersWithGoodBalance / positionBalance.Count * 100;
            }

            // Izračunaj broj kontrolora sa manjkom radnih sati
            int minRequiredWorkMinutes = (int)(solution.NumTimeSlots * _slotDurationMinutes * 0.75 / solution.NumControllers);

            foreach (var kvp in controllerWorkload)
            {
                if (kvp.Value < minRequiredWorkMinutes)
                {
                    stats.EmployeesWithShortage++;
                }
            }

            // Izračunaj broj nedostajućih izvršilaca
            stats.MissingExecutors = CalculateMissingExecutors(solution, timeSlots);

            // Postavi status rešenja i vreme izvršavanja
            stats.SolutionStatus = stats.SlotsWithShortage == 0 ? "Optimal" : "Feasible";
            stats.WallTime = 0; // Ovo će biti postavljeno kasnije

            return stats;
        }


        private int CalculateMissingExecutors(Solution solution, List<DateTime> timeSlots)
        {
            int maxMissingExecutors = 0;

            // Za svaki vremenski slot
            for (int t = 0; t < solution.NumTimeSlots; t++)
            {
                DateTime slotTime = timeSlots[t];

                // Dobavi potrebne sektore za ovaj slot
                var requiredSectors = _configurations.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= slotTime &&
                        row.Field<DateTime>("datumDo") > slotTime)
                    .Select(row => row.Field<string>("sektor"))
                    .ToList();

                // Broj potrebnih kontrolora (1 po sektoru)
                int requiredControllers = requiredSectors.Count;

                // Broj stvarno dodeljenih kontrolora
                int assignedControllers = 0;
                for (int c = 0; c < solution.NumControllers; c++)
                {
                    if (solution.Assignments[c, t, 0] != "111") // Ako kontrolor radi
                    {
                        assignedControllers++;
                    }
                }

                // Izračunaj broj nedostajućih kontrolora za ovaj slot
                int missingSectorExecutors = Math.Max(0, requiredControllers - assignedControllers);

                // Ažuriraj maksimum
                maxMissingExecutors = Math.Max(maxMissingExecutors, missingSectorExecutors);
            }

            return maxMissingExecutors;
        }

        private Dictionary<string, string> BuildConfigurationLabels(List<DateTime> timeSlots)
        {
            var configLabels = new Dictionary<string, string>();

            // Za svaki vremenski slot
            foreach (var slotStart in timeSlots)
            {
                DateTime slotEnd = slotStart.AddMinutes(_slotDurationMinutes);
                string timeKey = $"{slotStart:yyyy-MM-dd HH:mm:ss}|{slotEnd:yyyy-MM-dd HH:mm:ss}";

                // Pronađi TX konfiguraciju
                var txConfig = _configurations.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= slotStart &&
                        row.Field<DateTime>("datumDo") > slotStart &&
                        row.Field<string>("ConfigType") == "TX")
                    .Select(row => row.Field<string>("Konfiguracija"))
                    .FirstOrDefault();

                // Pronađi LU konfiguraciju
                var luConfig = _configurations.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= slotStart &&
                        row.Field<DateTime>("datumDo") > slotStart &&
                        row.Field<string>("ConfigType") == "LU")
                    .Select(row => row.Field<string>("Konfiguracija"))
                    .FirstOrDefault();

                // Kreiraj labelu za slot
                string label = "";

                if (!string.IsNullOrEmpty(txConfig))
                {
                    label += $"TX:{txConfig}";
                }

                if (!string.IsNullOrEmpty(luConfig))
                {
                    if (!string.IsNullOrEmpty(label))
                    {
                        label += " | ";
                    }

                    label += $"LU:{luConfig}";
                }

                // Dodaj labelu u rečnik
                configLabels[timeKey] = label;
            }

            return configLabels;
        }

        private Dictionary<string, int> CalculateSlotShortages(Solution solution, List<string> controllers, List<DateTime> timeSlots)
        {
            var shortages = new Dictionary<string, int>();

            // Za svaki vremenski slot
            for (int t = 0; t < timeSlots.Count; t++)
            {
                DateTime slotStart = timeSlots[t];
                DateTime slotEnd = slotStart.AddMinutes(_slotDurationMinutes);
                string timeKey = $"{slotStart:yyyy-MM-dd HH:mm:ss}|{slotEnd:yyyy-MM-dd HH:mm:ss}";

                // Dobavi potrebne sektore za ovaj slot
                var requiredSectors = _configurations.AsEnumerable()
                    .Where(row =>
                        row.Field<DateTime>("datumOd") <= slotStart &&
                        row.Field<DateTime>("datumDo") > slotStart)
                    .Select(row => row.Field<string>("sektor"))
                    .ToList();

                // Broji pokrivene sektore
                var coveredSectors = new HashSet<string>();

                for (int c = 0; c < solution.NumControllers; c++)
                {
                    string assignment = solution.Assignments[c, t, 0];

                    if (assignment != "111") // Ako kontrolor radi
                    {
                        coveredSectors.Add(assignment);
                    }
                }

                // Izračunaj broj nepokrivenih sektora
                int shortage = 0;

                foreach (var sector in requiredSectors)
                {
                    if (!coveredSectors.Contains(sector))
                    {
                        shortage++;
                    }
                }

                // Dodaj manjak u rečnik ako postoji
                if (shortage > 0)
                {
                    shortages[timeKey] = shortage;
                }
            }

            return shortages;
        }

        private void EnsureAllControllersAssigned(Solution solution)
        {
            _logger.LogInformation("Ensuring all controllers are assigned to at least one sector");

            // Proveri koji kontrolori nisu raspoređeni
            for (int c = 0; c < solution.NumControllers; c++)
            {
                bool isAssigned = false;

                // Proveri da li kontrolor ima barem jedno zaduženje
                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    if (solution.Assignments[c, t, 0] != "111")
                    {
                        isAssigned = true;
                        break;
                    }
                }

                // Ako kontrolor nije raspoređen, pronađi mu zaduženje
                if (!isAssigned)
                {
                    _logger.LogWarning($"Controller {c} not assigned, attempting to find assignment");

                    // Pokušaj da ga rasporediš na nepokriveni sektor
                    bool assignmentFound = false;

                    // Prvo, pronađi nepokrivene sektore
                    // Prvo, pronađi nepokrivene sektore
                    for (int t = 0; t < solution.NumTimeSlots && !assignmentFound; t++)
                    {
                        DateTime slotTime = _timeSlots[t];

                        // Dobavi potrebne sektore za ovaj slot
                        var requiredSectors = _configurations.AsEnumerable()
                            .Where(row =>
                                row.Field<DateTime>("datumOd") <= slotTime &&
                                row.Field<DateTime>("datumDo") > slotTime)
                            .Select(row => row.Field<string>("sektor"))
                            .ToList();

                        // Proveri koji sektori su već pokriveni
                        var coveredSectors = new HashSet<string>();

                        for (int otherC = 0; otherC < solution.NumControllers; otherC++)
                        {
                            if (solution.Assignments[otherC, t, 0] != "111")
                            {
                                coveredSectors.Add(solution.Assignments[otherC, t, 0]);
                            }
                        }

                        // Pronađi nepokrivene sektore
                        var uncoveredSectors = requiredSectors.Except(coveredSectors).ToList();

                        if (uncoveredSectors.Any())
                        {
                            // Dodeli kontroloru nepokriveni sektor - sektor već uključuje poziciju (E/P)
                            solution.Assignments[c, t, 0] = uncoveredSectors.First()!;
                            _logger.LogInformation($"Assigned controller {c} to uncovered sector {uncoveredSectors.First()} at time slot {t}");
                            assignmentFound = true;
                        }
                    }

                    // Ako nema nepokrivenih sektora, zameni sa drugim kontrolorom
                    if (!assignmentFound)
                    {
                        _logger.LogWarning($"No uncovered sectors found for controller {c}, attempting to reassign from another controller");

                        // Pronađi kontrolora sa najviše zaduženja
                        int maxAssignedController = -1;
                        int maxAssignments = 0;

                        for (int otherC = 0; otherC < solution.NumControllers; otherC++)
                        {
                            if (otherC == c) continue;

                            int assignments = 0;
                            for (int t = 0; t < solution.NumTimeSlots; t++)
                            {
                                if (solution.Assignments[otherC, t, 0] != "111")
                                {
                                    assignments++;
                                }
                            }

                            if (assignments > maxAssignments)
                            {
                                maxAssignments = assignments;
                                maxAssignedController = otherC;
                            }
                        }

                        // Ako smo pronašli kontrolora sa više zaduženja, preuzmemo jedno
                        if (maxAssignedController != -1 && maxAssignments > 1)
                        {
                            // Pronađi prvo zaduženje koje možemo preuzeti
                            for (int t = 0; t < solution.NumTimeSlots && !assignmentFound; t++)
                            {
                                if (solution.Assignments[maxAssignedController, t, 0] != "111")
                                {
                                    // Preuzmi zaduženje
                                    solution.Assignments[c, t, 0] = solution.Assignments[maxAssignedController, t, 0];
                                    solution.Assignments[maxAssignedController, t, 0] = "111";

                                    _logger.LogInformation($"Reassigned controller {c} to take over sector {solution.Assignments[c, t, 0]} at time slot {t} from controller {maxAssignedController}");
                                    assignmentFound = true;
                                }
                            }
                        }

                        // Ako i dalje nismo uspeli rasporediti kontrolora, pridruži ga bilo kojem sektoru
                        if (!assignmentFound)
                        {
                            _logger.LogWarning($"Last resort assignment for controller {c}");

                            for (int t = 0; t < solution.NumTimeSlots && !assignmentFound; t++)
                            {
                                DateTime slotTime = _timeSlots[t];

                                // Dobavi bilo koji aktivni sektor u ovom slotu
                                var anySector = _configurations.AsEnumerable()
                                    .Where(row =>
                                        row.Field<DateTime>("datumOd") <= slotTime &&
                                        row.Field<DateTime>("datumDo") > slotTime)
                                    .Select(row => row.Field<string>("sektor"))
                                    .FirstOrDefault();

                                if (!string.IsNullOrEmpty(anySector))
                                {
                                    // Dodeli kontroloru ovaj sektor, čak i ako je već pokriven
                                    solution.Assignments[c, t, 0] = anySector;
                                    _logger.LogInformation($"Last resort: Assigned controller {c} to sector {anySector} at time slot {t}");
                                    assignmentFound = true;
                                }
                            }
                        }

                        // Ako i dalje nismo uspeli rasporediti kontrolora, to je zaista problem
                        if (!assignmentFound)
                        {
                            _logger.LogError($"Failed to assign controller {c} to any sector");
                        }
                    }
                }
            }
        }

        private List<InitialAssignmentDTO> BuildInitialAssignments(DataTable inicialniRaspored)
        {
            var initialAssignments = new List<InitialAssignmentDTO>();

            // Prolazak kroz inicijalni raspored
            foreach (DataRow row in inicialniRaspored.Rows)
            {
                initialAssignments.Add(new InitialAssignmentDTO
                {
                    Sifra = row.Field<string>("sifra") ?? "",
                    Smena = row.Field<string>("smena") ?? "",
                    Flag = row.Field<string>("Flag") ?? "",
                    DatumOd = row.Field<DateTime>("datumOd"),
                    DatumDo = row.Field<DateTime>("datumDo")
                });
            }

            return initialAssignments;
        }

        private (List<int> ssControllers, List<int> supControllers) IdentifySpecialControllers(List<string> controllers, DataTable inicialniRaspored)
        {
            var ssControllers = new List<int>();
            var supControllers = new List<int>();

            for (int i = 0; i < controllers.Count; i++)
            {
                var controllerData = inicialniRaspored.AsEnumerable()
                    .FirstOrDefault(row => row.Field<string>("sifra") == controllers[i]);

                if (controllerData != null)
                {
                    string orm = controllerData.Field<string>("ORM");
                    if (orm == "SS")
                        ssControllers.Add(i);
                    else if (orm == "SUP")
                        supControllers.Add(i);
                }
            }

            return (ssControllers, supControllers);
        }

        private bool CheckSectorContinuity(Solution solution)
        {
            for (int c = 0; c < solution.NumControllers; c++)
            {
                string currentSectorBase = null;

                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    string assignment = solution.Assignments[c, t, 0];

                    if (assignment != "111") // Ako kontrolor radi
                    {
                        string baseSector = assignment.Substring(0, assignment.Length - 1);

                        if (currentSectorBase != null && baseSector != currentSectorBase)
                        {
                            // Pronađena promena sektora bez pauze
                            return false;
                        }

                        currentSectorBase = baseSector;
                    }
                    else // Pauza
                    {
                        currentSectorBase = null;
                    }
                }
            }

            return true;
        }

        private void FixSectorContinuity(Solution solution)
        {
            for (int c = 0; c < solution.NumControllers; c++)
            {
                string currentBaseSector = null;
                int sectorStartSlot = -1;

                for (int t = 0; t < solution.NumTimeSlots; t++)
                {
                    string assignment = solution.Assignments[c, t, 0];

                    if (assignment != "111") // Kontrolor radi
                    {
                        string baseSector = assignment.Substring(0, assignment.Length - 1);
                        string position = assignment.Substring(assignment.Length - 1);

                        if (currentBaseSector == null)
                        {
                            // Početak novog radnog bloka
                            currentBaseSector = baseSector;
                            sectorStartSlot = t;
                        }
                        else if (baseSector != currentBaseSector)
                        {
                            // Promena sektora bez pauze - postaviti na pauzu
                            _logger.LogDebug($"Fixing sector continuity for controller {c}: switching from {currentBaseSector} to {baseSector} at slot {t}");
                            solution.Assignments[c, t, 0] = "111";
                            currentBaseSector = null;
                            sectorStartSlot = -1;
                        }
                    }
                    else // Pauza
                    {
                        currentBaseSector = null;
                        sectorStartSlot = -1;
                    }
                }
            }
        }
    }
}