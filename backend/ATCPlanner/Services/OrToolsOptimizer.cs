using ATCPlanner.Data;
using ATCPlanner.Models;
using ATCPlanner.Utils;
using Google.OrTools.Sat;
using System.Data;

namespace ATCPlanner.Services
{
    /// <summary>
    /// Implementacija optimizatora rasporeda koristeći Google OR-Tools CP-SAT solver.
    /// Optimizacija se vrši prema sledećim prioritetima iz dokumentacije:
    /// 1. Kontrolor letenja se može raspoređivati na sektor u trajanju od: 30 min, 1 sat, 1.5 sat, 2 sata
    /// 2. Pauza u radu mora biti najmanje 30 min nakon 30min-1.5h rada, odnosno 1 sat nakon 2h rada
    /// 3. Planiranje pauza od 1 sata između dva rada kao optimalni model opterećenja-odmora
    /// 4. Rotacija između planer (P) i executive (E) pozicija radi smanjenja broja primopredaja
    /// 5. Rotacija kontrolora po radnim mestima sa sektora višeg nivoa opterećenja na sektor nižeg nivoa
    /// 6. Raspored se popunjava uvažavajući istoriju planiranja
    /// 7. Kontrolor koji je započeo smenu treba da bude slobodan u poslednjem satu
    /// 8. Flag="S" periodi označavaju vreme kada je kontrolor oslobođen dužnosti
    /// 9. SS i SUP ne rade u istom vremenskom slotu, SUP radi na manje opterećenim sektorima
    /// 10. SS radi samo kada sistem pokaže nedovoljan broj kontrolora
    /// </summary>
    
    public class OrToolsOptimizer(ILogger logger, DataTableFilter dataTableFilter, DatabaseHandler databaseHandler, int slotDurationMinutes = 30)
    {
        private readonly ILogger _logger = logger;
        private readonly DataTableFilter _dataTableFilter = dataTableFilter;
        private readonly DatabaseHandler _databaseHandler = databaseHandler;
        private readonly int _slotDurationMinutes = slotDurationMinutes;

        private DataTable? _konfiguracije;
        private List<string>? _controllersWithLicense;

        // Struktura za čuvanje originalnog rasporeda
        private Dictionary<(int controller, int slot), string> _originalAssignments = new Dictionary<(int controller, int slot), string>();

        // Ograničenja za model prema PRIORITETU #1 i #2
        private const int MIN_WORK_BLOCK = 1; // min 1 slot (30 min) rada (PRIORITET #1)

        // Penali za različite dužine radnih blokova (PRIORITET #1)
        private const int BLOCK_30MIN_PENALTY = 100; // Visok penal za blokove od 30min
        private const int BLOCK_1HOUR_PENALTY = 50;  // Srednji penal za blokove od 1h
        private const int BLOCK_15HOUR_BONUS = -30;  // Bonus za blokove od 1.5h
        private const int BLOCK_2HOUR_BONUS = -30;   // Bonus za blokove od 2h

        private const int UNCOVERED_SECTOR_PENALTY = 50000000; // Izuzetno visok penal

        // Definišemo rečnik za praćenje varijabli za kratke pauze
        readonly Dictionary<(int, int), IntVar> shortBreakVars = [];
        readonly Dictionary<(int, int, string), IntVar> shouldWorkOnEVars = [];
        private Dictionary<(int, string), IntVar> shouldCoverNightShift = new Dictionary<(int, string), IntVar>();

        List<int> nightShiftControllers = new List<int>();
        List<int> ssControllers = new List<int>();
        List<int> supControllers = new List<int>();
        private HashSet<int> nightShiftSlots = new HashSet<int>();
        private Dictionary<(int, string), IntVar> nightShiftMissedOpportunities = new Dictionary<(int, string), IntVar>();
        private Dictionary<(int, int), IntVar> nightShiftLongBreaks = new Dictionary<(int, int), IntVar>();
        private Dictionary<(int, int), IntVar> nightShiftLongWorkPeriods = new Dictionary<(int, int), IntVar>();
        private IntVar nightShiftWorkloadDifference;
        private Dictionary<(int, string), IntVar> nightShiftSectorTypeCoverage = new Dictionary<(int, string), IntVar>();
        private Dictionary<(int, int), IntVar> nightShiftConsecutiveWork = new Dictionary<(int, int), IntVar>();

        private Dictionary<(int controller, int timeSlot), IntVar> preferredWorkBlocks = new Dictionary<(int, int), IntVar>();
        private Dictionary<(int controller, int timeSlot), IntVar> fragmentedWorkPenalties = new Dictionary<(int, int), IntVar>();

        public async Task<OptimizationResponse> OptimizeRosterWithOrTools(string smena, DateTime datum, DataTable konfiguracije, DataTable inicijalniRaspored, List<DateTime> timeSlots,
            int maxExecTime, int? maxOptimalSolution, int? maxZeroShortageSlots, List<string>? selectedOperativeWorkplaces, List<string>? selectedEmployees, bool useManualAssignments = true,
            int? randomSeed = null, bool useRandomization = true)
        {
            try
            {
                _logger.LogInformation("Starting OR-Tools optimization");

                _konfiguracije = konfiguracije;

                // Učitaj kontrolore sa vazecom dozvolom
                _controllersWithLicense = await _databaseHandler.GetControllersWithLicenseAsync();
                _logger.LogInformation($"Loaded {_controllersWithLicense.Count} controllers with active license");


                // datumi moraju biti u DateTime formatu
                TimeUtils.ConvertDatesToDateTime(konfiguracije, "datumOd", "datumDo");
                TimeUtils.ConvertDatesToDateTime(inicijalniRaspored, "datumOd", "datumDo", "VremeStart");

                // Log broja manuelnih dodela pre filtriranja
                int manualAssignmentsBeforeFiltering = inicijalniRaspored.AsEnumerable().Count(row => !string.IsNullOrEmpty(row.Field<string>("sektor")));
                _logger.LogInformation($"Initial schedule contains {manualAssignmentsBeforeFiltering} manual assignments");


                // filtriraj ako je potrebno
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

                // Log broja manuelnih dodela posle filtriranja
                int manualAssignmentsAfterFiltering = inicijalniRaspored.AsEnumerable().Count(row => !string.IsNullOrEmpty(row.Field<string>("sektor")));

                _logger.LogInformation($"After filtering, schedule contains {manualAssignmentsAfterFiltering} manual assignments");

                // uzmi listu kl
                var controllers = selectedEmployees != null && selectedEmployees.Count > 0 ? selectedEmployees : inicijalniRaspored.AsEnumerable()
                    .Select(row => row.Field<string>("sifra")!).Distinct().ToList();
                _logger.LogInformation("Number of controllers: {Count}", controllers.Count);

                // uzmi lustu svih sektorskih konfiguracija za svaki vremenski slot
                var sectorConfigurations = this.LoadSectorConfigurations(konfiguracije, timeSlots);
                _logger.LogInformation("Loaded {Count} sector configurations", sectorConfigurations.Count);

                // uzmi podatke o kl (pocetna vremena, smene...)
                var controllerInfo = await this.GetControllerInfoAsync(controllers, inicijalniRaspored, datum);

                // uzmi listu svih sektora potrebnih za svaki time slot
                var requiredSectors = this.GetRequiredSectors(konfiguracije, timeSlots);

                // kreiraj i resi model
                var model = new CpModel();

                // kreiraj varijable odlucivanja za model
                var assignments = this.CreateAssignmentVariables(model, controllers, timeSlots, requiredSectors);

                // dodaj ogranicenja u model
                this.AddConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, inicijalniRaspored, useManualAssignments);

                // definisanje funkcije cilja
                var objective = this.DefineObjective(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, inicijalniRaspored);
                model.Minimize(objective);

                // resavanje modela
                _logger.LogInformation("Solving CP-SAT model");
                //var solver = new CpSolver
                //{
                //    StringParameters = $"max_time_in_seconds:{maxExecTime};log_search_progress:true"
                //};
                var solver = new CpSolver();

                // Dodaj randomizaciju
                string solverParams = $"max_time_in_seconds:{maxExecTime};log_search_progress:true";

                if (useRandomization)
                {
                    int seed = randomSeed ?? new Random().Next(1000000);
                    solverParams += $";random_seed:{seed}";
                    _logger.LogInformation($"Using random seed: {seed}");
                }
                solver.StringParameters = solverParams;
                // callback za pracenje progressa
                //int progressCounter = 0;
                //var callback = new Models.SolutionCallback(
                //            _logger,
                //            maxOptimalSolution,
                //            maxZeroShortageSlots,
                //            progress => progressCounter = progress,
                //            controllers,         
                //            timeSlots,           
                //            requiredSectors,     
                //            assignments          
                //);

                this.AnalyzeCoverageBeforeSolving(assignments, controllers, timeSlots, requiredSectors, controllerInfo, inicijalniRaspored);
                var status = solver.Solve(model);
             //   _logger.LogInformation("CP-SAT solver status: {Status}, found {Solutions} solutions", status, callback.SolutionsFound);

                var optimizationResponse = this.CreateOptimizationResponse(solver, assignments, status, controllers, timeSlots, requiredSectors, controllerInfo, inicijalniRaspored, datum);

                return optimizationResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occured in OR-Tools optimization");
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

        private List<SectorConfiguration> LoadSectorConfigurations(DataTable konfiguracije, List<DateTime> timeSlots)
        {
            var configurations = new List<SectorConfiguration>();

            foreach (var timeSlot in timeSlots)
            {
                var txConfigs = konfiguracije.AsEnumerable()
                    .Where(row => row.Field<DateTime>("datumOd") <= timeSlot && row.Field<DateTime>("datumDo") > timeSlot && row.Field<string>("ConfigType") == "TX")
                    .Select(row => row.Field<string>("Konfiguracija"))
                    .Distinct().ToList();

                var luConfigs = konfiguracije.AsEnumerable()
                    .Where(row => row.Field<DateTime>("datumOd") <= timeSlot && row.Field<DateTime>("datumDo") > timeSlot && row.Field<string>("ConfigType") == "LU")
                    .Select(row => row.Field<string>("Konfiguracija"))
                    .Distinct().ToList();

                var allConfigs = konfiguracije.AsEnumerable()
                    .Where(row => row.Field<DateTime>("datumOd") <= timeSlot && row.Field<DateTime>("datumDo") > timeSlot && row.Field<string>("ConfigType") == "ALL")
                    .Select(row => row.Field<string>("Konfiguracija"))
                    .Distinct().ToList();

                // TX konfiguracije
                foreach (var txConfig in txConfigs)
                {
                    var sectors = this.GetSectorsForConfiguration(txConfig!, "TX", konfiguracije, timeSlot);
                    if (sectors.Count != 0)
                    {
                        configurations.Add(new SectorConfiguration
                        {
                            ConfigCode = txConfig!,
                            Start = timeSlot,
                            End = timeSlot.AddMinutes(_slotDurationMinutes),
                            Type = "TX",
                            Sectors = sectors
                        });
                    }
                }

                // LU konfiguracije
                foreach (var luConfig in luConfigs)
                {
                    var sectors = this.GetSectorsForConfiguration(luConfig!, "LU", konfiguracije, timeSlot);
                    if (sectors.Count != 0)
                    {
                        configurations.Add(new SectorConfiguration
                        {
                            ConfigCode = luConfig!,
                            Start = timeSlot,
                            End = timeSlot.AddMinutes(_slotDurationMinutes),
                            Type = "LU",
                            Sectors = sectors
                        });
                    }
                }

                // ALL konfiguracije
                foreach (var allConfig in allConfigs)
                {
                    var sectors = this.GetSectorsForConfiguration(allConfig!, "ALL", konfiguracije, timeSlot);
                    if (sectors.Count != 0)
                    {
                        configurations.Add(new SectorConfiguration
                        {
                            ConfigCode = allConfig!,
                            Start = timeSlot,
                            End = timeSlot.AddMinutes(_slotDurationMinutes),
                            Type = "ALL",
                            Sectors = sectors
                        });
                    }
                }
            }

            return configurations;
        }

        private List<string> GetSectorsForConfiguration(string configCode, string configType, DataTable konfiguracije, DateTime timeSlot)
        {
            var configRows = konfiguracije.AsEnumerable().Where(row => row.Field<DateTime>("datumOd") <= timeSlot && row.Field<DateTime>("datumDo") > timeSlot &&
                            row.Field<string>("ConfigType") == configType && row.Field<string>("Konfiguracija") == configCode).ToList();

            _logger.LogDebug($"Found {configRows.Count} rows for config {configCode} type {configType} at time {timeSlot}");

            var sectors = configRows.Select(row => row.Field<string>("sektor")).Where(s => !string.IsNullOrEmpty(s)).ToList();

            return sectors!;
        }

        private async Task<Dictionary<string, ControllerInfo>> GetControllerInfoAsync(List<string> controllers, DataTable inicijalniRaspored, DateTime datum)
        {
            var controllerInfo = new Dictionary<string, ControllerInfo>();
            var controllersWithLicense = await _databaseHandler.GetControllersWithLicenseAsync();

            foreach (var controllerCode in controllers)
            {
                var controllerRows = inicijalniRaspored.AsEnumerable().Where(row => row.Field<string>("sifra") == controllerCode).ToList();

                if (controllerRows.Count != 0)
                {
                    var firstRow = controllerRows.First();
                    string shiftType = firstRow.Field<string>("smena") ?? "";
                    string orm = firstRow.Field<string>("ORM") ?? "";

                    var (shiftStart, shiftEnd) = await _databaseHandler.GetSmenaDurationAsync(datum, shiftType);

                    if (shiftStart == null || shiftEnd == null)
                    {
                        _logger.LogWarning($"Could not determine shift duration for controller {controllerCode}, shift {shiftType}");
                        continue;
                    }

                     controllerInfo[controllerCode] = new ControllerInfo
                    {
                        Code = controllerCode,
                        Name = firstRow.Field<string>("PrezimeIme") ?? "",
                        ShiftType = firstRow.Field<string>("smena") ?? "",
                        ORM = firstRow.Field<string>("ORM") ?? "",
                        VremeStart = firstRow.Field<DateTime>("VremeStart"),
                        ShiftStart = shiftStart.Value,
                        ShiftEnd = shiftEnd.Value,
                        IsShiftLeader = (firstRow.Field<string>("ORM") ?? "") == "SS",
                        IsSupervisor = (firstRow.Field<string>("ORM") ?? "") == "SUP",
                        HasLicense = controllersWithLicense.Contains(controllerCode)
                     };

                    _logger.LogDebug($"Controller {controllerCode} shift: {shiftStart} to {shiftEnd}, type: {controllerInfo[controllerCode].ShiftType}");
                }
            }

            return controllerInfo;
        }

        private Dictionary<int, List<string>> GetRequiredSectors(DataTable konfiguracije, List<DateTime> timeSlots)
        {
            var requiredSectors = new Dictionary<int, List<string>>();

            for (int t = 0; t < timeSlots.Count; t++)
            {
                var timeSlot = timeSlots[t];
                var sectors = konfiguracije.AsEnumerable().Where(row => row.Field<DateTime>("datumOd") <= timeSlot && row.Field<DateTime>("datumDo") > timeSlot)
                    .Select(row => row.Field<string>("sektor"))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct().ToList();

                requiredSectors[t] = sectors!;
            }

            return requiredSectors;
        }

        private Dictionary<(int, int, string), IntVar> CreateAssignmentVariables(CpModel model, List<string> controllers, List<DateTime> timeSlots,
            Dictionary<int, List<string>> requiredSectors)
        {
            var assignments = new Dictionary<(int, int, string), IntVar>();

            // kreiraj varijable dodele: kontroler c, slot t, sektor s -> 1 ako je dodeljen, 0 ako nije
            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 0; t < timeSlots.Count; t++)
                {
                    // dodaj "break" sektor za svakog kontrolera u svakom slotu
                    assignments[(c, t, "break")] = model.NewBoolVar($"controller_{c}_slot_{t}_break");

                    // dodaj sektore za ovaj vremenski slot
                    foreach (var sector in requiredSectors[t])
                    {
                        assignments[(c, t, sector)] = model.NewBoolVar($"controller_{c}_slot_{t}_sector_{sector}");
                    }
                }
            }

            return assignments;
        }


        #region Manual Assignment Helper Methods

        /// <summary>
        /// Proverava da li kontrolor ima manuelnu dodelu u određenom vremenskom slotu
        /// </summary>
        private bool HasManualAssignment(int controllerIndex, int timeSlot,
            Dictionary<int, Dictionary<int, string>> manualAssignmentsByController)
        {
            return manualAssignmentsByController.ContainsKey(controllerIndex) &&
                   manualAssignmentsByController[controllerIndex].ContainsKey(timeSlot);
        }

        /// <summary>
        /// Proverava da li je kontrolor manuelno dodeljen specifičnom sektoru
        /// </summary>
        private bool IsManuallyAssignedToSector(int controllerIndex, int timeSlot, string sector,
            Dictionary<int, Dictionary<int, string>> manualAssignmentsByController)
        {
            if (!HasManualAssignment(controllerIndex, timeSlot, manualAssignmentsByController))
                return false;

            return manualAssignmentsByController[controllerIndex][timeSlot] == sector;
        }

        /// <summary>
        /// Proverava da li se dodela kontrolora može modifikovati (nema manuelnu dodelu)
        /// </summary>
        private bool CanModifyAssignment(int controllerIndex, int timeSlot,
            Dictionary<int, Dictionary<int, string>> manualAssignmentsByController)
        {
            return !HasManualAssignment(controllerIndex, timeSlot, manualAssignmentsByController);
        }

        /// <summary>
        /// Vraća manuelnu dodelu kontrolora za određeni slot, ili null ako ne postoji
        /// </summary>
        private string? GetManualAssignment(int controllerIndex, int timeSlot,
            Dictionary<int, Dictionary<int, string>> manualAssignmentsByController)
        {
            if (HasManualAssignment(controllerIndex, timeSlot, manualAssignmentsByController))
            {
                return manualAssignmentsByController[controllerIndex][timeSlot];
            }
            return null;
        }

        /// <summary>
        /// Proverava da li je sektor već manuelno dodeljen nekom kontroloru
        /// </summary>
        private bool IsSectorManuallyAssigned(int timeSlot, string sector,
            Dictionary<(int timeSlot, string sector), int> manualAssignmentMap)
        {
            return manualAssignmentMap.ContainsKey((timeSlot, sector));
        }

        /// <summary>
        /// Vraća indeks kontrolora koji je manuelno dodeljen sektoru
        /// </summary>
        private int? GetControllerAssignedToSector(int timeSlot, string sector,
            Dictionary<(int timeSlot, string sector), int> manualAssignmentMap)
        {
            if (manualAssignmentMap.TryGetValue((timeSlot, sector), out int controllerIndex))
            {
                return controllerIndex;
            }
            return null;
        }

        /// <summary>
        /// Kreira mapu manuelnih dodela po slotovima i sektorima
        /// </summary>
        private Dictionary<(int timeSlot, string sector), int> CreateManualAssignmentMap(
            List<(string controllerCode, int timeSlotIndex, string sector)> manualAssignments,
            List<string> controllers,
            Dictionary<int, List<string>> requiredSectors)
        {
            var map = new Dictionary<(int timeSlot, string sector), int>();

            foreach (var (controllerCode, timeSlotIndex, sector) in manualAssignments)
            {
                int controllerIndex = controllers.IndexOf(controllerCode);
                if (controllerIndex >= 0 && requiredSectors[timeSlotIndex].Contains(sector))
                {
                    map[(timeSlotIndex, sector)] = controllerIndex;
                }
            }

            return map;
        }

        /// <summary>
        /// Proverava da li postoji konflikt između manuelnih dodela i predloženog ograničenja
        /// </summary>
        private bool HasManualAssignmentConflict(int controllerIndex, int startSlot, int endSlot,
            string requiredState, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController, List<DateTime> timeSlots)
        {
            for (int t = startSlot; t <= endSlot && t < timeSlots.Count; t++)
            {
                if (HasManualAssignment(controllerIndex, t, manualAssignmentsByController))
                {
                    string assignment = manualAssignmentsByController[controllerIndex][t];

                    // Proveri da li je manuelna dodela u konfliktu sa zahtevom
                    if (requiredState == "break" && assignment != "break")
                    {
                        return true; // Konflikt: treba pauza ali je dodeljen sektoru
                    }
                    else if (requiredState == "work" && assignment == "break")
                    {
                        return true; // Konflikt: treba da radi ali je na pauzi
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Loguje sve manuelne dodele za debugging
        /// </summary>
        private void LogManualAssignments(
            Dictionary<int, Dictionary<int, string>> manualAssignmentsByController,
            List<string> controllers,
            List<DateTime> timeSlots)
        {
            _logger.LogInformation("=== Manual Assignments Summary ===");

            foreach (var (controllerIndex, assignments) in manualAssignmentsByController)
            {
                string controllerCode = controllers[controllerIndex];
                _logger.LogInformation($"Controller {controllerCode}:");

                foreach (var (timeSlotIndex, sector) in assignments.OrderBy(a => a.Key))
                {
                    DateTime slotTime = timeSlots[timeSlotIndex];
                    _logger.LogInformation($"  - Slot {timeSlotIndex} ({slotTime:HH:mm}): {sector}");
                }
            }

            _logger.LogInformation("=== End Manual Assignments ===");
        }

        #endregion

        private void AddSectorContinuityConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
            Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController,
        bool useManualAssignments)
        {
            _logger.LogInformation("Adding sector continuity constraints...");

            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 1; t < timeSlots.Count; t++) // pocinjemo od drugog slota
                {
                    var controller = controllerInfo[controllers[c]];

                    // proveri da li je kl u smeni u oba slota
                    bool inShiftPrev = IsInShift(controller, timeSlots[t - 1], t - 1, timeSlots.Count);
                    bool inShiftCurr = IsInShift(controller, timeSlots[t], t, timeSlots.Count);

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
                                string currBase = currAssignment.Length >= 2 ? prevAssignment.Substring(0, 2) : currAssignment;

                                if (prevBase != currBase)
                                {
                                    _logger.LogWarning($"Manual assignments violate sector continuity for controller {controllers[c]} " +
                                             $"at slots {t - 1}-{t}: {prevAssignment} -> {currAssignment}. " +
                                             $"Skipping continuity constraint.");
                                    continue;
                                }
                            }
                           
                        }
                        else if (hasManualPrev || hasManualCurr)  // ako je samo jedan slot manuelno dodeljen, dozvoli fleksibilnost
                        {
                            _logger.LogDebug($"Partial manual assignment for controller {controllers[c]} at slots {t - 1}-{t}. " +
                                   $"Applying relaxed continuity constraint.");

                            // Nastavi sa ograničenjem ali dozvoli promene gde nema manuelnih dodela
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
                                // proveri da li mozemo primeniti ogranicenje
                                bool canApplyConstraint = true;

                                if (useManualAssignments)
                                {
                                    // ako je kl manuelno dodeljen na prevSector u t-1 i currSector u t, ne mozemo primeniti ogranicenje
                                    bool isManualPrev = IsManuallyAssignedToSector(c, t - 1, prevSector, manualAssignmentsByController);
                                    bool isManualCurr = IsManuallyAssignedToSector(c, t, currSector, manualAssignmentsByController);

                                    if (isManualPrev && isManualCurr)
                                    {
                                        _logger.LogWarning($"Cannot apply sector continuity constraint for controller {controllers[c]} " +
                                                 $"due to manual assignments: {prevSector}@{t - 1} -> {currSector}@{t}");
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

                                    // Zabrani promenu sektora bez pauze između
                                    model.Add(workingConsecutive + sectorSwitch <= 1);
                                }
                            }
                        }
                    }
                }
            }
        }

        private void AddMaximumWorkingConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
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
                        if (!IsInShift(controller, timeSlots[t + i], t + i, timeSlots.Count))
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

                        // 1 ako kl radi u ovom slotu, 0 ako ne radi
                        var isWorking = model.NewBoolVar($"is_working_{c}_{t + i}");
                        if (sectorVars.Count != 0)
                        {
                            model.Add(LinearExpr.Sum(sectorVars) <= 1); // najvise jedan sektor po slotu
                            model.Add(LinearExpr.Sum(sectorVars) == isWorking); // radi ako je dodeljen sektoru
                        }
                        else
                        {
                            model.Add(isWorking == 0); // ako nema sektora, ne radi
                        }
                        workVars.Add(isWorking);
                    }

                    // ako kl radi 4 slota uzastopno, mora imati pauzu u petom slotu
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

        private void AddBreakConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
        Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController,
       bool useManualAssignments)
        {
            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 0; t < timeSlots.Count - 1; t++) // Razmatramo svaki mogući trenutak prelaska na pauzu
                {
                    var controller = controllerInfo[controllers[c]];

                    // Preskačemo ako kontrolor nije u smeni
                    bool inShiftCurrent = IsInShift(controller, timeSlots[t], t, timeSlots.Count);
                    bool inShiftNext = t + 1 < timeSlots.Count && IsInShift(controller, timeSlots[t + 1], t + 1, timeSlots.Count);

                    if (!inShiftCurrent || !inShiftNext)
                        continue;

                    // Detektujemo da li kontrolor radi u trenutnom slotu
                    var workingAtT = model.NewBoolVar($"working_at_{c}_{t}");
                    model.Add(assignments[(c, t, "break")] == 0).OnlyEnforceIf(workingAtT);
                    model.Add(assignments[(c, t, "break")] == 1).OnlyEnforceIf(workingAtT.Not());

                    // Detektujemo da li kontrolor ima pauzu u sledećem slotu
                    var pauseAtTPlus1 = model.NewBoolVar($"pause_at_{c}_{t + 1}");
                    model.Add(assignments[(c, t + 1, "break")] == 1).OnlyEnforceIf(pauseAtTPlus1);
                    model.Add(assignments[(c, t + 1, "break")] == 0).OnlyEnforceIf(pauseAtTPlus1.Not());

                    // Detektujemo prelaz iz rada u pauzu (rad u t, pauza u t+1)
                    var transitionToPause = model.NewBoolVar($"transition_to_pause_{c}_{t}");
                    model.Add(workingAtT + pauseAtTPlus1 == 2).OnlyEnforceIf(transitionToPause);
                    model.Add(workingAtT + pauseAtTPlus1 < 2).OnlyEnforceIf(transitionToPause.Not());

                    // Računamo dužinu rada pre pauze (gledamo unazad)

                    // Provera za blok od 4 slota (120 min) - zahteva 2 slota pauze (60 min)
                    if (t >= 3) // Potrebna su 3 prethodna slota + trenutni
                    {
                        // Proveravamo da li je kontrolor u smeni u svim razmatranim slotovima
                        bool allInShift = true;
                        for (int i = 1; i <= 3; i++)
                        {
                            if (!IsInShift(controller, timeSlots[t - i], t - i, timeSlots.Count))
                            {
                                allInShift = false;
                                break;
                            }
                        }

                        if (allInShift)
                        {
                            // Proveravamo da li je radio u 3 prethodna slota
                            var previousWorkVars = new List<IntVar>();
                            for (int i = 1; i <= 3; i++)
                            {
                                var prevWorkingVar = model.NewBoolVar($"prev_working_{c}_{t - i}");
                                model.Add(assignments[(c, t - i, "break")] == 0).OnlyEnforceIf(prevWorkingVar);
                                model.Add(assignments[(c, t - i, "break")] == 1).OnlyEnforceIf(prevWorkingVar.Not());
                                previousWorkVars.Add(prevWorkingVar);
                            }

                            // Varijabla koja je 1 ako je radio u sva 3 prethodna slota
                            var worked3PrevSlots = model.NewBoolVar($"worked_3_prev_slots_{c}_{t}");
                            model.Add(LinearExpr.Sum(previousWorkVars) == 3).OnlyEnforceIf(worked3PrevSlots);
                            model.Add(LinearExpr.Sum(previousWorkVars) < 3).OnlyEnforceIf(worked3PrevSlots.Not());

                            // Ako je radio 4 slota ukupno (3 prethodna + trenutni) i prelazi na pauzu,
                            // onda je to kraj bloka od 120 min i zahteva pauzu od 60 min (2 slota)
                            var longWorkBlock = model.NewBoolVar($"long_work_block_{c}_{t}");
                            model.Add(worked3PrevSlots + workingAtT + pauseAtTPlus1 == 3).OnlyEnforceIf(longWorkBlock);
                            model.Add(worked3PrevSlots + workingAtT + pauseAtTPlus1 < 3).OnlyEnforceIf(longWorkBlock.Not());

                            // Osiguravamo da ima pauzu od min 2 slota (60 min)
                            if (t + 2 < timeSlots.Count && IsInShift(controller, timeSlots[t + 2], t + 2, timeSlots.Count))
                            {
                                model.Add(assignments[(c, t + 2, "break")] == 1).OnlyEnforceIf(longWorkBlock);
                            }
                        }
                    }

                    // Provera za kraće blokove (1-3 slota, tj. 30-90 min) - zahteva min 1 slot pauze (30 min)
                    // Već je osigurano kroz detektovanje prelaza na pauzu (pauseAtTPlus1)

                    // Za sve ostale slotove, najmanja pauza je 30 min (1 slot), što je već
                    // osigurano detekcijom transitionToPause varijable
                }
            }

            // Dodatna provera za blok od 4 slota (120 min) - posmatrajući 4 uzastopna slota
            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 0; t <= timeSlots.Count - 4; t++)
                {
                    var controller = controllerInfo[controllers[c]];

                    // Provera da li je kontrolor u smeni u svim slotovima bloka
                    bool allInShift = true;
                    for (int i = 0; i < 4; i++)
                    {
                        if (!IsInShift(controller, timeSlots[t + i], t + i, timeSlots.Count))
                        {
                            allInShift = false;
                            break;
                        }
                    }

                    if (!allInShift)
                        continue;

                    // Proveravamo da li kontrolor radi u 4 uzastopna slota
                    var workVars = new List<IntVar>();
                    for (int i = 0; i < 4; i++)
                    {
                        var isWorkingVar = model.NewBoolVar($"is_working_4_block_{c}_{t + i}");
                        model.Add(assignments[(c, t + i, "break")] == 0).OnlyEnforceIf(isWorkingVar);
                        model.Add(assignments[(c, t + i, "break")] == 1).OnlyEnforceIf(isWorkingVar.Not());
                        workVars.Add(isWorkingVar);
                    }

                    // Varijabla koja je 1 ako kontrolor radi u sva 4 slota
                    var works4Slots = model.NewBoolVar($"works_4_slots_{c}_{t}");
                    model.Add(LinearExpr.Sum(workVars) == 4).OnlyEnforceIf(works4Slots);
                    model.Add(LinearExpr.Sum(workVars) < 4).OnlyEnforceIf(works4Slots.Not());

                    // Ako radi 4 slota (120 min), osiguravamo pauzu od min 60 min (2 slota) nakon toga
                    if (t + 5 < timeSlots.Count &&
                        IsInShift(controller, timeSlots[t + 4], t + 4, timeSlots.Count) &&
                        IsInShift(controller, timeSlots[t + 5], t + 5, timeSlots.Count))
                    {
                        model.Add(assignments[(c, t + 4, "break")] + assignments[(c, t + 5, "break")] >= 2)
                             .OnlyEnforceIf(works4Slots);
                    }
                    else if (t + 4 < timeSlots.Count &&
                            IsInShift(controller, timeSlots[t + 4], t + 4, timeSlots.Count))
                    {
                        // Ako je dostupan samo jedan slot nakon rada, osiguramo bar taj jedan
                        model.Add(assignments[(c, t + 4, "break")] == 1).OnlyEnforceIf(works4Slots);
                    }
                }
            }
        }

        private void AddMinimumWorkBlockConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
            Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController)
        {
            _logger.LogInformation("Adding minimum work block constraints...");

            const int MIN_WORK_BLOCK = 1; // Minimum 30 minuta (1 slot)
            const int PREFERRED_WORK_BLOCK = 4; // Preferirano 2 sata (4 slota) ako je moguće

            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                for (int t = 0; t < timeSlots.Count - 1; t++)
                {
                    // Proveri da li je kontrolor u smeni
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count) ||
                        !IsInShift(controller, timeSlots[t + 1], t + 1, timeSlots.Count))
                        continue;

                    // Provera da li je kontrolor na pauzi u slotu t i radi na t+1 (početak rada)
                    var onBreakT = assignments[(c, t, "break")];

                    // Promenljiva da kontrolor radi na t+1
                    var workingT1 = model.NewBoolVar($"working_{c}_{t + 1}");

                    // Pokupi sve sektorske promenljive za vreme t+1
                    var sectorVarsT1 = new List<IntVar>();
                    foreach (var sector in requiredSectors[t + 1])
                    {
                        sectorVarsT1.Add(assignments[(c, t + 1, sector)]);
                    }

                    // Definiši workingT1 kao 1 ako je bilo koja sektorska varijabla 1
                    if (sectorVarsT1.Count != 0)
                    {
                        model.Add(LinearExpr.Sum(sectorVarsT1) >= 1).OnlyEnforceIf(workingT1);
                        model.Add(LinearExpr.Sum(sectorVarsT1) == 0).OnlyEnforceIf(workingT1.Not());
                    }
                    else
                    {
                        // Nema sektora na t+1, kontrolor je na pauzi
                        model.Add(workingT1 == 0);
                    }

                    // startingWork=true ako je onBreakT=1 and workingT1=1
                    var startingWork = model.NewBoolVar($"starting_work_{c}_{t + 1}");
                    model.Add(onBreakT + workingT1 >= 2).OnlyEnforceIf(startingWork);
                    model.Add(onBreakT + workingT1 <= 1).OnlyEnforceIf(startingWork.Not());

                    // PROVERA MANUELNIH DODELA
                    // Prvo proveri da li postoje manuelne dodele koje bi sprečile minimalni blok
                    bool canEnforceMinBlock = true;
                    List<int> manualBreakSlots = new List<int>();

                    // Proveri minimalni blok (1 slot)
                    for (int len = 0; len < MIN_WORK_BLOCK && t + 1 + len < timeSlots.Count; len++)
                    {
                        if (!IsInShift(controller, timeSlots[t + 1 + len], t + 1 + len, timeSlots.Count))
                        {
                            canEnforceMinBlock = false;
                            break;
                        }

                        string? manualAssignment = GetManualAssignment(c, t + 1 + len, manualAssignmentsByController);
                        if (manualAssignment == "break")
                        {
                            manualBreakSlots.Add(t + 1 + len);
                            canEnforceMinBlock = false;
                        }
                    }

                    if (!canEnforceMinBlock && manualBreakSlots.Count > 0)
                    {
                        _logger.LogDebug($"Cannot enforce minimum work block for controller {controllers[c]} " +
                                       $"starting at slot {t + 1} due to manual break assignments at slots: " +
                                       $"{string.Join(", ", manualBreakSlots)}");
                        continue;
                    }

                    // Ako je početak rada, osiguraj minimum dužinu bloka (1 slot)
                    if (canEnforceMinBlock)
                    {
                        for (int len = 0; len < MIN_WORK_BLOCK && t + 1 + len < timeSlots.Count; len++)
                        {
                            var futureBreak = assignments[(c, t + 1 + len, "break")];
                            model.Add(futureBreak == 0).OnlyEnforceIf(startingWork);
                        }
                    }

                    // PREFERIRANI BLOK OD 4 SLOTA (soft constraint)
                    // Proveri da li je moguće da kontrolor radi 4 slota
                    bool canWork4Slots = true;
                    bool hasManualAssignmentsIn4Slots = false;

                    for (int i = 0; i < PREFERRED_WORK_BLOCK; i++)
                    {
                        if (t + 1 + i >= timeSlots.Count ||
                            !IsInShift(controller, timeSlots[t + 1 + i], t + 1 + i, timeSlots.Count))
                        {
                            canWork4Slots = false;
                            break;
                        }

                        string? manual = GetManualAssignment(c, t + 1 + i, manualAssignmentsByController);
                        if (manual != null)
                        {
                            hasManualAssignmentsIn4Slots = true;
                            if (manual == "break")
                            {
                                canWork4Slots = false;
                                break;
                            }
                        }
                    }

                    // Proveri da li može imati pauzu od 2 slota nakon 4 slota rada
                    bool canHave2SlotBreakAfter = true;
                    if (canWork4Slots)
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            int breakSlot = t + 1 + PREFERRED_WORK_BLOCK + i;
                            if (breakSlot >= timeSlots.Count ||
                                !IsInShift(controller, timeSlots[breakSlot], breakSlot, timeSlots.Count))
                            {
                                canHave2SlotBreakAfter = false;
                                break;
                            }

                            string? manual = GetManualAssignment(c, breakSlot, manualAssignmentsByController);
                            if (manual != null && manual != "break")
                            {
                                canHave2SlotBreakAfter = false;
                                break;
                            }
                        }
                    }

                    // Ako može raditi 4 slota i imati pauzu nakon toga, dodaj soft preferenciju
                    if (canWork4Slots && canHave2SlotBreakAfter && !hasManualAssignmentsIn4Slots)
                    {
                        // Kreiraj varijablu koja označava da li kontrolor radi tačno 4 slota
                        var works4Slots = model.NewBoolVar($"prefers_4_slots_{c}_{t + 1}");

                        // Varijable za praćenje rada u naredna 4 slota
                        var next4WorkVars = new List<IntVar>();
                        for (int i = 0; i < PREFERRED_WORK_BLOCK; i++)
                        {
                            var isWorking = model.NewBoolVar($"is_working_pref_{c}_{t + 1 + i}");
                            model.Add(assignments[(c, t + 1 + i, "break")] == 0).OnlyEnforceIf(isWorking);
                            model.Add(assignments[(c, t + 1 + i, "break")] == 1).OnlyEnforceIf(isWorking.Not());
                            next4WorkVars.Add(isWorking);
                        }

                        // works4Slots = 1 ako radi sva 4 slota
                        model.Add(LinearExpr.Sum(next4WorkVars) == PREFERRED_WORK_BLOCK).OnlyEnforceIf(works4Slots);
                        model.Add(LinearExpr.Sum(next4WorkVars) < PREFERRED_WORK_BLOCK).OnlyEnforceIf(works4Slots.Not());

                        // Ako počinje rad i radi 4 slota, preferiraj ovu opciju
                        var prefers4SlotBlock = model.NewBoolVar($"prefers_4_slot_block_{c}_{t + 1}");
                        model.Add(startingWork == 1).OnlyEnforceIf(prefers4SlotBlock);
                        model.Add(works4Slots == 1).OnlyEnforceIf(prefers4SlotBlock);

                        // Ovo će biti korišćeno u funkciji cilja kao soft preferencija
                        _logger.LogDebug($"Controller {controllers[c]} can potentially work 4-slot block starting at {t + 1}");

                        // Čuvaj za funkciju cilja (nagrada za 4-slot blokove)
                        // preferredWorkBlocks[(c, t + 1)] = prefers4SlotBlock;
                    }
                }
            }

            // Dodatno: Sprečava fragmentaciju rada
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                for (int t = 1; t < timeSlots.Count - 1; t++)
                {
                    if (!IsInShift(controller, timeSlots[t - 1], t - 1, timeSlots.Count) ||
                        !IsInShift(controller, timeSlots[t], t, timeSlots.Count) ||
                        !IsInShift(controller, timeSlots[t + 1], t + 1, timeSlots.Count))
                        continue;

                    // Proveri pattern: rad-pauza-rad (što treba izbegavati)
                    bool hasManualT_1 = HasManualAssignment(c, t - 1, manualAssignmentsByController);
                    bool hasManualT = HasManualAssignment(c, t, manualAssignmentsByController);
                    bool hasManualT1 = HasManualAssignment(c, t + 1, manualAssignmentsByController);

                    // Ako su svi slotovi slobodni za optimizaciju
                    if (!hasManualT_1 && !hasManualT && !hasManualT1)
                    {
                        // Detektuj pattern rad-pauza-rad
                        var workingT_1 = model.NewBoolVar($"working_fragment_{c}_{t - 1}");
                        model.Add(assignments[(c, t - 1, "break")] == 0).OnlyEnforceIf(workingT_1);
                        model.Add(assignments[(c, t - 1, "break")] == 1).OnlyEnforceIf(workingT_1.Not());

                        var pauseT = model.NewBoolVar($"pause_fragment_{c}_{t}");
                        model.Add(assignments[(c, t, "break")] == 1).OnlyEnforceIf(pauseT);
                        model.Add(assignments[(c, t, "break")] == 0).OnlyEnforceIf(pauseT.Not());

                        var workingT1 = model.NewBoolVar($"working_fragment_{c}_{t + 1}");
                        model.Add(assignments[(c, t + 1, "break")] == 0).OnlyEnforceIf(workingT1);
                        model.Add(assignments[(c, t + 1, "break")] == 1).OnlyEnforceIf(workingT1.Not());

                        // Varijabla koja označava fragmentovan rad
                        var fragmentedWork = model.NewBoolVar($"fragmented_work_{c}_{t}");
                        model.Add(workingT_1 + pauseT + workingT1 == 3).OnlyEnforceIf(fragmentedWork);
                        model.Add(workingT_1 + pauseT + workingT1 < 3).OnlyEnforceIf(fragmentedWork.Not());

                        // Ovo će biti penalizovano u funkciji cilja
                        // fragmentedWorkPenalties[(c, t)] = fragmentedWork;
                    }
                }
            }
        }

        private void AddRotationConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
            Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController)
        {
            _logger.LogInformation("Adding E/P rotation constraints...");

            // Dictionary za čuvanje varijabli za funkciju cilja
            var rotationViolations = new Dictionary<(int, int, string), IntVar>();

            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 0; t < timeSlots.Count - 2; t++)
                {
                    var controller = controllerInfo[controllers[c]];

                    // Proveri da li je kontrolor u smeni za sve relevantne slotove
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count) ||
                        !IsInShift(controller, timeSlots[t + 1], t + 1, timeSlots.Count) ||
                        !IsInShift(controller, timeSlots[t + 2], t + 2, timeSlots.Count))
                        continue;

                    // Za svaki sektor sa E ili P oznakom
                    foreach (var sector in requiredSectors[t].Where(s => s.EndsWith("E") || s.EndsWith("P")))
                    {
                        string baseSector = sector[..^1];
                        string currentPosition = sector[^1..];
                        string alternativePosition = (currentPosition == "E") ? "P" : "E";
                        string alternativeSector = baseSector + alternativePosition;

                        // Proveri da li postoje oba sektora u narednim slotovima
                        if (!requiredSectors[t + 1].Contains(sector) ||
                            !requiredSectors[t + 2].Contains(alternativeSector))
                            continue;

                        // Proveri manuelne dodele
                        string? manualT = GetManualAssignment(c, t, manualAssignmentsByController);
                        string? manualT1 = GetManualAssignment(c, t + 1, manualAssignmentsByController);
                        string? manualT2 = GetManualAssignment(c, t + 2, manualAssignmentsByController);

                        // Ako su manuelno dodeljeni na način koji krši rotaciju, preskoči
                        if (manualT == sector && manualT1 == sector && manualT2 == sector)
                        {
                            _logger.LogWarning($"Controller {controllers[c]} manually assigned to same position " +
                                             $"{sector} for 3 consecutive slots starting at {t}. " +
                                             $"This violates rotation principle but will be respected.");
                            continue;
                        }

                        // Ako je kontrolor manuelno dodeljen na alternativnu poziciju u slotu t+2, to je dobro
                        if (manualT2 == alternativeSector)
                        {
                            _logger.LogDebug($"Controller {controllers[c]} has manual rotation to {alternativeSector} at slot {t + 2}");
                            continue;
                        }

                        // Primeni ograničenje rotacije samo ako nema konfliktnih manuelnih dodela
                        bool canApplyRotation = true;

                        // Ako je manuelno dodeljen na istu poziciju u t+2 nakon rada na istoj u t i t+1
                        if (manualT == sector && manualT1 == sector && manualT2 == sector)
                        {
                            canApplyRotation = false;
                        }

                        // Ako je manuelno dodeljen na potpuno drugi sektor u t+2
                        if (manualT2 != null && manualT2 != alternativeSector && manualT2 != "break")
                        {
                            canApplyRotation = false;
                        }

                        if (canApplyRotation)
                        {
                            // Varijabla koja označava da kontrolor radi na istom sektoru 2 slota zaredom
                            var workingSameSectorTwoSlots = model.NewBoolVar($"working_same_{sector}_two_slots_{c}_{t}");

                            // Uslov 1: Kontrolor radi na istom sektoru u slotu t
                            model.Add(assignments[(c, t, sector)] == 1).OnlyEnforceIf(workingSameSectorTwoSlots);

                            // Uslov 2: Kontrolor radi na istom sektoru u slotu t+1
                            model.Add(assignments[(c, t + 1, sector)] == 1).OnlyEnforceIf(workingSameSectorTwoSlots);

                            // Proveri da li kontrolor nije na pauzi u slotu t+2
                            var notOnBreakTPlus2 = model.NewBoolVar($"not_on_break_{c}_{t + 2}");
                            model.Add(assignments[(c, t + 2, "break")] == 0).OnlyEnforceIf(notOnBreakTPlus2);
                            model.Add(assignments[(c, t + 2, "break")] == 1).OnlyEnforceIf(notOnBreakTPlus2.Not());

                            // Kontrolor treba da rotira ako je radio 2 slota na istoj poziciji i nije na pauzi
                            var shouldRotateAfterTwoSlots = model.NewBoolVar($"should_rotate_after_two_{c}_{t}_{sector}");
                            model.Add(workingSameSectorTwoSlots == 1).OnlyEnforceIf(shouldRotateAfterTwoSlots);
                            model.Add(notOnBreakTPlus2 == 1).OnlyEnforceIf(shouldRotateAfterTwoSlots);

                            // Proveri da li zaista rotira
                            var doesRotateToAlternative = model.NewBoolVar($"does_rotate_to_{alternativeSector}_{c}_{t + 2}");
                            model.Add(assignments[(c, t + 2, alternativeSector)] == 1).OnlyEnforceIf(doesRotateToAlternative);
                            model.Add(assignments[(c, t + 2, alternativeSector)] == 0).OnlyEnforceIf(doesRotateToAlternative.Not());

                            // Varijabla koja označava kršenje rotacije (soft constraint)
                            var rotationViolation = model.NewBoolVar($"rotation_violation_{c}_{t}_{baseSector}");

                            // rotationViolation = 1 ako treba da rotira ali ne rotira
                            model.Add(shouldRotateAfterTwoSlots == 1).OnlyEnforceIf(rotationViolation);
                            model.Add(doesRotateToAlternative == 0).OnlyEnforceIf(rotationViolation);

                            // Definiši kada nije kršenje
                            var notShouldRotate = model.NewBoolVar($"not_should_rotate_{c}_{t}_{baseSector}");
                            model.Add(shouldRotateAfterTwoSlots == 0).OnlyEnforceIf(notShouldRotate);
                            model.Add(shouldRotateAfterTwoSlots == 1).OnlyEnforceIf(notShouldRotate.Not());

                            model.AddBoolOr([notShouldRotate, doesRotateToAlternative]).OnlyEnforceIf(rotationViolation.Not());

                            // Čuvaj za funkciju cilja
                            rotationViolations[(c, t + 2, baseSector)] = rotationViolation;
                        }
                        else
                        {
                            _logger.LogDebug($"Skipping rotation constraint for controller {controllers[c]} " +
                                           $"at slots {t}-{t + 2} due to manual assignments");
                        }
                    }
                }
            }

            // Sačuvaj rotation violations za korišćenje u objective funkciji
            _logger.LogInformation($"Added {rotationViolations.Count} rotation tracking variables");
        }

        private void AddSupervisorShiftLeaderConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
            Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController)
        {
            _logger.LogInformation("Adding SS (Shift Leader) and SUP (Supervisor) constraints...");

            // Identifikuj SS i SUP kontrolore
            var ssControllers = new List<int>();
            var supControllers = new List<int>();

            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];
                if (controller.IsShiftLeader)
                {
                    ssControllers.Add(c);
                    _logger.LogDebug($"Identified SS controller: {controllers[c]}");
                }
                else if (controller.IsSupervisor)
                {
                    supControllers.Add(c);
                    _logger.LogDebug($"Identified SUP controller: {controllers[c]}");
                }
            }

            _logger.LogInformation($"Found {ssControllers.Count} SS controllers and {supControllers.Count} SUP controllers");

            // Za svaki vremenski slot
            for (int t = 0; t < timeSlots.Count; t++)
            {
                // Proveri manuelne dodele SS i SUP kontrolora za ovaj slot
                var manuallyWorkingSS = new List<(int controller, string sector)>();
                var manuallyWorkingSUP = new List<(int controller, string sector)>();

                // Proveri SS kontrolore
                foreach (int ssC in ssControllers)
                {
                    if (!IsInShift(controllerInfo[controllers[ssC]], timeSlots[t], t, timeSlots.Count))
                        continue;

                    string? manualAssignment = GetManualAssignment(ssC, t, manualAssignmentsByController);
                    if (manualAssignment != null && manualAssignment != "break" && requiredSectors[t].Contains(manualAssignment))
                    {
                        manuallyWorkingSS.Add((ssC, manualAssignment));
                    }
                }

                // Proveri SUP kontrolore
                foreach (int supC in supControllers)
                {
                    if (!IsInShift(controllerInfo[controllers[supC]], timeSlots[t], t, timeSlots.Count))
                        continue;

                    string? manualAssignment = GetManualAssignment(supC, t, manualAssignmentsByController);
                    if (manualAssignment != null && manualAssignment != "break" && requiredSectors[t].Contains(manualAssignment))
                    {
                        manuallyWorkingSUP.Add((supC, manualAssignment));
                    }
                }

                // Proveri konflikt u manuelnim dodelama
                if (manuallyWorkingSS.Count > 0 && manuallyWorkingSUP.Count > 0)
                {
                    _logger.LogError($"CONFLICT at slot {t} ({timeSlots[t]:HH:mm}): " +
                                   $"Both SS and SUP manually assigned to work! " +
                                   $"SS: {string.Join(", ", manuallyWorkingSS.Select(x => $"{controllers[x.controller]} on {x.sector}"))} " +
                                   $"SUP: {string.Join(", ", manuallyWorkingSUP.Select(x => $"{controllers[x.controller]} on {x.sector}"))}");

                    // U slučaju konflikta, možemo dodati soft constraint ili prijaviti grešku
                    // Za sada ćemo dozvoliti ali dodati veliki penal u funkciju cilja
                    continue;
                }

                // Kreiraj varijable za praćenje rada SS i SUP kontrolora
                var ssWorkingVars = new List<IntVar>();
                var supWorkingVars = new List<IntVar>();

                // SS kontrolori
                foreach (int ssC in ssControllers)
                {
                    if (!IsInShift(controllerInfo[controllers[ssC]], timeSlots[t], t, timeSlots.Count))
                        continue;

                    var ssIsWorking = model.NewBoolVar($"ss_{ssC}_working_{t}");
                    var sectorVars = new List<IntVar>();

                    foreach (var sector in requiredSectors[t])
                    {
                        sectorVars.Add(assignments[(ssC, t, sector)]);
                    }

                    if (sectorVars.Count > 0)
                    {
                        model.Add(LinearExpr.Sum(sectorVars) >= 1).OnlyEnforceIf(ssIsWorking);
                        model.Add(LinearExpr.Sum(sectorVars) == 0).OnlyEnforceIf(ssIsWorking.Not());
                        ssWorkingVars.Add(ssIsWorking);
                    }
                }

                // SUP kontrolori
                foreach (int supC in supControllers)
                {
                    if (!IsInShift(controllerInfo[controllers[supC]], timeSlots[t], t, timeSlots.Count))
                        continue;

                    var supIsWorking = model.NewBoolVar($"sup_{supC}_working_{t}");
                    var sectorVars = new List<IntVar>();

                    foreach (var sector in requiredSectors[t])
                    {
                        sectorVars.Add(assignments[(supC, t, sector)]);
                    }

                    if (sectorVars.Count > 0)
                    {
                        model.Add(LinearExpr.Sum(sectorVars) >= 1).OnlyEnforceIf(supIsWorking);
                        model.Add(LinearExpr.Sum(sectorVars) == 0).OnlyEnforceIf(supIsWorking.Not());
                        supWorkingVars.Add(supIsWorking);
                    }
                }

                // KLJUČNO OGRANIČENJE: Maksimalno jedan od SS ili SUP može raditi u isto vreme
                // Ali samo ako nemamo konfliktne manuelne dodele
                if (ssWorkingVars.Count > 0 && supWorkingVars.Count > 0)
                {
                    var allSpecialControllers = new List<IntVar>();
                    allSpecialControllers.AddRange(ssWorkingVars);
                    allSpecialControllers.AddRange(supWorkingVars);

                    model.Add(LinearExpr.Sum(allSpecialControllers) <= 1);

                    _logger.LogDebug($"Added SS/SUP mutual exclusion for slot {t}: " +
                                   $"{ssWorkingVars.Count} SS vars, {supWorkingVars.Count} SUP vars");
                }
                else if (ssWorkingVars.Count > 1)
                {
                    // Ako ima više SS kontrolora, maksimalno jedan može raditi
                    model.Add(LinearExpr.Sum(ssWorkingVars) <= 1);
                }
                else if (supWorkingVars.Count > 1)
                {
                    // Ako ima više SUP kontrolora, maksimalno jedan može raditi
                    model.Add(LinearExpr.Sum(supWorkingVars) <= 1);
                }

                // Dodatno: SUP preferirano radi na manje opterećenim sektorima
                // Ovo implementiramo kroz funkciju cilja kasnije
            }

            // Pravilo 10: SS radi samo kada sistem pokaže nedovoljan broj kontrolora
            // Ovo ćemo implementirati kroz funkciju cilja - SS će imati penal za rad
            // osim ako nije potreban da pokrije sektor

            for (int t = 0; t < timeSlots.Count; t++)
            {
                foreach (var sector in requiredSectors[t])
                {
                    // Proveri da li je sektor pokriven običnim kontrolorima
                    var regularControllerAssignments = new List<IntVar>();

                    for (int c = 0; c < controllers.Count; c++)
                    {
                        var controller = controllerInfo[controllers[c]];

                        // Preskoči SS i SUP kontrolore
                        if (controller.IsShiftLeader || controller.IsSupervisor)
                            continue;

                        if (IsInShift(controller, timeSlots[t], t, timeSlots.Count))
                        {
                            regularControllerAssignments.Add(assignments[(c, t, sector)]);
                        }
                    }

                    // Varijabla koja označava da li je sektor pokriven običnim kontrolorima
                    if (regularControllerAssignments.Count > 0)
                    {
                        var sectorCoveredByRegular = model.NewBoolVar($"sector_covered_by_regular_{t}_{sector}");
                        model.Add(LinearExpr.Sum(regularControllerAssignments) >= 1).OnlyEnforceIf(sectorCoveredByRegular);
                        model.Add(LinearExpr.Sum(regularControllerAssignments) == 0).OnlyEnforceIf(sectorCoveredByRegular.Not());

                        // Ako sektor nije pokriven običnim kontrolorima, SS ili SUP mogu da pomognu
                        // Ali preferiraj SUP pre SS (kroz funkciju cilja)

                        // Čuvamo ovu informaciju za funkciju cilja
                        _logger.LogDebug($"Tracking coverage for sector {sector} at slot {t}");
                    }
                }
            }
        }

        private void AddConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
                 Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo,     DataTable inicijalniRaspored, bool useManualAssignments = true)
        {
            // Korak 1: Identifikuj i organizuj manuelne dodele
            var manualAssignments = IdentifyManualAssignments(inicijalniRaspored, controllers, timeSlots);

            // Kreiraj strukture podataka za lakše rukovanje manuelnim dodelama
            var manualAssignmentsByController = new Dictionary<int, Dictionary<int, string>>();
            var manualAssignmentMap = new Dictionary<(int timeSlot, string sector), int>();

            foreach (var (controllerCode, timeSlotIndex, sector) in manualAssignments)
            {
                int controllerIndex = controllers.IndexOf(controllerCode);
                if (controllerIndex < 0) continue;

                // Popuni manualAssignmentsByController
                if (!manualAssignmentsByController.ContainsKey(controllerIndex))
                {
                    manualAssignmentsByController[controllerIndex] = new Dictionary<int, string>();
                }
                manualAssignmentsByController[controllerIndex][timeSlotIndex] = sector;

                // Popuni manualAssignmentMap
                if (requiredSectors[timeSlotIndex].Contains(sector))
                {
                    manualAssignmentMap[(timeSlotIndex, sector)] = controllerIndex;
                }
            }

            _logger.LogInformation($"Identified {manualAssignments.Count} manual assignments. UseManualAssignments: {useManualAssignments}");

            if (useManualAssignments)
            {
                LogManualAssignments(manualAssignmentsByController, controllers, timeSlots);

                // Korak 2: Primeni manuelne dodele kao čvrsta ograničenja
                foreach (var (controllerCode, timeSlotIndex, sector) in manualAssignments)
                {
                    int controllerIndex = controllers.IndexOf(controllerCode);
                    if (controllerIndex < 0) continue;

                    // Proveri da li je sektor validan za ovaj slot
                    if (!requiredSectors[timeSlotIndex].Contains(sector) && sector != "break")
                    {
                        _logger.LogWarning($"Manual assignment for controller {controllerCode} at slot {timeSlotIndex} " +
                                         $"has invalid sector {sector}. Will not enforce this assignment.");
                        continue;
                    }

                    _logger.LogInformation($"Enforcing manual assignment: Controller {controllerCode} at slot {timeSlotIndex} " +
                                          $"on sector {sector}");

                    if (sector == "break")
                    {
                        // Kontrolor je na pauzi
                        model.Add(assignments[(controllerIndex, timeSlotIndex, "break")] == 1);
                        // Ne sme biti dodeljen nijednom sektoru
                        foreach (var otherSector in requiredSectors[timeSlotIndex])
                        {
                            model.Add(assignments[(controllerIndex, timeSlotIndex, otherSector)] == 0);
                        }
                    }
                    else
                    {
                        // Kontrolor je dodeljen sektoru
                        model.Add(assignments[(controllerIndex, timeSlotIndex, sector)] == 1);
                        model.Add(assignments[(controllerIndex, timeSlotIndex, "break")] == 0);

                        // Ne sme biti dodeljen drugim sektorima
                        foreach (var otherSector in requiredSectors[timeSlotIndex])
                        {
                            if (otherSector != sector)
                            {
                                model.Add(assignments[(controllerIndex, timeSlotIndex, otherSector)] == 0);
                            }
                        }
                    }
                }
            }

            // 1. OSNOVNO: Svaki sektor ima najviše jednog kontrolora
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

            // 2. OSNOVNO: Svaki kontrolor je dodeljen tačno jednom sektoru ili pauzi
            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 0; t < timeSlots.Count; t++)
                {
                    var controller = controllerInfo[controllers[c]];
                    bool inShift = IsInShift(controller, timeSlots[t], t, timeSlots.Count);

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
                        //Proveri Flag = "S"
                        bool isFlagS = this.IsFlagS(controllers[c], timeSlots[t], inicijalniRaspored);
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

            // 3. PRIORITET #4: Kontinuitet sektora (sa pravilnim rukovanjem manuelnih dodela)
            AddSectorContinuityConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController, useManualAssignments);
            AddMaximumWorkingConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController, useManualAssignments);
            AddBreakConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController, useManualAssignments);
            AddMinimumWorkBlockConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController);
            //AddRotationConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController);
            AddSupervisorShiftLeaderConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController);

        }

        //private void AddConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
        //    Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, DataTable inicijalniRaspored, bool useManualAssignments = true)
        //{
        //    var manualAssignments = IdentifyManualAssignments(inicijalniRaspored, controllers, timeSlots);

        //    // Logujemo informaciju o manuelnim dodelama i parametru
        //    _logger.LogInformation($"Identified {manualAssignments.Count} manual assignments from initial schedule. UseManualAssignments: {useManualAssignments}");

        //    if (useManualAssignments)
        //    {

        //        var sectorAssignmentsDict = new Dictionary<(int, string), List<string>>();

        //        foreach (var (controllerCode, timeSlotIndex, sector) in manualAssignments)
        //        {
        //            var key = (timeSlotIndex, sector);
        //            if (!sectorAssignmentsDict.ContainsKey(key))
        //            {
        //                sectorAssignmentsDict[key] = new List<string>();
        //            }
        //            sectorAssignmentsDict[key].Add(controllerCode);
        //        }

        //        // Loguj upozorenje za konflikte
        //        foreach (var entry in sectorAssignmentsDict)
        //        {
        //            if (entry.Value.Count > 1)
        //            {
        //                _logger.LogWarning($"Conflict in manual assignments: {entry.Value.Count} controllers " +
        //                                  $"({string.Join(", ", entry.Value)}) assigned to sector {entry.Key.Item2} " +
        //                                  $"at time slot {timeSlots[entry.Key.Item1]}");
        //            }
        //        }


        //        _logger.LogInformation($"Identified {manualAssignments.Count} manual assignments from initial schedule");

        //        // ogranicenja za manuelne dodele
        //        foreach (var (controllerCode, timeSlotIndex, sector) in manualAssignments)
        //        {
        //            int controllerIndex = controllers.IndexOf(controllerCode);
        //            if (controllerIndex < 0) continue;

        //            // proveri da li je sektor validan za ovaj slot
        //            if (!requiredSectors[timeSlotIndex].Contains(sector))
        //            {
        //                _logger.LogWarning($"Manual assignment for controller {controllerCode} at slot {timeSlotIndex} " +
        //                          $"has invalid sector {sector}. Will not enforce this assignment.");
        //                continue;
        //            }

        //            _logger.LogInformation($"Enforcing manual assignment: Controller {controllerCode} at slot {timeSlotIndex} " + $"on sector {sector}");

        //            // postavi ogranicenje da je kontrolor dodeljen ovom sektoru
        //            model.Add(assignments[(controllerIndex, timeSlotIndex, sector)] == 1);

        //            // osiguraj da nije na pauzi
        //            model.Add(assignments[(controllerIndex, timeSlotIndex, "break")] == 0);

        //            // osiguraj da nije dodeljen drugim sektorima
        //            foreach (var otherSector in requiredSectors[timeSlotIndex])
        //            {
        //                if (otherSector != sector)
        //                {
        //                    model.Add(assignments[(controllerIndex, timeSlotIndex, otherSector)] == 0);
        //                }
        //            }
        //        }
        //    }
        //    else
        //    {
        //        _logger.LogInformation("Manual assignments will be ignored as requested.");
        //    }

        //    // 1. Svaki sektor ima najviše jednog kontrolora
        //    // Za svaki vremenski slot
        //    for (int t = 0; t < timeSlots.Count; t++)
        //    {
        //        // Za svaki sektor u konfiguraciji za taj slot
        //        foreach (var sector in requiredSectors[t])
        //        {
        //            // Prikupi varijable za sve kontrolore koji bi mogli raditi na ovom sektoru
        //            var sectorAssignments = new List<IntVar>();
        //            for (int c = 0; c < controllers.Count; c++)
        //            {
        //                sectorAssignments.Add(assignments[(c, t, sector)]);
        //            }

        //            // Osiguraj da je najviše jedan kontrolor dodeljen ovom sektoru
        //            model.Add(LinearExpr.Sum(sectorAssignments) <= 1);
        //        }
        //    }


        //    // 2. Svaki kontrolor je dodeljen tačno jednom sektoru (ili odmoru) u svakom slotu
        //    // (Ovo je osnovno ograničenje za pravilno formiranje rasporeda)
        //    for (int c = 0; c < controllers.Count; c++)
        //    {
        //        for (int t = 0; t < timeSlots.Count; t++)
        //        {
        //            // uzmi pocetak rada kl i proveri da li je tada u smeni
        //            var controller = controllerInfo[controllers[c]];
        //            DateTime slotTime = timeSlots[t];

        //            bool inShift = IsInShift(controller, timeSlots[t], t, timeSlots.Count);

        //            // ako nije u smeni, onda je na pauzi (break)
        //            if (!inShift)
        //            {
        //                model.Add(assignments[(c, t, "break")] == 1);
        //                // i ne sme biti dodeljen nijednom sektoru
        //                foreach (var sector in requiredSectors[t])
        //                {
        //                    model.Add(assignments[(c, t, sector)] == 0);
        //                }
        //            }
        //            else
        //            {
        //                // provera da li kl ima Flag="S" za ovaj slot (onda je na pauzi)
        //                bool isFlagS = this.IsFlagS(controllers[c], timeSlots[t], inicijalniRaspored);
        //                if (isFlagS)
        //                {
        //                    model.Add(assignments[(c, t, "break")] == 1);
        //                    foreach (var sector in requiredSectors[t])
        //                    {
        //                        model.Add(assignments[(c, t, sector)] == 0);
        //                    }
        //                }
        //                else
        //                {
        //                    // kl moze biti dodeljen samo jednom sektoru ili pauzi
        //                    var taskVars = new List<IntVar> { assignments[(c, t, "break")] };
        //                    taskVars.AddRange(requiredSectors[t].Select(s => assignments[(c, t, s)]));
        //                    model.Add(LinearExpr.Sum(taskVars) == 1);
        //                }
        //            }
        //        }
        //    }


//            // 3. PRIORITET #4: Kontinuitet sektora - kontrolor treba da ostane na istom sektoru
//            // (Smanjuje broj istovremenih primopredaja)
//            // Novo rešenje koje prati OR-Tools sintaksu
//            if (!useManualAssignments)
//            {
//                for (int c = 0; c<controllers.Count; c++)
//                {
//                    for (int t = 1; t<timeSlots.Count; t++) // Počinjemo od drugog slota
//                    {
//                        var controller = controllerInfo[controllers[c]];

//        // Proveri da li je kontrolor u smeni u oba slota
//        bool inShiftPrev = IsInShift(controller, timeSlots[t - 1], t - 1, timeSlots.Count);
//        bool inShiftCurr = IsInShift(controller, timeSlots[t], t, timeSlots.Count);

//                        if (!inShiftPrev || !inShiftCurr)
//                            continue;

//                        // Varijabla koja je 1 ako kontrolor NE odmara u prethodnom slotu
//                        var workingPrev = model.NewBoolVar($"working_prev_{c}_{t - 1}");
//        model.Add(assignments[(c, t - 1, "break")] == 0).OnlyEnforceIf(workingPrev);
//        model.Add(assignments[(c, t - 1, "break")] == 1).OnlyEnforceIf(workingPrev.Not());

//        // Varijabla koja je 1 ako kontrolor NE odmara u trenutnom slotu
//        var workingCurr = model.NewBoolVar($"working_curr_{c}_{t}");
//        model.Add(assignments[(c, t, "break")] == 0).OnlyEnforceIf(workingCurr);
//        model.Add(assignments[(c, t, "break")] == 1).OnlyEnforceIf(workingCurr.Not());

//        // Varijabla koja je 1 ako kontrolor radi u oba slota (nema pauzu)
//        var workingConsecutive = model.NewBoolVar($"working_consecutive_{c}_{t}");
//        model.Add(workingPrev == 1).OnlyEnforceIf(workingConsecutive);
//        model.Add(workingCurr == 1).OnlyEnforceIf(workingConsecutive);
//        model.Add(workingPrev + workingCurr< 2).OnlyEnforceIf(workingConsecutive.Not());

//                        // Za sve parove sektora u prethodnom i trenutnom slotu
//                        foreach (var prevSector in requiredSectors[t - 1])
//                        {
//                            string prevBaseSector = prevSector.Length >= 2 ? prevSector.Substring(0, 2) : prevSector;

//                            foreach (var currSector in requiredSectors[t])
//                            {
//                                string currBaseSector = currSector.Length >= 2 ? currSector.Substring(0, 2) : currSector;

//                                // Ako osnove sektora nisu iste
//                                if (prevBaseSector != currBaseSector)
//                                {
//                                    // Kreiraj varijablu koja je 1 ako kontrolor radi na prevSector u prethodnom slotu
//                                    var onPrevSector = model.NewBoolVar($"on_prev_sector_{c}_{t - 1}_{prevSector}");
//        model.Add(assignments[(c, t - 1, prevSector)] == 1).OnlyEnforceIf(onPrevSector);
//        model.Add(assignments[(c, t - 1, prevSector)] == 0).OnlyEnforceIf(onPrevSector.Not());

//        // Kreiraj varijablu koja je 1 ako kontrolor radi na currSector u trenutnom slotu
//        var onCurrSector = model.NewBoolVar($"on_curr_sector_{c}_{t}_{currSector}");
//        model.Add(assignments[(c, t, currSector)] == 1).OnlyEnforceIf(onCurrSector);
//        model.Add(assignments[(c, t, currSector)] == 0).OnlyEnforceIf(onCurrSector.Not());

//        // Varijabla koja je 1 ako kontrolor radi na različitim sektorima u dva uzastopna slota
//        var sectorSwitch = model.NewBoolVar($"sector_switch_{c}_{t}_{prevSector}_{currSector}");
//        model.Add(onPrevSector == 1).OnlyEnforceIf(sectorSwitch);
//        model.Add(onCurrSector == 1).OnlyEnforceIf(sectorSwitch);
//        model.Add(onPrevSector + onCurrSector< 2).OnlyEnforceIf(sectorSwitch.Not());

//        // Zabrani promenu sektora bez pauze između - ovo je strogo ograničenje
//        model.Add(workingConsecutive + sectorSwitch <= 1);
//                                }
//}
//                        }
//                    }
//                }
//            }
//            else
//{


//    // Identifikujemo vremenske slotove sa manuelnim dodelama
//    var manualAssignmentsByController = new Dictionary<int, Dictionary<int, string>>();

//    foreach (var (controllerCode, timeSlotIndex, sector) in manualAssignments)
//    {
//        int controllerIndex = controllers.IndexOf(controllerCode);
//        if (controllerIndex < 0) continue;

//        if (!manualAssignmentsByController.ContainsKey(controllerIndex))
//        {
//            manualAssignmentsByController[controllerIndex] = new Dictionary<int, string>();
//        }

//        manualAssignmentsByController[controllerIndex][timeSlotIndex] = sector;
//    }

//    // Za svakog kontrolora
//    for (int c = 0; c < controllers.Count; c++)
//    {
//        for (int t = 1; t < timeSlots.Count; t++) // Počinjemo od drugog slota
//        {
//            var controller = controllerInfo[controllers[c]];

//            // Proveri da li je kontrolor u smeni u oba slota
//            bool inShiftPrev = IsInShift(controller, timeSlots[t - 1], t - 1, timeSlots.Count);
//            bool inShiftCurr = IsInShift(controller, timeSlots[t], t, timeSlots.Count);

//            if (!inShiftPrev || !inShiftCurr)
//                continue;

//            // Proveri da li imamo manuelne dodele za ova dva slota
//            bool hasManualPrev = manualAssignmentsByController.ContainsKey(c) &&
//                                manualAssignmentsByController[c].ContainsKey(t - 1);
//            bool hasManualCurr = manualAssignmentsByController.ContainsKey(c) &&
//                                manualAssignmentsByController[c].ContainsKey(t);

//            // Ako imamo manuelne dodele za oba slota, proverimo da li su na istom sektoru
//            if (hasManualPrev && hasManualCurr)
//            {
//                string prevSector = manualAssignmentsByController[c][t - 1];
//                string currSector = manualAssignmentsByController[c][t];

//                string prevBaseSector = prevSector.Length >= 2 ? prevSector.Substring(0, 2) : prevSector;
//                string currBaseSector = currSector.Length >= 2 ? currSector.Substring(0, 2) : currSector;

//                // Ako su osnovni sektori različiti u manuelnim dodelama,
//                // preskačemo primenu ograničenja kontinuiteta za ovaj par slotova
//                if (prevBaseSector != currBaseSector)
//                {
//                    _logger.LogWarning($"Relaxing sector continuity constraint for controller {controllers[c]} " +
//                                     $"at slots {t - 1}-{t} due to manual assignments on different sectors: " +
//                                     $"{prevSector} -> {currSector}");
//                    continue;
//                }
//            }

//            // Ako imamo manuelne dodele, ali samo za jedan od slotova,
//            // takođe preskačemo ograničenje, jer želimo dati slobodu solveru
//            if (hasManualPrev || hasManualCurr)
//            {
//                _logger.LogInformation($"Relaxing sector continuity constraint for controller {controllers[c]} " +
//                                     $"at slots {t - 1}-{t} due to partial manual assignments");
//                continue;
//            }

//            // Uobičajeni kod za kontinuitet sektora kada nema manuelnih dodela
//            var workingPrev = model.NewBoolVar($"working_prev_{c}_{t - 1}");
//            model.Add(assignments[(c, t - 1, "break")] == 0).OnlyEnforceIf(workingPrev);
//            model.Add(assignments[(c, t - 1, "break")] == 1).OnlyEnforceIf(workingPrev.Not());

//            var workingCurr = model.NewBoolVar($"working_curr_{c}_{t}");
//            model.Add(assignments[(c, t, "break")] == 0).OnlyEnforceIf(workingCurr);
//            model.Add(assignments[(c, t, "break")] == 1).OnlyEnforceIf(workingCurr.Not());

//            var workingConsecutive = model.NewBoolVar($"working_consecutive_{c}_{t}");
//            model.Add(workingPrev == 1).OnlyEnforceIf(workingConsecutive);
//            model.Add(workingCurr == 1).OnlyEnforceIf(workingConsecutive);
//            model.Add(workingPrev + workingCurr < 2).OnlyEnforceIf(workingConsecutive.Not());

//            // Za sve parove sektora u prethodnom i trenutnom slotu
//            foreach (var prevSector in requiredSectors[t - 1])
//            {
//                string prevBaseSector = prevSector.Length >= 2 ? prevSector.Substring(0, 2) : prevSector;

//                foreach (var currSector in requiredSectors[t])
//                {
//                    string currBaseSector = currSector.Length >= 2 ? currSector.Substring(0, 2) : currSector;

//                    // Ako osnove sektora nisu iste
//                    if (prevBaseSector != currBaseSector)
//                    {
//                        var onPrevSector = model.NewBoolVar($"on_prev_sector_{c}_{t - 1}_{prevSector}");
//                        model.Add(assignments[(c, t - 1, prevSector)] == 1).OnlyEnforceIf(onPrevSector);
//                        model.Add(assignments[(c, t - 1, prevSector)] == 0).OnlyEnforceIf(onPrevSector.Not());

//                        var onCurrSector = model.NewBoolVar($"on_curr_sector_{c}_{t}_{currSector}");
//                        model.Add(assignments[(c, t, currSector)] == 1).OnlyEnforceIf(onCurrSector);
//                        model.Add(assignments[(c, t, currSector)] == 0).OnlyEnforceIf(onCurrSector.Not());

//                        var sectorSwitch = model.NewBoolVar($"sector_switch_{c}_{t}_{prevSector}_{currSector}");
//                        model.Add(onPrevSector == 1).OnlyEnforceIf(sectorSwitch);
//                        model.Add(onCurrSector == 1).OnlyEnforceIf(sectorSwitch);
//                        model.Add(onPrevSector + onCurrSector < 2).OnlyEnforceIf(sectorSwitch.Not());

//                        // Zabrani promenu sektora bez pauze između - ovo je strogo ograničenje
//                        model.Add(workingConsecutive + sectorSwitch <= 1);
//                    }
//                }
//            }
//        }
//    }
//}

//    // 4. PRIORITET #1 i #2: Kontrolor ne može raditi više od 2 sata bez pauze
//    for (int c = 0; c < controllers.Count; c++)
//    {
//        for (int t = 0; t <= timeSlots.Count - 4; t++)
//        {
//            var controller = controllerInfo[controllers[c]];

//            bool allInShift = true;
//            for (int i = 0; i < 4; i++)
//            {
//                if (!IsInShift(controller, timeSlots[t + i], t + i, timeSlots.Count))
//                {
//                    allInShift = false;
//                    break;
//                }
//            }

//            if (!allInShift)
//                continue;

//            var workVars = new List<IntVar>();
//            for (int i = 0; i < 4; i++)
//            {
//                var sectorVars = new List<IntVar>();
//                foreach (var sector in requiredSectors[t + i])
//                {
//                    sectorVars.Add(assignments[(c, t + i, sector)]);
//                }

//                // 1 ako kl radi u ovom slotu, 0 ako ne radi
//                var isWorking = model.NewBoolVar($"is_working_{c}_{t + i}");
//                if (sectorVars.Count != 0)
//                {
//                    model.Add(LinearExpr.Sum(sectorVars) <= 1); // najvise jedan sektor po slotu
//                    model.Add(LinearExpr.Sum(sectorVars) == isWorking); // radi ako je dodeljen sektoru
//                }
//                else
//                {
//                    model.Add(isWorking == 0); // ako nema sektora, ne radi
//                }
//                workVars.Add(isWorking);
//            }

//            // ako kl radi 4 slota uzastopno, mora imati pauzu u petom slotu
//            if (t + 4 < timeSlots.Count)
//            {
//                var allWorking = model.NewBoolVar($"all_working_{c}_{t}");
//                model.Add(LinearExpr.Sum(workVars) >= 4).OnlyEnforceIf(allWorking);
//                model.Add(LinearExpr.Sum(workVars) < 4).OnlyEnforceIf(allWorking.Not());

//                if (t + 4 < timeSlots.Count)
//                {
//                    model.Add(assignments[(c, t + 4, "break")] == 1).OnlyEnforceIf(allWorking);
//                }
//            }
//        }
//    }

//    // 5. PRIORITET #2: Nakon rada, pauza mora biti najmanje 30 min (1 slot)
//    for (int c = 0; c < controllers.Count; c++)
//    {
//        for (int t = 0; t < timeSlots.Count - 1; t++)
//        {
//            var wasWorking = model.NewBoolVar($"was_working_{c}_{t}");
//            var sectorVars = new List<IntVar>();
//            foreach (var sector in requiredSectors[t])
//            {
//                sectorVars.Add(assignments[(c, t, sector)]);
//            }

//            if (sectorVars.Count != 0)
//            {
//                model.Add(LinearExpr.Sum(sectorVars) > 0).OnlyEnforceIf(wasWorking);
//                model.Add(LinearExpr.Sum(sectorVars) == 0).OnlyEnforceIf(wasWorking.Not());
//            }
//            else
//            {
//                model.Add(wasWorking == 0);
//            }
//        }
//    }


//    // 6. PRIORITET #2: Pauze nakon rada
//    // - Nakon 30, 60, 90 min (1-3 slota) rada: pauza min 30 min (1 slot)
//    // - Nakon 120 min (4 slota) rada: pauza min 60 min (2 slota)
//    for (int c = 0; c < controllers.Count; c++)
//    {
//        for (int t = 0; t < timeSlots.Count - 1; t++) // Razmatramo svaki mogući trenutak prelaska na pauzu
//        {
//            var controller = controllerInfo[controllers[c]];

//            // Preskačemo ako kontrolor nije u smeni
//            bool inShiftCurrent = IsInShift(controller, timeSlots[t], t, timeSlots.Count);
//            bool inShiftNext = t + 1 < timeSlots.Count && IsInShift(controller, timeSlots[t + 1], t + 1, timeSlots.Count);

//            if (!inShiftCurrent || !inShiftNext)
//                continue;

//            // Detektujemo da li kontrolor radi u trenutnom slotu
//            var workingAtT = model.NewBoolVar($"working_at_{c}_{t}");
//            model.Add(assignments[(c, t, "break")] == 0).OnlyEnforceIf(workingAtT);
//            model.Add(assignments[(c, t, "break")] == 1).OnlyEnforceIf(workingAtT.Not());

//            // Detektujemo da li kontrolor ima pauzu u sledećem slotu
//            var pauseAtTPlus1 = model.NewBoolVar($"pause_at_{c}_{t + 1}");
//            model.Add(assignments[(c, t + 1, "break")] == 1).OnlyEnforceIf(pauseAtTPlus1);
//            model.Add(assignments[(c, t + 1, "break")] == 0).OnlyEnforceIf(pauseAtTPlus1.Not());

//            // Detektujemo prelaz iz rada u pauzu (rad u t, pauza u t+1)
//            var transitionToPause = model.NewBoolVar($"transition_to_pause_{c}_{t}");
//            model.Add(workingAtT + pauseAtTPlus1 == 2).OnlyEnforceIf(transitionToPause);
//            model.Add(workingAtT + pauseAtTPlus1 < 2).OnlyEnforceIf(transitionToPause.Not());

//            // Računamo dužinu rada pre pauze (gledamo unazad)

//            // Provera za blok od 4 slota (120 min) - zahteva 2 slota pauze (60 min)
//            if (t >= 3) // Potrebna su 3 prethodna slota + trenutni
//            {
//                // Proveravamo da li je kontrolor u smeni u svim razmatranim slotovima
//                bool allInShift = true;
//                for (int i = 1; i <= 3; i++)
//                {
//                    if (!IsInShift(controller, timeSlots[t - i], t - i, timeSlots.Count))
//                    {
//                        allInShift = false;
//                        break;
//                    }
//                }

//                if (allInShift)
//                {
//                    // Proveravamo da li je radio u 3 prethodna slota
//                    var previousWorkVars = new List<IntVar>();
//                    for (int i = 1; i <= 3; i++)
//                    {
//                        var prevWorkingVar = model.NewBoolVar($"prev_working_{c}_{t - i}");
//                        model.Add(assignments[(c, t - i, "break")] == 0).OnlyEnforceIf(prevWorkingVar);
//                        model.Add(assignments[(c, t - i, "break")] == 1).OnlyEnforceIf(prevWorkingVar.Not());
//                        previousWorkVars.Add(prevWorkingVar);
//                    }

//                    // Varijabla koja je 1 ako je radio u sva 3 prethodna slota
//                    var worked3PrevSlots = model.NewBoolVar($"worked_3_prev_slots_{c}_{t}");
//                    model.Add(LinearExpr.Sum(previousWorkVars) == 3).OnlyEnforceIf(worked3PrevSlots);
//                    model.Add(LinearExpr.Sum(previousWorkVars) < 3).OnlyEnforceIf(worked3PrevSlots.Not());

//                    // Ako je radio 4 slota ukupno (3 prethodna + trenutni) i prelazi na pauzu,
//                    // onda je to kraj bloka od 120 min i zahteva pauzu od 60 min (2 slota)
//                    var longWorkBlock = model.NewBoolVar($"long_work_block_{c}_{t}");
//                    model.Add(worked3PrevSlots + workingAtT + pauseAtTPlus1 == 3).OnlyEnforceIf(longWorkBlock);
//                    model.Add(worked3PrevSlots + workingAtT + pauseAtTPlus1 < 3).OnlyEnforceIf(longWorkBlock.Not());

//                    // Osiguravamo da ima pauzu od min 2 slota (60 min)
//                    if (t + 2 < timeSlots.Count && IsInShift(controller, timeSlots[t + 2], t + 2, timeSlots.Count))
//                    {
//                        model.Add(assignments[(c, t + 2, "break")] == 1).OnlyEnforceIf(longWorkBlock);
//                    }
//                }
//            }

//            // Provera za kraće blokove (1-3 slota, tj. 30-90 min) - zahteva min 1 slot pauze (30 min)
//            // Već je osigurano kroz detektovanje prelaza na pauzu (pauseAtTPlus1)

//            // Za sve ostale slotove, najmanja pauza je 30 min (1 slot), što je već
//            // osigurano detekcijom transitionToPause varijable
//        }
//    }

//    // Dodatna provera za blok od 4 slota (120 min) - posmatrajući 4 uzastopna slota
//    for (int c = 0; c < controllers.Count; c++)
//    {
//        for (int t = 0; t <= timeSlots.Count - 4; t++)
//        {
//            var controller = controllerInfo[controllers[c]];

//            // Provera da li je kontrolor u smeni u svim slotovima bloka
//            bool allInShift = true;
//            for (int i = 0; i < 4; i++)
//            {
//                if (!IsInShift(controller, timeSlots[t + i], t + i, timeSlots.Count))
//                {
//                    allInShift = false;
//                    break;
//                }
//            }

//            if (!allInShift)
//                continue;

//            // Proveravamo da li kontrolor radi u 4 uzastopna slota
//            var workVars = new List<IntVar>();
//            for (int i = 0; i < 4; i++)
//            {
//                var isWorkingVar = model.NewBoolVar($"is_working_4_block_{c}_{t + i}");
//                model.Add(assignments[(c, t + i, "break")] == 0).OnlyEnforceIf(isWorkingVar);
//                model.Add(assignments[(c, t + i, "break")] == 1).OnlyEnforceIf(isWorkingVar.Not());
//                workVars.Add(isWorkingVar);
//            }

//            // Varijabla koja je 1 ako kontrolor radi u sva 4 slota
//            var works4Slots = model.NewBoolVar($"works_4_slots_{c}_{t}");
//            model.Add(LinearExpr.Sum(workVars) == 4).OnlyEnforceIf(works4Slots);
//            model.Add(LinearExpr.Sum(workVars) < 4).OnlyEnforceIf(works4Slots.Not());

//            // Ako radi 4 slota (120 min), osiguravamo pauzu od min 60 min (2 slota) nakon toga
//            if (t + 5 < timeSlots.Count &&
//                IsInShift(controller, timeSlots[t + 4], t + 4, timeSlots.Count) &&
//                IsInShift(controller, timeSlots[t + 5], t + 5, timeSlots.Count))
//            {
//                model.Add(assignments[(c, t + 4, "break")] + assignments[(c, t + 5, "break")] >= 2)
//                     .OnlyEnforceIf(works4Slots);
//            }
//            else if (t + 4 < timeSlots.Count &&
//                    IsInShift(controller, timeSlots[t + 4], t + 4, timeSlots.Count))
//            {
//                // Ako je dostupan samo jedan slot nakon rada, osiguramo bar taj jedan
//                model.Add(assignments[(c, t + 4, "break")] == 1).OnlyEnforceIf(works4Slots);
//            }
//        }
//    }

//    // 7. PRIORITET #1: Minimum radnog bloka - najmanje 30 minuta (1 slot)
//    for (int c = 0; c < controllers.Count; c++)
//    {
//        for (int t = 0; t < timeSlots.Count - 1; t++)
//        {
//            // provera da li je kontrolor na pauzi u slotu t i radi na t+1 (pocetak rada)
//            var onBreakT = assignments[(c, t, "break")];

//            // promenljiva da kl radi na t+1
//            var workingT1 = model.NewBoolVar($"working_{c}_{t + 1}");

//            // pokupi sve sektorske promenljiva za vreme t+1
//            var sectorVarsT1 = new List<IntVar>();
//            foreach (var sector in requiredSectors[t + 1])
//            {
//                sectorVarsT1.Add(assignments[(c, t + 1, sector)]);
//            }

//            // definisi workingT1 kao 1 ako je bilo koja sektorska verijabla 1
//            if (sectorVarsT1.Count != 0)
//            {
//                model.Add(LinearExpr.Sum(sectorVarsT1) >= 1).OnlyEnforceIf(workingT1);
//                model.Add(LinearExpr.Sum(sectorVarsT1) == 0).OnlyEnforceIf(workingT1.Not());
//            }
//            else
//            {
//                // nema sektora na t+1, kl je na pauzi
//                model.Add(workingT1 == 0);
//            }

//            // startingWork=true ako je onBreakT=true and workingT1=true
//            var startingWork = model.NewBoolVar($"starting_work_{c}_{t + 1}");

//            // startingWrok=true ako i samo ako je onBreakT=true i workingT1=true
//            model.Add(onBreakT + workingT1 >= 2).OnlyEnforceIf(startingWork);
//            model.Add(onBreakT + workingT1 <= 1).OnlyEnforceIf(startingWork.Not());

//            // ako je pocetak rada, osiguran minimum duzinu bloka
//            for (int len = 1; len < MIN_WORK_BLOCK && t + 1 + len < timeSlots.Count; len++)
//            {
//                // za svaki buduci slot u minimalnom bloku, kl ne sme biti na pauzi
//                var futureBreak = assignments[(c, t + 1 + len, "break")];
//                model.Add(futureBreak == 0).OnlyEnforceIf(startingWork);
//            }
//        }
//    }

//    // 8 PRIORITET #4: Rotacija između E i P pozicija
//    // (Planer prelazi na E, a novi kontrolor preuzima P poziciju)
//    // PRIORITET #4: Rotacija između E i P pozicija nakon kontinuiranog rada na istoj poziciji
//    for (int c = 0; c < controllers.Count; c++)
//    {
//        // Za svaki vremenski slot osim poslednja dva
//        for (int t = 0; t < timeSlots.Count - 2; t++)
//        {
//            // Za svaki sektor u trenutnom slotu
//            foreach (var sector in requiredSectors[t].Where(s => s.EndsWith("E") || s.EndsWith("P")))
//            {
//                // Izvlačimo osnovni sektor (bez E/P oznake)
//                string baseSector = sector[..^1];

//                // Trenutna pozicija (E ili P)
//                string currentPosition = sector[^1..];

//                // Alternativna pozicija (P ako je trenutna E, i obrnuto)
//                string alternativePosition = (currentPosition == "E") ? "P" : "E";
//                string alternativeSector = baseSector + alternativePosition;

//                // Proveravamo da li isti sektor i alternativni sektor postoje u narednim slotovima
//                if (requiredSectors[t + 1].Contains(sector) && requiredSectors[t + 2].Contains(alternativeSector))
//                {
//                    // Varijabla koja označava da kontrolor radi na istom sektoru i poziciji 2 slota zaredom (t i t+1)
//                    var workingSameSectorTwoSlots = model.NewBoolVar($"working_same_{sector}_two_slots_{c}_{t}");

//                    // Kontrolor radi na istom sektoru i poziciji u slotu t
//                    model.Add(assignments[(c, t, sector)] == 1).OnlyEnforceIf(workingSameSectorTwoSlots);

//                    // Kontrolor radi na istom sektoru i poziciji u slotu t+1
//                    model.Add(assignments[(c, t + 1, sector)] == 1).OnlyEnforceIf(workingSameSectorTwoSlots);

//                    // Kontrolor nije na pauzi u slotu t+2
//                    var notOnBreakTPlus2 = model.NewBoolVar($"not_on_break_{c}_{t + 2}");
//                    model.Add(assignments[(c, t + 2, "break")] == 0).OnlyEnforceIf(notOnBreakTPlus2);
//                    model.Add(assignments[(c, t + 2, "break")] == 1).OnlyEnforceIf(notOnBreakTPlus2.Not());

//                    // Ako kontrolor radi na istom sektoru i poziciji 2 slota zaredom i nije na pauzi u trećem slotu,
//                    // trebalo bi da rotira na alternativnu poziciju u trećem slotu
//                    var shouldRotateAfterTwoSlots = model.NewBoolVar($"should_rotate_after_two_{c}_{t}_{sector}");
//                    model.Add(workingSameSectorTwoSlots == 1).OnlyEnforceIf(shouldRotateAfterTwoSlots);
//                    model.Add(notOnBreakTPlus2 == 1).OnlyEnforceIf(shouldRotateAfterTwoSlots);

//                    // Varijabla koja označava da kontrolor zaista rotira na alternativnu poziciju
//                    var doesRotateToAlternative = model.NewBoolVar($"does_rotate_to_{alternativeSector}_{c}_{t + 2}");
//                    model.Add(assignments[(c, t + 2, alternativeSector)] == 1).OnlyEnforceIf(doesRotateToAlternative);
//                    model.Add(assignments[(c, t + 2, alternativeSector)] == 0).OnlyEnforceIf(doesRotateToAlternative.Not());

//                    // Varijabla za funkciju cilja - označava da kontrolor treba da rotira ali ne rotira
//                    var shouldRotateButDoesnt = model.NewBoolVar($"should_rotate_but_doesnt_{c}_{t}_{baseSector}");

//                    // Definišemo kada kontrolor treba da rotira ali ne rotira:
//                    // shouldRotateAfterTwoSlots == 1 && doesRotateToAlternative == 0
//                    model.Add(shouldRotateAfterTwoSlots == 1).OnlyEnforceIf(shouldRotateButDoesnt);
//                    model.Add(doesRotateToAlternative == 0).OnlyEnforceIf(shouldRotateButDoesnt);

//                    // Definišemo kada kontrolor NE treba da rotira ili zaista rotira:
//                    // shouldRotateAfterTwoSlots == 0 || doesRotateToAlternative == 1
//                    var notShouldRotate = model.NewBoolVar($"not_should_rotate_{c}_{t}_{baseSector}");
//                    var doesRotate = model.NewBoolVar($"does_rotate_{c}_{t}_{baseSector}");

//                    model.Add(shouldRotateAfterTwoSlots == 0).OnlyEnforceIf(notShouldRotate);
//                    model.Add(shouldRotateAfterTwoSlots == 1).OnlyEnforceIf(notShouldRotate.Not());

//                    model.Add(doesRotateToAlternative == 1).OnlyEnforceIf(doesRotate);
//                    model.Add(doesRotateToAlternative == 0).OnlyEnforceIf(doesRotate.Not());

//                    model.AddBoolOr([notShouldRotate, doesRotate]).OnlyEnforceIf(shouldRotateButDoesnt.Not());

//                    // Čuvamo ovu varijablu za funkciju cilja
//                    shouldWorkOnEVars[(c, t + 2, baseSector)] = shouldRotateButDoesnt;
//                }
//            }
//        }
//    }


//    // 10. PRIORITET #8: Jasno ograničenje za Flag="S" periode
//    // (Kontrolor sa Flag="S" mora biti oslobođen dužnosti)
//    for (int c = 0; c < controllers.Count; c++)
//    {
//        string controllerCode = controllers[c];

//        for (int t = 0; t < timeSlots.Count; t++)
//        {
//            DateTime slotTime = timeSlots[t];

//            // Proveri da li kontrolor ima Flag="S" za ovaj slot
//            bool flagS = IsFlagS(controllerCode, slotTime, inicijalniRaspored);

//            if (flagS)
//            {
//                // Kontrolor sa Flag="S" mora biti na pauzi
//                model.Add(assignments[(c, t, "break")] == 1);

//                // I ne sme biti dodeljen ni jednom sektoru
//                foreach (var sector in requiredSectors[t])
//                {
//                    model.Add(assignments[(c, t, sector)] == 0);
//                }
//            }
//        }
//    }

//    // ISPRAVKA: Dodaj ovo ograničenje umesto zakomentarisanog koda
//    // 9. PRIORITET #9: SS i SUP ne mogu raditi na sektoru istovremeno
//    for (int t = 0; t < timeSlots.Count; t++)
//    {
//        // Identifikuj SS kontrolore koji su u smeni u ovom slotu
//        var ssWorkingVars = new List<IntVar>();
//        var supWorkingVars = new List<IntVar>();

//        for (int c = 0; c < controllers.Count; c++)
//        {
//            var controller = controllerInfo[controllers[c]];

//            // Proveri da li je kontrolor u smeni u ovom slotu
//            bool inShift = IsInShift(controller, timeSlots[t], t, timeSlots.Count);

//            if (!inShift) continue;

//            if (controller.IsShiftLeader) // SS
//            {
//                // Kreiraj varijablu koja je 1 ako SS radi na bilo kom sektoru u ovom slotu
//                var ssIsWorking = model.NewBoolVar($"ss_{c}_working_{t}");

//                var sectorVars = new List<IntVar>();
//                foreach (var sector in requiredSectors[t])
//                {
//                    sectorVars.Add(assignments[(c, t, sector)]);
//                }

//                if (sectorVars.Count > 0)
//                {
//                    // ssIsWorking = 1 ako radi na bilo kom sektoru, 0 ako je na pauzi
//                    model.Add(LinearExpr.Sum(sectorVars) >= 1).OnlyEnforceIf(ssIsWorking);
//                    model.Add(LinearExpr.Sum(sectorVars) == 0).OnlyEnforceIf(ssIsWorking.Not());

//                    ssWorkingVars.Add(ssIsWorking);

//                    // Loguj za debug
//                    _logger.LogDebug($"Added SS working constraint for controller {controllers[c]} at slot {t}");
//                }
//            }
//            else if (controller.IsSupervisor) // SUP
//            {
//                // Kreiraj varijablu koja je 1 ako SUP radi na bilo kom sektoru u ovom slotu
//                var supIsWorking = model.NewBoolVar($"sup_{c}_working_{t}");

//                var sectorVars = new List<IntVar>();
//                foreach (var sector in requiredSectors[t])
//                {
//                    sectorVars.Add(assignments[(c, t, sector)]);
//                }

//                if (sectorVars.Count > 0)
//                {
//                    // supIsWorking = 1 ako radi na bilo kom sektoru, 0 ako je na pauzi
//                    model.Add(LinearExpr.Sum(sectorVars) >= 1).OnlyEnforceIf(supIsWorking);
//                    model.Add(LinearExpr.Sum(sectorVars) == 0).OnlyEnforceIf(supIsWorking.Not());

//                    supWorkingVars.Add(supIsWorking);

//                    // Loguj za debug
//                    _logger.LogDebug($"Added SUP working constraint for controller {controllers[c]} at slot {t}");
//                }
//            }
//        }

//        // KLJUČNO OGRANIČENJE: Maksimalno jedan od SS ili SUP može raditi u isto vreme
//        if (ssWorkingVars.Count > 0 && supWorkingVars.Count > 0)
//        {
//            // Suma svih SS i SUP varijabli mora biti <= 1
//            var allSpecialControllers = new List<IntVar>();
//            allSpecialControllers.AddRange(ssWorkingVars);
//            allSpecialControllers.AddRange(supWorkingVars);

//            model.Add(LinearExpr.Sum(allSpecialControllers) <= 1);

//            _logger.LogInformation($"Added SS/SUP mutual exclusion constraint for slot {t}: {ssWorkingVars.Count} SS controllers, {supWorkingVars.Count} SUP controllers");
//        }
//        else if (ssWorkingVars.Count > 0)
//        {
//            // Ako ima više SS kontrolora, maksimalno jedan može raditi
//            model.Add(LinearExpr.Sum(ssWorkingVars) <= 1);
//            _logger.LogDebug($"Added constraint for multiple SS controllers at slot {t}");
//        }
//        else if (supWorkingVars.Count > 0)
//        {
//            // Ako ima više SUP kontrolora, maksimalno jedan može raditi
//            model.Add(LinearExpr.Sum(supWorkingVars) <= 1);
//            _logger.LogDebug($"Added constraint for multiple SUP controllers at slot {t}");
//        }
//    }

//    // POSEBNO RUKOVANJE NOĆNOM SMENOM
//    // Dodatna ograničenja i preferencije za noćnu smenu
//    // Identifikuj slotove, kontrolore u noćnoj smeni i SS/SUP kontrolore
//    for (int t = 0; t < timeSlots.Count; t++)
//    {
//        bool isNightSlot = false;

//        for (int c = 0; c < controllers.Count; c++)
//        {
//            var controller = controllerInfo[controllers[c]];
//            bool inShift = IsInShift(controller, timeSlots[t], t, timeSlots.Count);

//            if (inShift)
//            {
//                if (controller.ShiftType == "N")
//                {
//                    isNightSlot = true;

//                    if (!controller.IsShiftLeader && !controller.IsSupervisor)
//                    {
//                        if (!nightShiftControllers.Contains(c))
//                            nightShiftControllers.Add(c);
//                    }
//                }

//                if (controller.IsShiftLeader && !ssControllers.Contains(c))
//                {
//                    ssControllers.Add(c);
//                }
//                else if (controller.IsSupervisor && !supControllers.Contains(c))
//                {
//                    supControllers.Add(c);
//                }
//            }
//        }

//        if (isNightSlot)
//        {
//            nightShiftSlots.Add(t);
//        }
//    }

//    _logger.LogInformation($"Identified {nightShiftSlots.Count} night shift slots, {nightShiftControllers.Count} regular controllers, {ssControllers.Count} SS controllers, {supControllers.Count} SUP controllers");

//    if (nightShiftSlots.Count > 0 && (ssControllers.Count > 0 || supControllers.Count > 0))
//    {
//        // 1. SS i SUP ne mogu raditi istovremeno
//        foreach (int t in nightShiftSlots)
//        {
//            foreach (var sector1 in requiredSectors[t])
//            {
//                foreach (var sector2 in requiredSectors[t])
//                {
//                    // Za svaki SS kontrolor
//                    foreach (int ssC in ssControllers)
//                    {
//                        // Za svaki SUP kontrolor
//                        foreach (int supC in supControllers)
//                        {
//                            // Ograničenje: ako SS radi na sektoru1, SUP ne može raditi na sektoru2
//                            model.Add(assignments[(ssC, t, sector1)] + assignments[(supC, t, sector2)] <= 1);
//                        }
//                    }
//                }
//            }
//        }

//        // 2. Preferiraj da SS i SUP preuzimaju sektore u noćnoj smeni
//        foreach (int t in nightShiftSlots)
//        {
//            foreach (var sector in requiredSectors[t])
//            {
//                // 2.1 Varijable za praćenje ko pokriva sektor
//                var sectorCoveredBySS = model.NewBoolVar($"sector_covered_by_ss_{t}_{sector}");
//                var ssVars = new List<IntVar>();
//                foreach (int c in ssControllers)
//                {
//                    if (IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
//                    {
//                        ssVars.Add(assignments[(c, t, sector)]);
//                    }
//                }

//                if (ssVars.Any())
//                {
//                    model.Add(LinearExpr.Sum(ssVars) >= 1).OnlyEnforceIf(sectorCoveredBySS);
//                    model.Add(LinearExpr.Sum(ssVars) == 0).OnlyEnforceIf(sectorCoveredBySS.Not());
//                }
//                else
//                {
//                    model.Add(sectorCoveredBySS == 0);
//                }

//                var sectorCoveredBySUP = model.NewBoolVar($"sector_covered_by_sup_{t}_{sector}");
//                var supVars = new List<IntVar>();
//                foreach (int c in supControllers)
//                {
//                    if (IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
//                    {
//                        supVars.Add(assignments[(c, t, sector)]);
//                    }
//                }

//                if (supVars.Any())
//                {
//                    model.Add(LinearExpr.Sum(supVars) >= 1).OnlyEnforceIf(sectorCoveredBySUP);
//                    model.Add(LinearExpr.Sum(supVars) == 0).OnlyEnforceIf(sectorCoveredBySUP.Not());
//                }
//                else
//                {
//                    model.Add(sectorCoveredBySUP == 0);
//                }

//                var sectorCoveredByRegular = model.NewBoolVar($"sector_covered_by_regular_{t}_{sector}");
//                var regularVars = new List<IntVar>();
//                foreach (int c in nightShiftControllers)
//                {
//                    if (IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
//                    {
//                        regularVars.Add(assignments[(c, t, sector)]);
//                    }
//                }

//                if (regularVars.Any())
//                {
//                    model.Add(LinearExpr.Sum(regularVars) >= 1).OnlyEnforceIf(sectorCoveredByRegular);
//                    model.Add(LinearExpr.Sum(regularVars) == 0).OnlyEnforceIf(sectorCoveredByRegular.Not());
//                }
//                else
//                {
//                    model.Add(sectorCoveredByRegular == 0);
//                }

//                // 2.2 Varijabla koja označava da li je sektor pokriven (mora biti tačno 1)
//                model.Add(sectorCoveredBySS + sectorCoveredBySUP + sectorCoveredByRegular == 1);

//                // 2.3 Varijabla koja označava propuštenu priliku (SS/SUP su slobodni ali ne pokrivaju sektor)
//                var missedOpportunity = model.NewBoolVar($"missed_opportunity_{t}_{sector}");

//                // missedOpportunity = 1 ako: 
//                // 1. Sektor pokriva običan kontrolor (sectorCoveredByRegular = 1)
//                // 2. Ni SS ni SUP ne pokrivaju druge sektore u tom slotu (mogu biti slobodni)

//                // Provera da li SS pokriva bilo koji drugi sektor u ovom slotu
//                var ssWorking = model.NewBoolVar($"ss_working_{t}");
//                var ssWorkingVars = new List<IntVar>();
//                foreach (int c in ssControllers)
//                {
//                    if (IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
//                    {
//                        foreach (var otherSector in requiredSectors[t])
//                        {
//                            ssWorkingVars.Add(assignments[(c, t, otherSector)]);
//                        }
//                    }
//                }

//                if (ssWorkingVars.Any())
//                {
//                    model.Add(LinearExpr.Sum(ssWorkingVars) >= 1).OnlyEnforceIf(ssWorking);
//                    model.Add(LinearExpr.Sum(ssWorkingVars) == 0).OnlyEnforceIf(ssWorking.Not());
//                }
//                else
//                {
//                    model.Add(ssWorking == 0);
//                }

//                // Provera da li SUP pokriva bilo koji drugi sektor u ovom slotu
//                var supWorking = model.NewBoolVar($"sup_working_{t}");
//                var supWorkingVars = new List<IntVar>();
//                foreach (int c in supControllers)
//                {
//                    if (IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
//                    {
//                        foreach (var otherSector in requiredSectors[t])
//                        {
//                            supWorkingVars.Add(assignments[(c, t, otherSector)]);
//                        }
//                    }
//                }

//                if (supWorkingVars.Any())
//                {
//                    model.Add(LinearExpr.Sum(supWorkingVars) >= 1).OnlyEnforceIf(supWorking);
//                    model.Add(LinearExpr.Sum(supWorkingVars) == 0).OnlyEnforceIf(supWorking.Not());
//                }
//                else
//                {
//                    model.Add(supWorking == 0);
//                }

//                // Definišemo missedOpportunity: regularni kontrolor radi, a SS ili SUP su dostupni
//                // (regularni radi + (SS ne radi ILI SUP ne radi))
//                var notSSWorking = model.NewBoolVar($"not_ss_working_{t}");
//                model.Add(ssWorking == 0).OnlyEnforceIf(notSSWorking);
//                model.Add(ssWorking == 1).OnlyEnforceIf(notSSWorking.Not());

//                var notSUPWorking = model.NewBoolVar($"not_sup_working_{t}");
//                model.Add(supWorking == 0).OnlyEnforceIf(notSUPWorking);
//                model.Add(supWorking == 1).OnlyEnforceIf(notSUPWorking.Not());

//                // AtLeastOne od (notSSWorking, notSUPWorking) - true ako bar jedan nije zauzet
//                var atLeastOneAvailable = model.NewBoolVar($"at_least_one_available_{t}");
//                model.AddBoolOr([notSSWorking, notSUPWorking]).OnlyEnforceIf(atLeastOneAvailable);
//                model.AddBoolAnd([ssWorking, supWorking]).OnlyEnforceIf(atLeastOneAvailable.Not());

//                // Finalna definicija missedOpportunity
//                model.Add(sectorCoveredByRegular + atLeastOneAvailable >= 2).OnlyEnforceIf(missedOpportunity);
//                model.Add(sectorCoveredByRegular + atLeastOneAvailable < 2).OnlyEnforceIf(missedOpportunity.Not());

//                // Čuvaj za funkciju cilja
//                nightShiftMissedOpportunities[(t, sector)] = missedOpportunity;
//            }
//        }

//        // 3. Osiguraj maksimalne pauze za obične kontrolore
//        foreach (int c in nightShiftControllers)
//        {
//            // 3.1 Podstiči duge pauze (2+ sata) za obične kontrolore
//            for (int t = 0; t < timeSlots.Count - 3; t++)
//            {
//                if (!nightShiftSlots.Contains(t))
//                    continue;

//                if (!IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
//                    continue;

//                // Proveri da li kontrolor ima pauzu u 4 uzastopna slota (2 sata)
//                var has2HourBreak = model.NewBoolVar($"has_2hour_break_{c}_{t}");

//                var breakVars = new List<IntVar>();
//                for (int i = 0; i < 4 && t + i < timeSlots.Count; i++)
//                {
//                    if (IsInShift(controllerInfo[controllers[c]], timeSlots[t + i], t + i, timeSlots.Count))
//                    {
//                        breakVars.Add(assignments[(c, t + i, "break")]);
//                    }
//                }

//                if (breakVars.Count == 4)
//                {
//                    model.Add(LinearExpr.Sum(breakVars) == 4).OnlyEnforceIf(has2HourBreak);
//                    model.Add(LinearExpr.Sum(breakVars) < 4).OnlyEnforceIf(has2HourBreak.Not());

//                    // Čuvaj za funkciju cilja
//                    nightShiftLongBreaks[(c, t)] = has2HourBreak;
//                }
//            }

//            // 3.2 Izbegavaj duge periode rada bez pauze
//            for (int t = 0; t < timeSlots.Count - 2; t++)
//            {
//                if (!nightShiftSlots.Contains(t))
//                    continue;

//                if (!IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
//                    continue;

//                // Proveri da li kontrolor radi 3 uzastopna slota (1.5 sat) bez pauze
//                var workingVars = new List<IntVar>();
//                for (int i = 0; i < 3 && t + i < timeSlots.Count; i++)
//                {
//                    if (IsInShift(controllerInfo[controllers[c]], timeSlots[t + i], t + i, timeSlots.Count))
//                    {
//                        var isWorking = model.NewBoolVar($"is_working_{c}_{t + i}");
//                        model.Add(assignments[(c, t + i, "break")] == 0).OnlyEnforceIf(isWorking);
//                        model.Add(assignments[(c, t + i, "break")] == 1).OnlyEnforceIf(isWorking.Not());
//                        workingVars.Add(isWorking);
//                    }
//                }

//                if (workingVars.Count == 3)
//                {
//                    var works3Consecutive = model.NewBoolVar($"works_3_consecutive_{c}_{t}");
//                    model.Add(LinearExpr.Sum(workingVars) == 3).OnlyEnforceIf(works3Consecutive);
//                    model.Add(LinearExpr.Sum(workingVars) < 3).OnlyEnforceIf(works3Consecutive.Not());

//                    // Čuvaj za funkciju cilja - visoki penali za duge periode rada
//                    nightShiftLongWorkPeriods[(c, t)] = works3Consecutive;
//                }
//            }
//        }
//    }

//    // 4. BALANSIRANJE OPTEREĆENJA OBIČNIH KONTROLORA U NOĆNOJ SMENI
//    if (nightShiftControllers.Count > 1)
//    {
//        _logger.LogInformation("Adding load balancing constraints for night shift controllers");

//        // 4.1 Prati ukupno radno vreme za svakog kontrolora tokom noćne smene
//        var nightShiftWorkloadVars = new Dictionary<int, IntVar>();

//        foreach (int c in nightShiftControllers)
//        {
//            var workingVars = new List<IntVar>();
//            foreach (int t in nightShiftSlots)
//            {
//                if (IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
//                {
//                    // Varijabla koja je 1 ako kontrolor radi u slotu t, 0 ako je na pauzi
//                    var isWorkingVar = model.NewBoolVar($"is_working_night_{c}_{t}");
//                    model.Add(assignments[(c, t, "break")] == 0).OnlyEnforceIf(isWorkingVar);
//                    model.Add(assignments[(c, t, "break")] == 1).OnlyEnforceIf(isWorkingVar.Not());

//                    workingVars.Add(isWorkingVar);
//                }
//            }

//            // Ukupan broj slotova rada za kontrolora
//            int maxPossibleWork = workingVars.Count;
//            nightShiftWorkloadVars[c] = model.NewIntVar(0, maxPossibleWork, $"night_shift_workload_{c}");
//            model.Add(nightShiftWorkloadVars[c] == LinearExpr.Sum(workingVars));
//        }

//        // 4.2 Izračunaj maksimalnu razliku u radnom vremenu između kontrolora
//        // Koristi "minimax" pristup - minimizuj maksimalno opterećenje, osiguravajući ravnomernu distribuciju

//        // Varijabla za maksimalno opterećenje bilo kog kontrolora
//        var maxWorkload = model.NewIntVar(0, nightShiftSlots.Count, "max_night_workload");

//        // Varijabla za minimalno opterećenje bilo kog kontrolora
//        var minWorkload = model.NewIntVar(0, nightShiftSlots.Count, "min_night_workload");

//        // Postavi maxWorkload kao gornju granicu za sve kontrolore
//        foreach (int c in nightShiftControllers)
//        {
//            model.Add(nightShiftWorkloadVars[c] <= maxWorkload);
//        }

//        // Postavi minWorkload kao donju granicu za sve kontrolore
//        foreach (int c in nightShiftControllers)
//        {
//            model.Add(nightShiftWorkloadVars[c] >= minWorkload);
//        }

//        // Varijabla koja predstavlja razliku između max i min opterećenja
//        var workloadDifference = model.NewIntVar(0, nightShiftSlots.Count, "night_workload_difference");
//        model.Add(workloadDifference == maxWorkload - minWorkload);

//        // Čuvaj za funkciju cilja
//        nightShiftWorkloadDifference = workloadDifference;

//        // 4.3 Dodatna ograničenja za balansiranje po sektorima
//        // Osigurajmo da kontrolori rade na različitim tipovima sektora

//        // Prati rad na svakom tipu sektora za svakog kontrolora
//        foreach (int c in nightShiftControllers)
//        {
//            // Prati različite tipove sektora
//            var sectorTypes = new HashSet<string>();

//            foreach (int t in nightShiftSlots)
//            {
//                if (!IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
//                    continue;

//                foreach (var sector in requiredSectors[t])
//                {
//                    // Izvuci osnovni tip sektora (prva 2 karaktera)
//                    if (sector.Length >= 2)
//                    {
//                        string sectorType = sector.Substring(0, 2);
//                        if (!sectorTypes.Contains(sectorType))
//                        {
//                            sectorTypes.Add(sectorType);

//                            // Varijabla koja prati da li kontrolor radi na ovom tipu sektora
//                            var worksOnSectorType = model.NewBoolVar($"works_on_sector_type_{c}_{sectorType}");

//                            // Prikupi sve sektore ovog tipa
//                            var sectorTypeVars = new List<IntVar>();
//                            foreach (int slot in nightShiftSlots)
//                            {
//                                if (!IsInShift(controllerInfo[controllers[c]], timeSlots[slot], slot, timeSlots.Count))
//                                    continue;

//                                foreach (var sec in requiredSectors[slot])
//                                {
//                                    if (sec.Length >= 2 && sec.Substring(0, 2) == sectorType)
//                                    {
//                                        sectorTypeVars.Add(assignments[(c, slot, sec)]);
//                                    }
//                                }
//                            }

//                            if (sectorTypeVars.Any())
//                            {
//                                model.Add(LinearExpr.Sum(sectorTypeVars) >= 1).OnlyEnforceIf(worksOnSectorType);
//                                model.Add(LinearExpr.Sum(sectorTypeVars) == 0).OnlyEnforceIf(worksOnSectorType.Not());

//                                // Varijabla za funkciju cilja
//                                nightShiftSectorTypeCoverage[(c, sectorType)] = worksOnSectorType;
//                            }
//                        }
//                    }
//                }
//            }
//        }

//        // 4.4 Ograniči ukupno trajanje kontinuiranog rada
//        // Kontrolor ne bi trebao raditi više od X uzastopnih slotova
//        foreach (int c in nightShiftControllers)
//        {
//            for (int t = 0; t < nightShiftSlots.Count - 2; t++)
//            {
//                if (!IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
//                    continue;

//                // Sprečavamo više od 2 uzastopna slota rada (1 sat) bez pauze u noćnoj smeni
//                if (t + 2 < timeSlots.Count)
//                {
//                    var threeConsecutiveWork = model.NewBoolVar($"three_consecutive_work_{c}_{t}");
//                    var workVars = new List<IntVar>();

//                    for (int i = 0; i < 3; i++)
//                    {
//                        if (t + i < timeSlots.Count && IsInShift(controllerInfo[controllers[c]], timeSlots[t + i], t + i, timeSlots.Count))
//                        {
//                            var isWorkingVar = model.NewBoolVar($"is_working_consecutive_{c}_{t + i}");
//                            model.Add(assignments[(c, t + i, "break")] == 0).OnlyEnforceIf(isWorkingVar);
//                            model.Add(assignments[(c, t + i, "break")] == 1).OnlyEnforceIf(isWorkingVar.Not());

//                            workVars.Add(isWorkingVar);
//                        }
//                    }

//                    if (workVars.Count == 3)
//                    {
//                        model.Add(LinearExpr.Sum(workVars) == 3).OnlyEnforceIf(threeConsecutiveWork);
//                        model.Add(LinearExpr.Sum(workVars) < 3).OnlyEnforceIf(threeConsecutiveWork.Not());

//                        // Dodaj za funkciju cilja
//                        nightShiftConsecutiveWork[(c, t)] = threeConsecutiveWork;
//                    }
//                }
//            }
//        }
//    }

//    if (_controllersWithLicense == null)
//    {
//        _logger.LogWarning("Controllers with license list is not initialized");
//        _controllersWithLicense = new List<string>(); // Inicijalizujemo praznu listu kao fallback
//    }

//    //// Posebna ograničenja za FMP pozicije
//    for (int t = 0; t < timeSlots.Count; t++)
//    {
//        foreach (var sector in requiredSectors[t].Where(s => s.Contains("FMP")))
//        {
//            var fmpAssignments = new List<IntVar>();

//            for (int c = 0; c < controllers.Count; c++)
//            {
//                var controllerCode = controllers[c];

//                // Proveri da li je ovaj kontrolor na FMP poziciji
//                bool isFMP = controllerInfo[controllerCode].ORM == "FMP";

//                // Proveri da li kontrolor ima licencu
//                bool hasLicense = _controllersWithLicense.Contains(controllerCode);

//                // Samo kontrolori sa FMP pozicijom i licencom mogu raditi na FMP sektoru
//                if (isFMP && hasLicense)
//                {
//                    fmpAssignments.Add(assignments[(c, t, sector)]);
//                }
//                else
//                {
//                    // Osiguramo da kontrolori bez licence ne mogu raditi na FMP sektoru
//                    model.Add(assignments[(c, t, sector)] == 0);
//                }
//            }

//            // Osiguramo da je FMP sektor pokriven ako postoje dostupni kontrolori sa licencom
//            if (fmpAssignments.Count > 0)
//            {
//                // Pokrij sektor ako ima dostupnih kontrolora (ali maksimalno 1)
//                model.Add(LinearExpr.Sum(fmpAssignments) <= 1);
//            }
//        }
//    }

//    this.AddEmergencyConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, inicijalniRaspored);

//}

private void AddEmergencyConstraints(CpModel model, Dictionary<(int, int, string), IntVar> assignments,
            List<string> controllers, List<DateTime> timeSlots,
            Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo,
            DataTable inicijalniRaspored)
        {
            _logger.LogInformation("Adding emergency constraints to ensure sector coverage");

            int emergencyConstraintsAdded = 0;
            int totalSectorSlots = 0;
            int sectorsWithoutControllers = 0;

            // Debug info - proveri FMP kontrolore
            if (_controllersWithLicense != null)
            {
                int fmpWithLicense = controllerInfo.Values.Count(c =>
                    c.ORM == "FMP" && _controllersWithLicense.Contains(c.Code));
                _logger.LogInformation("Available FMP controllers with license: {Count}", fmpWithLicense);
            }

            for (int t = 0; t < timeSlots.Count; t++)
            {
                DateTime slotTime = timeSlots[t];

                foreach (var sector in requiredSectors[t])
                {
                    totalSectorSlots++;

                    // Lista dostupnih kontrolora za ovaj sektor
                    var availableAssignments = new List<IntVar>();
                    var availableControllers = new List<string>();

                    for (int c = 0; c < controllers.Count; c++)
                    {
                        var controller = controllerInfo[controllers[c]];

                        // Osnovne provere
                        bool inShift = IsInShift(controller, timeSlots[t], t, timeSlots.Count);
                        bool isFlagS = IsFlagS(controllers[c], timeSlots[t], inicijalniRaspored);

                        // Preskačemo kontrolore koji nisu u smeni ili imaju Flag="S"
                        if (!inShift || isFlagS)
                            continue;

                        // Proveri da li assignment varijabla postoji
                        var assignmentKey = (c, t, sector);
                        if (!assignments.ContainsKey(assignmentKey))
                            continue;

                        bool canAssign = false;
                        string controllerType = "";

                        // Specifična logika za FMP sektore
                        if (sector.Contains("FMP"))
                        {
                            bool isFMP = controller.ORM == "FMP";
                            bool hasLicense = _controllersWithLicense?.Contains(controllers[c]) ?? false;

                            // SAMO FMP kontrolori sa licencom mogu raditi na FMP sektorima
                            if (isFMP && hasLicense)
                            {
                                canAssign = true;
                                controllerType = "FMP+License";
                            }
                            // NE dodajemo emergency fallback - to je bio uzrok problema
                        }
                        else
                        {
                            // Za sve ostale sektore, dozvoljavamo sve tipove kontrolora
                            canAssign = true;

                            if (controller.IsShiftLeader)
                                controllerType = "SS";
                            else if (controller.IsSupervisor)
                                controllerType = "SUP";
                            else
                                controllerType = "Regular";
                        }

                        // Dodaj kontrolora u listu dostupnih
                        if (canAssign)
                        {
                            availableAssignments.Add(assignments[assignmentKey]);
                            availableControllers.Add($"{controllers[c]}({controllerType})");
                        }
                    }

                    // Dodaj constraint samo ako ima dostupnih kontrolora
                    if (availableAssignments.Count > 0)
                    {
                        try
                        {
                            // VAŽNO: Koristimo MEKO ograničenje umesto čvrstog
                            // Ovo sprečava konflikte sa postojećim ograničenjima

                            var sectorCovered = model.NewBoolVar($"emergency_sector_covered_{t}_{sector.Replace("/", "_")}");

                            // Definiši kada je sektor pokriven
                            model.Add(LinearExpr.Sum(availableAssignments) >= 1).OnlyEnforceIf(sectorCovered);
                            model.Add(LinearExpr.Sum(availableAssignments) == 0).OnlyEnforceIf(sectorCovered.Not());

                            // Umesto čvrstog ograničenja, ovo će biti dodano u objective funkciju
                            // kao visok penal za nepokriven sektor
                            // NAPOMENA: Ovo treba dodati u DefineObjective metodi:
                            // objectiveTerms.Add(EMERGENCY_UNCOVERED_PENALTY * sectorCovered.Not());

                            emergencyConstraintsAdded++;

                            _logger.LogDebug("Emergency constraint for sector {Sector} at {TimeSlot}: " +
                                           "{ControllerCount} controllers available: {Controllers}",
                                sector, slotTime.ToString("HH:mm"), availableAssignments.Count,
                                string.Join(", ", availableControllers.Take(5))); // Ograniči log na 5 kontrolora
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to add emergency constraint for sector {Sector} at {TimeSlot}",
                                sector, slotTime.ToString("HH:mm"));
                            // Nastavi sa izvršavanjem, ne prekidaj
                        }
                    }
                    else
                    {
                        sectorsWithoutControllers++;

                        if (sector.Contains("FMP"))
                        {
                            _logger.LogWarning("FMP sector {Sector} at {TimeSlot} has no available FMP controllers with license",
                                sector, slotTime.ToString("HH:mm"));
                        }
                        else
                        {
                            _logger.LogWarning("Sector {Sector} at {TimeSlot} has no available controllers",
                                sector, slotTime.ToString("HH:mm"));
                        }
                    }
                }
            }

            // Sumarni log
            _logger.LogInformation("Emergency constraints summary:");
            _logger.LogInformation("  Total sector-slot combinations: {TotalSlots}", totalSectorSlots);
            _logger.LogInformation("  Emergency constraints added: {EmergencyConstraints}", emergencyConstraintsAdded);
            _logger.LogInformation("  Sectors without controllers: {SectorsWithoutControllers}", sectorsWithoutControllers);

            if (sectorsWithoutControllers > 0)
            {
                double uncoveredPercentage = (double)sectorsWithoutControllers / totalSectorSlots * 100;
                _logger.LogWarning("WARNING: {Percentage:F1}% of sector-slots have no available controllers and will remain uncovered",
                    uncoveredPercentage);
            }
        }


        private static bool IsInShift(ControllerInfo controller, DateTime slotTime, int slotIndex, int totalSlots)
        {
            // Osnovni uslov - vreme slota je između početka i kraja smene
            bool inShift = slotTime >= controller.ShiftStart && slotTime < controller.ShiftEnd;

            // Dodatni uslov za smenu tipa M - ne radi u poslednja dva slota
            if (inShift && controller.ShiftType == "M" && slotIndex >= totalSlots - 2)
            {
                inShift = false;
            }

            return inShift;
        }

        private bool IsFlagS(string controllerCode, DateTime timeSlot, DataTable inicijalniRaspored)
        {
            var controllerShifts = inicijalniRaspored.AsEnumerable().Where(row => row.Field<string>("sifra") == controllerCode).ToList();

            foreach (var shift in controllerShifts)
            {
                DateTime shiftStart = shift.Field<DateTime>("datumOd");
                DateTime shiftEnd = shift.Field<DateTime>("datumDo");
                string flag = shift.Field<string>("Flag")!;

                if (flag == "S" && timeSlot >= shiftStart && timeSlot < shiftEnd)
                {
                    _logger.LogDebug($"Controller {controllerCode} has Flag=S at time {timeSlot}");
                    return true;
                }
            }

            return false;
        }

        private LinearExpr DefineObjective(CpModel model, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
            Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, DataTable inicijalniRaspored)
        {
            // identifikuj manuelne dodele
            var manualAssignments = IdentifyManualAssignments(inicijalniRaspored, controllers, timeSlots);
            var manualAssignmentSet = new HashSet<(int controllerIndex, int timeSlotIndex, string sector)>();

            foreach (var (controllerCode, timeSlotIndex, sector) in manualAssignments)
            {
                int controllerIndex = controllers.IndexOf(controllerCode);
                if (controllerIndex >= 0)
                {
                    manualAssignmentSet.Add((controllerIndex, timeSlotIndex, sector));
                }
            }

            // Inicijalizacija liste termina za funkciju cilja
            var objectiveTerms = new List<LinearExpr>();

            // 1. PENALI ZA NEPOKRIVENE SEKTORE (veoma visok prioritet)
            // PRIORITET #10: Sef smene (SS) radi samo kada nema dovoljno kontrolora
            // i PRIORITET #1-7: Maksimalna iskorišćenost sektora kroz ravnomerno opterećenje
            for (int t = 0; t < timeSlots.Count; t++)
            {
                foreach (var sector in requiredSectors[t])
                {
                    var sectorVars = new List<IntVar>();
                    for (int c = 0; c < controllers.Count; c++)
                    {
                        // Dodaj samo kontrolore koji su u smeni i nemaju Flag="S"
                        if (assignments.TryGetValue((c, t, sector), out var assignment))
                        {
                            var controller = controllerInfo[controllers[c]];
                            bool inShift = IsInShift(controller, timeSlots[t], t, timeSlots.Count);
                            bool isFlagS = IsFlagS(controllers[c], timeSlots[t], inicijalniRaspored);

                            if (inShift && !isFlagS)
                            {
                                sectorVars.Add(assignment);
                            }
                        }
                    }

                    // Kreiraj varijablu koja je 1 ako sektor nije pokriven
                    var sectorNotCovered = model.NewBoolVar($"sector_not_covered_{t}_{sector}");
                    if (sectorVars.Count > 0)
                    {
                        model.Add(LinearExpr.Sum(sectorVars) == 0).OnlyEnforceIf(sectorNotCovered);
                        model.Add(LinearExpr.Sum(sectorVars) > 0).OnlyEnforceIf(sectorNotCovered.Not());

                        // Veoma visok penal za nepokrivene sektore ako ima dostupnih kontrolora
                        objectiveTerms.Add(UNCOVERED_SECTOR_PENALTY * sectorNotCovered);
                    }
                }
            }

            // 2. PENALI ZA SS I SUP RAD (visok prioritet)
            // PRIORITET #9 i #10: Definisanje kada SS i SUP rade
            // - SUP treba da radi na sektorima nižeg opterećenja
            // - SS radi samo kada nema dovoljno kontrolora
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                if (controller.IsShiftLeader || controller.IsSupervisor)
                {
                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        foreach (var sector in requiredSectors[t])
                        {
                            // Smanjeni penali za SS i SUP rad da bi se više koristili za pokrivanje sektora 
                            // kad nema dovoljno kontrolora
                            // Preskočimo penale za manuelne dodele
                            if (manualAssignmentSet.Contains((c, t, sector)))
                            {
                                _logger.LogDebug($"Skipping penalty for manual assignment: Controller {controllers[c]} " + $"at slot {t} on sector {sector}");
                                continue;
                            }


                            int penalty = controller.IsShiftLeader ? 2000 : 500;  // Smanjeno sa 5000/1000
                            objectiveTerms.Add(penalty * assignments[(c, t, sector)]);
                        }
                    }
                }
            }
            //// ========== PRIORITET 2: MINIMIZUJ KORIŠĆENJE SS/SUP (MANJI PRIORITET) ==========
            //// Drastično smanjeni penali da ne interferiraju sa pokrivanjem
            const int SS_WORK_PENALTY = 200;   // Smanjeno sa 2000
            const int SUP_WORK_PENALTY = 100;  // Smanjeno sa 500

            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                if (controller.IsShiftLeader || controller.IsSupervisor)
                {
                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        foreach (var sector in requiredSectors[t])
                        {
                            // Preskačemo penale za manuelne dodele
                            if (manualAssignmentSet.Contains((c, t, sector)))
                            {
                                continue;
                            }

                            int penalty = controller.IsShiftLeader ? SS_WORK_PENALTY : SUP_WORK_PENALTY;
                            objectiveTerms.Add(penalty * assignments[(c, t, sector)]);
                        }
                    }
                }
            }

            // 3. PENALI ZA RAD U POSLEDNJEM SATU SMENE (srednji prioritet)
            // PRIORITET #7: Kontrolor koji je započeo smenu treba da bude slobodan u poslednjem satu
            const int LAST_HOUR_PENALTY = 500; // Smanjeno sa 500
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                // Za poslednja 2 slota (jedan sat)
                for (int t = Math.Max(0, timeSlots.Count - 2); t < timeSlots.Count; t++)
                {
                    foreach (var sector in requiredSectors[t])
                    {
                        // Penal 500 za rad u poslednjem satu
                        objectiveTerms.Add(LAST_HOUR_PENALTY * assignments[(c, t, sector)]);
                    }
                }
            }

            // 4. PENALI ZA KRATKE PAUZE (MANJE OD 1 SAT) - srednji prioritet
            // PRIORITET #3: Pauze od 1 sata između rada na sektoru kao optimalni model
            foreach (var entry in shortBreakVars)
            {
                // Penal 300 za kratke pauze (manje od 1 sat)
                objectiveTerms.Add(300 * entry.Value);
            }

            // 5. PENALI ZA NEPOŠTOVANJE ROTACIJE E/P POZICIJA (niži prioritet)
            // PRIORITET #4: Rotacija kontrolora po pozicijama (E/P)
            foreach (var entry in shouldWorkOnEVars)
            {
                // Penal 200 za nepoštovanje rotacije
                objectiveTerms.Add(200 * entry.Value);
            }

            // Bonus za pravilno rotiranje pozicija
            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 2; t < timeSlots.Count; t++)
                {
                    // Za svaki sektor koji ima poziciju E ili P
                    foreach (var sector in requiredSectors[t].Where(s => s.EndsWith("E") || s.EndsWith("P")))
                    {
                        string baseSector = sector[..^1];
                        string currentPosition = sector[^1..];
                        string alternativePosition = (currentPosition == "E") ? "P" : "E";
                        string alternativeSector = baseSector + alternativePosition;

                        // Ako postoji i alternativni sektor u trenutnom slotu
                        if (requiredSectors[t].Contains(alternativeSector))
                        {
                            // Proveravamo da li je kontrolor rotirao sa dve iste pozicije na novu poziciju
                            // tj. da li je radio u t-2 i t-1 na istoj poziciji, a sada je na drugoj
                            if (t >= 2 && requiredSectors[t - 1].Contains(alternativeSector) && requiredSectors[t - 2].Contains(alternativeSector))
                            {
                                var correctRotation = model.NewBoolVar($"correct_rotation_{c}_{t}_{baseSector}");
                                model.Add(assignments[(c, t - 2, alternativeSector)] == 1).OnlyEnforceIf(correctRotation);
                                model.Add(assignments[(c, t - 1, alternativeSector)] == 1).OnlyEnforceIf(correctRotation);
                                model.Add(assignments[(c, t, sector)] == 1).OnlyEnforceIf(correctRotation);

                                // Dodajemo bonus (negativni penal) za pravilno rotiranje
                                objectiveTerms.Add(-100 * correctRotation);
                            }
                        }
                    }
                }
            }

            // 6. BALANSIRANJE RADNOG OPTEREĆENJA (niži prioritet)
            // PRIORITET #5 i #6: Balansiranje radnog opterećenja i istorija planiranja
            //var workloadVars = new Dictionary<int, IntVar>();
            //for (int c = 0; c < controllers.Count; c++)
            //{
            //    var workingVars = new List<IntVar>();
            //    for (int t = 0; t < timeSlots.Count; t++)
            //    {
            //        foreach (var sector in requiredSectors[t])
            //        {
            //            workingVars.Add(assignments[(c, t, sector)]);
            //        }
            //    }

            //    // Ukupno vreme rada za ovog kontrolora
            //    workloadVars[c] = model.NewIntVar(0, timeSlots.Count, $"workload_{c}");
            //    model.Add(workloadVars[c] == LinearExpr.Sum(workingVars));
            //}

            //// Minimiziranje razlike u radnom opterećenju između kontrolora
            //for (int c1 = 0; c1 < controllers.Count; c1++)
            //{
            //    for (int c2 = c1 + 1; c2 < controllers.Count; c2++)
            //    {
            //        var workloadDiff = model.NewIntVar(0, timeSlots.Count, $"workload_diff_{c1}_{c2}");
            //        model.AddAbsEquality(workloadDiff, workloadVars[c1] - workloadVars[c2]);

            //        // Penal 50 za svaku jedinicu razlike
            //        objectiveTerms.Add(5 * workloadDiff);
            //    }
            //}



            // PRIORITET #1: Eksplicitni prioriteti za trajanje rada
            // Podstičemo formiranje radnih blokova poželjnih dužina (1.5h i 2h)
            for (int c = 0; c < controllers.Count; c++)
            {
                // Za svaki potencijalni početak bloka
                for (int t = 0; t < timeSlots.Count - 1; t++)
                {
                    // Provera da li kontrolor ima pauzu pre slota t (ili je t početak smene)
                    var prevSlotIsBreak = t == 0 ?
                        model.NewConstant(1) :
                        assignments[(c, t - 1, "break")];

                    // Provera da li kontrolor ne radi u slotu t (ima pauzu)
                    var currentSlotIsBreak = assignments[(c, t, "break")];

                    // Ako kontrolor nije na pauzi, proverimo dužinu bloka koji počinje ovde
                    var startsWorkingHere = model.NewBoolVar($"starts_working_{c}_{t}");

                    // Definišemo uslove za startsWorkingHere
                    model.Add(prevSlotIsBreak == 1).OnlyEnforceIf(startsWorkingHere);
                    model.Add(currentSlotIsBreak == 0).OnlyEnforceIf(startsWorkingHere);

                    // Za negirani uslov koristimo AddBoolOr
                    var prevNotBreak = model.NewBoolVar($"prev_not_break_{c}_{t}");
                    var currIsBreak = model.NewBoolVar($"curr_is_break_{c}_{t}");

                    model.Add(prevSlotIsBreak == 0).OnlyEnforceIf(prevNotBreak);
                    model.Add(prevSlotIsBreak == 1).OnlyEnforceIf(prevNotBreak.Not());

                    model.Add(currentSlotIsBreak == 1).OnlyEnforceIf(currIsBreak);
                    model.Add(currentSlotIsBreak == 0).OnlyEnforceIf(currIsBreak.Not());

                    // OR uslov: (prevSlotIsBreak == 0) ILI (currentSlotIsBreak == 1)
                    model.AddBoolOr([prevNotBreak, currIsBreak]).OnlyEnforceIf(startsWorkingHere.Not());

                    // Za svaku dužinu bloka (1-4 slota = 30min, 1h, 1.5h, 2h)
                    for (int length = 1; length <= 4 && t + length <= timeSlots.Count; length++)
                    {
                        var hasBlockOfLength = model.NewBoolVar($"has_block_{c}_{t}_{length}");

                        // Uslovi za blok tačno ove dužine:
                        // 1. Kontrolor počinje da radi u slotu t
                        model.Add(startsWorkingHere == 1).OnlyEnforceIf(hasBlockOfLength);

                        // 2. Kontrolor radi u svim slotovima bloka
                        for (int i = 0; i < length; i++)
                        {
                            model.Add(assignments[(c, t + i, "break")] == 0).OnlyEnforceIf(hasBlockOfLength);
                        }

                        // 3. Kontrolor završava blok (ima pauzu nakon bloka ili je kraj smene)
                        if (t + length < timeSlots.Count)
                        {
                            model.Add(assignments[(c, t + length, "break")] == 1).OnlyEnforceIf(hasBlockOfLength);
                        }

                        // Dodajemo odgovarajuće penale prema dužini bloka
                        switch (length)
                        {
                            case 1: // 30min - visok penal
                                objectiveTerms.Add(BLOCK_30MIN_PENALTY * hasBlockOfLength);
                                break;
                            case 2: // 1h - srednji penal
                                objectiveTerms.Add(BLOCK_1HOUR_PENALTY * hasBlockOfLength);
                                break;
                            case 3: // 1.5h - bonus (negativni penal)
                                objectiveTerms.Add(BLOCK_15HOUR_BONUS * hasBlockOfLength);
                                break;
                            case 4: // 2h - bonus (negativni penal)
                                objectiveTerms.Add(BLOCK_2HOUR_BONUS * hasBlockOfLength);
                                break;
                        }
                    }
                }
            }

            // 7. PREFERIRANJE KONTROLORA SA ISKUSTVOM ZA OPTEREĆENIJE SEKTORE
            // (ovo bi zahtevalo dodatne informacije o iskustvu kontrolora i težini sektora)

            // Dodatni penal za dodelu kontrolora van smene (kao sigurnosni mehanizam)
            const int OUT_OF_SHIFT_PENALTY = 1000000;

            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                for (int t = 0; t < timeSlots.Count; t++)
                {
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count))
                    {
                        foreach (var sector in requiredSectors[t])
                        {
                            // Ogroman penal ako kontrolor radi van svoje smene
                            objectiveTerms.Add(OUT_OF_SHIFT_PENALTY * assignments[(c, t, sector)]);
                        }
                    }
                }
            }

            // Bonus za kontinuitet sektora preko različitih smena
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                for (int t = 1; t < timeSlots.Count; t++)
                {
                    // Slučaj kada je kontrolor na granici između dve smene
                    bool inShiftPrev = IsInShift(controller, timeSlots[t - 1], t - 1, timeSlots.Count);
                    bool inShiftCurr = IsInShift(controller, timeSlots[t], t, timeSlots.Count);

                    if (inShiftPrev && inShiftCurr)
                    {
                        // Za svaki sektor na kojem kontrolor može da radi
                        foreach (var sector in requiredSectors[t - 1].Intersect(requiredSectors[t]))
                        {
                            // Bonus za kontinuitet sektora preko smena
                            var continuityBonus = model.NewBoolVar($"continuity_bonus_{c}_{t}_{sector}");
                            model.Add(assignments[(c, t - 1, sector)] == 1).OnlyEnforceIf(continuityBonus);
                            model.Add(assignments[(c, t, sector)] == 1).OnlyEnforceIf(continuityBonus);

                            // Dodajemo negativni penal (bonus) za kontinuitet
                            objectiveTerms.Add(-200 * continuityBonus);
                        }
                    }
                }
            }

            // Visok penal za višak kontrolora na istom sektoru
            const int EXCESS_CONTROLLERS_PENALTY = 100000;

            for (int t = 0; t < timeSlots.Count; t++)
            {
                foreach (var sector in requiredSectors[t])
                {
                    var sectorVars = new List<IntVar>();
                    for (int c = 0; c < controllers.Count; c++)
                    {
                        sectorVars.Add(assignments[(c, t, sector)]);
                    }

                    // Varijabla koja predstavlja broj kontrolora dodeljenih ovom sektoru
                    var controllersOnSector = model.NewIntVar(0, controllers.Count, $"controllers_on_{t}_{sector}");
                    model.Add(controllersOnSector == LinearExpr.Sum(sectorVars));

                    // Varijabla koja je 1 ako ima više od jednog kontrolora, 0 inače
                    var excessControllers = model.NewBoolVar($"excess_{t}_{sector}");
                    model.Add(controllersOnSector > 1).OnlyEnforceIf(excessControllers);
                    model.Add(controllersOnSector <= 1).OnlyEnforceIf(excessControllers.Not());

                    // Dodaj penal za višak kontrolora
                    objectiveTerms.Add(EXCESS_CONTROLLERS_PENALTY * excessControllers);
                }
            }

            // POSEBAN ZAHTEV ZA NOĆNU SMENU: Duže pauze i SS/SUP treba da pokrivaju sektore
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                // Ako je kontrolor u noćnoj smeni
                if (controller.ShiftType == "N" && !controller.IsShiftLeader && !controller.IsSupervisor)
                {
                    // Penali za kratke pauze u noćnoj smeni (veći nego za druge smene)
                    for (int t = 0; t < timeSlots.Count - 3; t++)
                    {
                        bool allInShift = true;
                        for (int i = 0; i < 4; i++)
                        {
                            if (t + i < timeSlots.Count && !IsInShift(controller, timeSlots[t + i], t + i, timeSlots.Count))
                            {
                                allInShift = false;
                                break;
                            }
                        }

                        if (!allInShift)
                            continue;

                        // Proveri za pauze od samo 30 minuta
                        var workingAtT = model.NewBoolVar($"night_working_at_{c}_{t}");
                        var workingAtTPlus2 = model.NewBoolVar($"night_working_at_{c}_{t + 2}");

                        // Proveri da li kontrolor radi u slotu t
                        var sectorVarsT = new List<IntVar>();
                        foreach (var sector in requiredSectors[t])
                        {
                            sectorVarsT.Add(assignments[(c, t, sector)]);
                        }

                        if (sectorVarsT.Any())
                        {
                            model.Add(LinearExpr.Sum(sectorVarsT) >= 1).OnlyEnforceIf(workingAtT);
                            model.Add(LinearExpr.Sum(sectorVarsT) == 0).OnlyEnforceIf(workingAtT.Not());
                        }
                        else
                        {
                            model.Add(workingAtT == 0);
                        }

                        // Proveri da li kontrolor radi u slotu t+2
                        var sectorVarsTPlus2 = new List<IntVar>();
                        foreach (var sector in requiredSectors[t + 2])
                        {
                            sectorVarsTPlus2.Add(assignments[(c, t + 2, sector)]);
                        }

                        if (sectorVarsTPlus2.Any())
                        {
                            model.Add(LinearExpr.Sum(sectorVarsTPlus2) >= 1).OnlyEnforceIf(workingAtTPlus2);
                            model.Add(LinearExpr.Sum(sectorVarsTPlus2) == 0).OnlyEnforceIf(workingAtTPlus2.Not());
                        }
                        else
                        {
                            model.Add(workingAtTPlus2 == 0);
                        }

                        // Proveri da li ima pauzu u slotu t+1
                        var breakAtTPlus1 = assignments[(c, t + 1, "break")];

                        // Kratka pauza: radi u t, pauza u t+1, radi u t+2
                        var nightShortBreak = model.NewBoolVar($"night_short_break_{c}_{t}");
                        model.Add(workingAtT + breakAtTPlus1 + workingAtTPlus2 >= 3).OnlyEnforceIf(nightShortBreak);
                        model.Add(workingAtT + breakAtTPlus1 + workingAtTPlus2 < 3).OnlyEnforceIf(nightShortBreak.Not());

                        // Veći penal za kratku pauzu u noćnoj smeni nego u drugim smenama - 600 umesto 300
                        objectiveTerms.Add(600 * nightShortBreak);
                    }

                    // Bonus za duže pauze u noćnoj smeni (2 slota ili više)
                    for (int t = 0; t < timeSlots.Count - 3; t++)
                    {
                        bool allInShift = true;
                        for (int i = 0; i < 4; i++)
                        {
                            if (t + i < timeSlots.Count && !IsInShift(controller, timeSlots[t + i], t + i, timeSlots.Count))
                            {
                                allInShift = false;
                                break;
                            }
                        }

                        if (!allInShift)
                            continue;

                        // Kontrolor radi u slotu t
                        var workingAtT = model.NewBoolVar($"night_working_for_long_break_{c}_{t}");
                        var sectorVarsT = new List<IntVar>();
                        foreach (var sector in requiredSectors[t])
                        {
                            sectorVarsT.Add(assignments[(c, t, sector)]);
                        }

                        if (sectorVarsT.Any())
                        {
                            model.Add(LinearExpr.Sum(sectorVarsT) >= 1).OnlyEnforceIf(workingAtT);
                            model.Add(LinearExpr.Sum(sectorVarsT) == 0).OnlyEnforceIf(workingAtT.Not());
                        }
                        else
                        {
                            model.Add(workingAtT == 0);
                        }

                        // Kontrolor ima dugu pauzu (2 ili više slota)
                        var longBreak = model.NewBoolVar($"night_long_break_{c}_{t}");
                        if (t + 3 < timeSlots.Count)
                        {
                            var breakVars = new List<IntVar>();
                            breakVars.Add(assignments[(c, t + 1, "break")]);
                            breakVars.Add(assignments[(c, t + 2, "break")]);

                            model.Add(LinearExpr.Sum(breakVars) >= 2).OnlyEnforceIf(longBreak);
                            model.Add(LinearExpr.Sum(breakVars) < 2).OnlyEnforceIf(longBreak.Not());

                            // Bonus za dugu pauzu (negativni penal)
                            objectiveTerms.Add(-200 * longBreak);
                        }
                    }
                }
            }

            // Penali za nepokrivene sektore u noćnoj smeni kada kontrolori imaju pauzu
            foreach (var entry in shouldCoverNightShift)
            {
                // Visok penal (500) ako kontrolori u noćnoj imaju pauzu, a SS ili SUP ne pokrivaju sektor
                objectiveTerms.Add(500 * entry.Value);
            }

            // FAKTORI ZA NOĆNU SMENU
            // 1. Veoma visok penal za propuštenu priliku da SS/SUP preuzmu sektor
            foreach (var entry in nightShiftMissedOpportunities)
            {
                // Izuzetno visok penal (5000) ako SS/SUP ne pokriva sektor kada je dostupan
                objectiveTerms.Add(5000 * entry.Value);
            }

            // 2. Veoma visok bonus za duge pauze (2h) običnih kontrolora
            foreach (var entry in nightShiftLongBreaks)
            {
                // Značajan bonus (-2000) za duge pauze kontrolora
                objectiveTerms.Add(-2000 * entry.Value);
            }

            // 3. Veoma visok penal za duge periode rada (1.5h) običnih kontrolora
            foreach (var entry in nightShiftLongWorkPeriods)
            {
                // Visok penal (3000) za duge periode rada bez pauze
                objectiveTerms.Add(3000 * entry.Value);
            }

            // 4. Dodatni penali/bonusi za rad u noćnoj smeni
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                foreach (int t in nightShiftSlots)
                {
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count))
                        continue;

                    // Ako je običan kontrolor (nije SS/SUP)
                    if (!controller.IsShiftLeader && !controller.IsSupervisor && controller.ShiftType == "N")
                    {
                        // Bonus za svaku pauzu običnih kontrolora u noćnoj smeni
                        objectiveTerms.Add(-1000 * assignments[(c, t, "break")]);

                        // Dodatni penal za rad na bilo kom sektoru
                        foreach (var sector in requiredSectors[t])
                        {
                            objectiveTerms.Add(800 * assignments[(c, t, sector)]);
                        }
                    }

                    // Ako je SS ili SUP - bonus za rad
                    if (controller.IsShiftLeader || controller.IsSupervisor)
                    {
                        foreach (var sector in requiredSectors[t])
                        {
                            // Bonus za SS/SUP u noćnoj smeni - poništava standardne penale za SS/SUP rad
                            objectiveTerms.Add(-2000 * assignments[(c, t, sector)]);
                        }
                    }
                }
            }



            // 5. BALANSIRANJE OPTEREĆENJA KONTROLORA U NOĆNOJ SMENI
            // Penalizuj razliku u radnom vremenu između kontrolora
            if (nightShiftWorkloadDifference != null)
            {
                // Vrlo visok penal za razliku u opterećenju - 1000 po jednom slotu razlike
                objectiveTerms.Add(1000 * nightShiftWorkloadDifference);
            }

            // 6. PODSTIČI RAZNOVRSNOST SEKTORA
            foreach (var entry in nightShiftSectorTypeCoverage)
            {
                // Negativni penal (bonus) za rad na različitim tipovima sektora
                objectiveTerms.Add(-500 * entry.Value);
            }

            // 7. PENALIZUJ DUGAČKE PERIODE KONTINUIRANOG RADA
            foreach (var entry in nightShiftConsecutiveWork)
            {
                // Visok penal za 3 ili više uzastopnih slotova rada
                objectiveTerms.Add(2000 * entry.Value);
            }

            // PRAVEDNIJE BALANSIRANJE RADA SVIH KONTROLORA U NOĆNOJ SMENI
            if (nightShiftSlots.Count > 0 && (ssControllers.Count > 0 || supControllers.Count > 0 || nightShiftControllers.Count > 0))
            {
                _logger.LogInformation("Implementing fair workload distribution between all controllers in night shift");

                // 1. Računanje radnog vremena za sve kontrolore (obične, SS i SUP)
                var allNightShiftControllers = nightShiftControllers.Concat(ssControllers).Concat(supControllers).ToList();
                var nightWorkloadVars = new Dictionary<int, IntVar>();

                foreach (int c in allNightShiftControllers)
                {
                    var workingSlots = new List<IntVar>();
                    foreach (int t in nightShiftSlots)
                    {
                        if (IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
                        {
                            var isWorking = model.NewBoolVar($"is_working_night_{c}_{t}");
                            model.Add(assignments[(c, t, "break")] == 0).OnlyEnforceIf(isWorking);
                            model.Add(assignments[(c, t, "break")] == 1).OnlyEnforceIf(isWorking.Not());
                            workingSlots.Add(isWorking);
                        }
                    }

                    // Ukupno radno vreme kontrolora u noćnoj smeni
                    nightWorkloadVars[c] = model.NewIntVar(0, nightShiftSlots.Count, $"night_workload_{c}");
                    model.Add(nightWorkloadVars[c] == LinearExpr.Sum(workingSlots));

                    _logger.LogInformation($"Tracking night workload for controller {controllers[c]}");
                }

                // 2. Postavljanje cilja za ravnomerno radno opterećenje
                // Dodaćemo penale za razliku u radnom vremenu IZMEĐU SVIH kontrolora
                for (int i = 0; i < allNightShiftControllers.Count; i++)
                {
                    for (int j = i + 1; j < allNightShiftControllers.Count; j++)
                    {
                        int c1 = allNightShiftControllers[i];
                        int c2 = allNightShiftControllers[j];

                        var workloadDiff = model.NewIntVar(0, nightShiftSlots.Count, $"night_workload_diff_{c1}_{c2}");
                        model.AddAbsEquality(workloadDiff, nightWorkloadVars[c1] - nightWorkloadVars[c2]);

                        // VISOK PENAL za razliku u radnom vremenu - ovo je ključno za ravnomerno radno vreme
                        objectiveTerms.Add(2000 * workloadDiff);

                        _logger.LogInformation($"Added balancing constraint between controllers {controllers[c1]} and {controllers[c2]}");
                    }
                }

                // 3. UKLANJANJE svih postojećih bonusa/penala za rad SS/SUP u noćnoj smeni
                // Ovo je važno jer želimo da optimizator tretira sve kontrolore jednako

                // Umesto toga, koristićemo striktno manje penale za SS/SUP
                foreach (int c in ssControllers.Concat(supControllers))
                {
                    foreach (int t in nightShiftSlots)
                    {
                        if (IsInShift(controllerInfo[controllers[c]], timeSlots[t], t, timeSlots.Count))
                        {
                            foreach (var sector in requiredSectors[t])
                            {
                                // Mali penal (50) za SS/SUP rad - dovoljno da preferiraju običnog KL ako je dostupan,
                                // ali ne dovoljno da dramatično utiče na balans
                                objectiveTerms.Add(50 * assignments[(c, t, sector)]);
                            }
                        }
                    }
                }

                // 4. Ograničenja koja obezbeđuju da SS i SUP mogu da pokriju pozicije kada nema drugih kontrolora
                // Ovo će omogućiti da se raspoloživi kontrolori optimalno rasporede

                // 5. DODATNO: Minimalno očekivano radno vreme za sve kontrolore
                // Ovo će osigurati da svi kontrolori imaju razumnu količinu rada
                int minExpectedWork = Math.Max(1, nightShiftSlots.Count / 3); // Najmanje trećina ukupnog vremena u smeni

                foreach (int c in allNightShiftControllers)
                {
                    // Blagi podsticaj (ne čvrsto ograničenje) da svaki kontrolor radi minimalni broj slotova
                    var belowMinWork = model.NewBoolVar($"below_min_work_{c}");
                    model.Add(nightWorkloadVars[c] < minExpectedWork).OnlyEnforceIf(belowMinWork);
                    model.Add(nightWorkloadVars[c] >= minExpectedWork).OnlyEnforceIf(belowMinWork.Not());

                    // Manji penal za nedovoljno rada
                    objectiveTerms.Add(500 * belowMinWork);
                }
            }

            if (_controllersWithLicense == null)
            {
                _logger.LogWarning("Controllers with license list is not initialized");
                _controllersWithLicense = new List<string>(); // Inicijalizujemo praznu listu kao fallback
            }

            // Dodatni faktor za FMP pozicije
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];
                bool isFMP = controller.ORM == "FMP";
                bool hasLicense = _controllersWithLicense.Contains(controllers[c]);

                if (isFMP && hasLicense)
                {
                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        // Za FMP kontrolore sa licencom, dajemo bonus za rad na FMP sektorima
                        foreach (var sector in requiredSectors[t].Where(s => s.Contains("FMP")))
                        {
                            // Bonus (negativni penal) za FMP kontrolore sa licencom
                            objectiveTerms.Add(-500 * assignments[(c, t, sector)]);
                        }

                        // Za ostale sektore, dodajemo manji penal
                        foreach (var sector in requiredSectors[t].Where(s => !s.Contains("FMP") && !s.Equals("break")))
                        {
                            // Manji penal (200) za rad na drugim sektorima
                            objectiveTerms.Add(200 * assignments[(c, t, sector)]);
                        }
                    }
                }
                else if (isFMP && !hasLicense)
                {
                    // Za FMP kontrolore bez licence, dodajemo visok penal za rad na bilo kom sektoru
                    // jer oni ne bi trebali raditi na kontrolorskim pozicijama
                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        foreach (var sector in requiredSectors[t].Where(s => !s.Equals("break")))
                        {
                            // Visok penal (5000) za FMP bez licence
                            objectiveTerms.Add(5000 * assignments[(c, t, sector)]);
                        }
                    }
                }
                else
                {
                    // Za ostale kontrolore, dodajemo visok penal za rad na FMP sektorima
                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        foreach (var sector in requiredSectors[t].Where(s => s.Contains("FMP")))
                        {
                            // Visok penal (2000) za ne-FMP kontrolore na FMP sektorima
                            objectiveTerms.Add(2000 * assignments[(c, t, sector)]);
                        }
                    }
                }
            }

            // Nagradi 4-slot radne blokove (negativan penal = nagrada)
            foreach (var (key, preferredBlockVar) in preferredWorkBlocks)
            {
                objectiveTerms.Add(preferredBlockVar * -20); // Nagrada za preferirane blokove
            }

            // Penalizuj fragmentovan rad
            foreach (var (key, fragmentVar) in fragmentedWorkPenalties)
            {
                objectiveTerms.Add(fragmentVar * 30); // Penal za fragmentaciju
            }

            return LinearExpr.Sum(objectiveTerms);
        }

        private OptimizationResponse CreateOptimizationResponse(CpSolver solver, Dictionary<(int, int, string), IntVar> assignments, CpSolverStatus status, List<string> controllers,
            List<DateTime> timeSlots, Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, DataTable inicijalniRaspored, DateTime datum)
        {
            var optimizedResults = new List<OptimizationResultDTO>();

            if (status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible)
            {
                // konvertuj resenje u listu rezultata optimizacije
                for (int c = 0; c < controllers.Count; c++) {
                    var controllerCode = controllers[c];
                    var controllerData = controllerInfo[controllerCode];

                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        DateTime timeSlot = timeSlots[t];
                        bool inShift = timeSlot >= controllerData.ShiftStart && timeSlot < controllerData.ShiftEnd;

                        if (inShift && controllerData.ShiftType == "M" && t >= timeSlots.Count - 2)
                        {
                            inShift = false;
                        }

                        if (inShift)
                        {
                            string assignedSector = string.Empty;

                            // provera da li je kl na pauzi
                            bool onBreak = solver.Value(assignments[(c, t, "break")]) > 0.5;

                            if (!onBreak)
                            {
                                // pronadji kojim sektorima je kl dodeljen
                                foreach (var sector in requiredSectors[t])
                                {
                                    if (solver.Value(assignments[(c, t, sector)]) > 0.5)
                                    {
                                        assignedSector = sector;
                                        break;
                                    }
                                }
                            }

                            optimizedResults.Add(new OptimizationResultDTO
                            {
                                Sifra = controllerCode,
                                PrezimeIme = controllerData.Name,
                                Smena = controllerData.ShiftType,
                                Datum = datum,
                                DatumOd = timeSlot,
                                DatumDo = timeSlot.AddMinutes(_slotDurationMinutes),
                                Sektor = assignedSector, // null ako je na pauzi (break)
                                ORM = controllerData.ORM,
                                Flag = this.IsFlagS(controllerCode, timeSlot, inicijalniRaspored) ? "S" : null,
                                VremeStart = controllerData.VremeStart
                            });
                        }
                    }
                }
            }
            else
            {
                // NOVA FUNKCIONALNOST: Analiziraj razloge neuspešnosti
                AnalyzeFailureReasons(status, assignments, controllers, timeSlots, requiredSectors, controllerInfo, inicijalniRaspored);

                // Ako nemamo validno rešenje, vratimo praznu listu rezultata sa adekvatnim statusom
                _logger.LogWarning($"Solver finished with status: {status}, no solution available");
                return new OptimizationResponse
                {
                    OptimizedResults = new List<OptimizationResultDTO>(),
                    NonOptimizedResults = new List<OptimizationResultDTO>(),
                    AllResults = new List<OptimizationResultDTO>(),
                    Statistics = new OptimizationStatistics
                    {
                        SolutionStatus = $"{status} - See logs for details",
                        ObjectiveValue = 0,
                        SuccessRate = 0
                    },
                    SlotShortages = new Dictionary<string, int>(),
                    ConfigurationLabels = BuildConfigurationLabels(timeSlots),
                    InitialAssignments = BuildInitialAssignments(inicijalniRaspored)
                };
            }

            var statistics = this.CalculateStatistics(solver, assignments, status, controllers, timeSlots, requiredSectors);
            var shortages = this.CalculateSlotShortages(solver, assignments, controllers, timeSlots, requiredSectors);
            var configLabels = BuildConfigurationLabels(timeSlots);

            return new OptimizationResponse
            {
                OptimizedResults = optimizedResults,
                NonOptimizedResults = [],
                AllResults = optimizedResults,
                Statistics = statistics,
                SlotShortages = shortages,
                ConfigurationLabels = configLabels,
                InitialAssignments = this.BuildInitialAssignments(inicijalniRaspored)
            };
        }

        private void AnalyzeFailureReasons(CpSolverStatus status, Dictionary<(int, int, string), IntVar> assignments,
                    List<string> controllers, List<DateTime> timeSlots, Dictionary<int, List<string>> requiredSectors,
                    Dictionary<string, ControllerInfo> controllerInfo, DataTable inicijalniRaspored)
        {
            _logger.LogError("=== ANALYZING FAILURE REASONS ===");
            _logger.LogError("Solver status: {Status}", status);

            switch (status)
            {
                case CpSolverStatus.Infeasible:
                    _logger.LogError("The model is INFEASIBLE - constraints cannot be satisfied");
                    AnalyzeInfeasibilityReasons(controllers, timeSlots, requiredSectors, controllerInfo, inicijalniRaspored);
                    break;

                case CpSolverStatus.Unknown:
                    _logger.LogError("Solver could not determine feasibility within time limit");
                    _logger.LogError("Try increasing maxExecTime or simplifying constraints");
                    break;

                case CpSolverStatus.ModelInvalid:
                    _logger.LogError("The model is INVALID - check constraint definitions");
                    break;

                default:
                    _logger.LogError("Unexpected solver status: {Status}", status);
                    break;
            }
        }

        private void AnalyzeInfeasibilityReasons(List<string> controllers, List<DateTime> timeSlots,
                        Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo,
                        DataTable inicijalniRaspored)
        {
            _logger.LogError("Analyzing infeasibility reasons:");

            // 1. Proveri osnovnu dostupnost kontrolora
            var totalControllers = controllers.Count;
            var maxSectorsInSlot = requiredSectors.Values.Max(sectors => sectors.Count);

            _logger.LogError("Basic capacity check:");
            _logger.LogError("- Total controllers: {Total}", totalControllers);
            _logger.LogError("- Max sectors per slot: {MaxSectors}", maxSectorsInSlot);

            if (totalControllers < maxSectorsInSlot)
            {
                _logger.LogError("FUNDAMENTAL PROBLEM: Not enough controllers overall!");
                return;
            }

            // 2. Proveri Flag="S" probleme
            var flagSCounts = new Dictionary<int, int>();
            for (int t = 0; t < timeSlots.Count; t++)
            {
                int flagSCount = 0;
                foreach (var controller in controllers)
                {
                    if (IsFlagS(controller, timeSlots[t], inicijalniRaspored))
                        flagSCount++;
                }
                flagSCounts[t] = flagSCount;

                int available = totalControllers - flagSCount;
                int required = requiredSectors[t].Count;

                if (available < required)
                {
                    _logger.LogError("Slot {Slot} ({Time}): {FlagS} controllers have Flag=S, leaving {Available} available for {Required} sectors",
                        t, timeSlots[t].ToString("HH:mm"), flagSCount, available, required);
                }
            }

            // 3. Proveri shift probleme
            for (int t = 0; t < timeSlots.Count; t++)
            {
                int outOfShift = 0;
                foreach (var controllerCode in controllers)
                {
                    var controller = controllerInfo[controllerCode];
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count))
                        outOfShift++;
                }

                int available = totalControllers - outOfShift - flagSCounts[t];
                int required = requiredSectors[t].Count;

                if (available < required)
                {
                    _logger.LogError("Slot {Slot} ({Time}): {OutOfShift} out of shift + {FlagS} Flag=S = {Available} available for {Required} sectors",
                        t, timeSlots[t].ToString("HH:mm"), outOfShift, flagSCounts[t], available, required);
                }
            }
        }

        private OptimizationStatistics CalculateStatistics(CpSolver solver, Dictionary<(int, int, string), IntVar> assignments, CpSolverStatus status, List<string> controllers,
            List<DateTime> timeSlots, Dictionary<int, List<string>> requiredSectors)
        {
            var stats = new OptimizationStatistics
            {
                SolutionStatus = status switch
                {
                    CpSolverStatus.Optimal => "Optimal",
                    CpSolverStatus.Feasible => "Feasible",
                    CpSolverStatus.Infeasible => "Infeasible",
                    _ => "Unknown"
                }
            };

            if (status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible)
            {
                int totalRequiredSectors = 0;
                int coveredSectors = 0;
                int slotWithShortage = 0;
                int slotWithExcess = 0;

                // za svaki time slot
                for (int t = 0;  t < timeSlots.Count; t++)
                {
                    var sectors = requiredSectors[t];
                    totalRequiredSectors += sectors.Count;

                    // proveri pokrivenost svakog sektora
                    foreach (var sector in sectors)
                    {
                        bool isCovered = false;
                        int assignedControllers = 0;

                        for (int c = 0; c < controllers.Count; c++)
                        {
                            if (solver.Value(assignments[(c, t, sector)]) > 0.5)
                            {
                                isCovered = true;
                                assignedControllers++;
                            }
                        }

                        if (isCovered)
                        {
                            coveredSectors++;
                        }
                        else
                        {
                            slotWithShortage++;
                        }

                        // ukoliko ima vise od jednog kl po sektoru
                        if (assignedControllers > 1)
                        {
                            slotWithExcess++;
                        }
                    }
                }

                stats.SuccessRate = totalRequiredSectors > 0 ? (double)coveredSectors / totalRequiredSectors * 100 : 100.0;

                stats.SlotsWithShortage = slotWithShortage;
                stats.SlotsWithExcess = slotWithExcess;

                // racunanje distribucije radnog opterecenja kl
                var controllerWorkload = new Dictionary<int, int>();
                for (int c = 0; c < controllers.Count; c++)
                {
                    int workMinutes = 0;

                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        bool onBreak = solver.Value(assignments[(c, t, "break")]) > 0.5;
                        if (!onBreak)
                        {
                            workMinutes += _slotDurationMinutes;
                        }
                    }

                    controllerWorkload[c] = workMinutes;
                }

                // racunanje maksimalne razlike u radnom opterecenju
                if (controllerWorkload.Count > 0)
                {
                    int maxWorkload = controllerWorkload.Values.Max();
                    int minWorkload = controllerWorkload.Values.Min();
                    stats.MaxWorkHourDifference = (double)(maxWorkload - minWorkload) / 60.0;
                }

                stats.MissingExecutors = this.CalculateMissingExecutors(solver, assignments, controllers, timeSlots, requiredSectors);

                stats.ObjectiveValue = solver.ObjectiveValue;

                stats.BreakCompliance = this.CalculateBreakCompliance(solver, assignments, controllers, timeSlots);

                stats.RotationCompliance = this.CalculateRotationCompliance(solver, assignments, controllers, timeSlots, requiredSectors);

                stats.EmployeesWithShortage = this.CalculateEmployeesWithShortage(controllerWorkload, timeSlots.Count);

                stats.WallTime = solver.WallTime();

            }

            return stats;
        }

        private int CalculateMissingExecutors(CpSolver solver, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
            Dictionary<int, List<string>> requiredSectors)
        {
            int maxMissingExecutors = 0;

            // za svaki timeslot
            for (int t = 0; t < timeSlots.Count; t++)
            {
                var sectors = requiredSectors[t];
                int uncoveredSectors = 0;

                foreach (var sector in sectors)
                {
                    bool isCovered = false;

                    for (int c = 0; c < controllers.Count; c++)
                    {
                        if (solver.Value(assignments[(c, t, sector)]) > 0.5)
                        {
                            isCovered = true;
                            break;
                        }
                    }

                    if (!isCovered)
                    {
                        uncoveredSectors++;
                    }
                }

                maxMissingExecutors = Math.Max(maxMissingExecutors, uncoveredSectors);
            }

            return maxMissingExecutors;
        }

        private double CalculateBreakCompliance(CpSolver solver, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots)
        {
            double totalRestTime = 0;
            double totalTime = timeSlots.Count * _slotDurationMinutes * controllers.Count;

            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 0; t < timeSlots.Count; t++) { 
                    if (solver.Value(assignments[(c, t, "break")]) > 0.5)
                    {
                        totalRestTime += _slotDurationMinutes;
                    }
                }
            }

            double actualRestPercentage = (totalRestTime / totalTime) * 100;
            double targetRestPercentage = 25.0; // cilj: 25% vremena bi brebao da bude odmor

            return Math.Min(100, (actualRestPercentage / targetRestPercentage) * 100);
        }

        private double CalculateRotationCompliance(CpSolver solver, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, List<DateTime> timeSlots,
            Dictionary<int, List<string>> requiredSectors)
        {
            var positionBalance = new Dictionary<int, (int ExecutiveTime, int PlannerTime)>();

            for (int c = 0; c < controllers.Count; c++)
            {
                int executiveTime = 0;
                int plannerTime = 0;

                for (int t = 0; t < timeSlots.Count; t++)
                {
                    foreach (var sector in requiredSectors[t])
                    {
                        // Provera da li ključ postoji u rečniku
                        if (assignments.TryGetValue((c, t, sector), out var varValue) && solver.Value(varValue) > 0.5)
                        {
                            if (sector.EndsWith("E"))
                            {
                                executiveTime += _slotDurationMinutes;
                            }
                            else if (sector.EndsWith("P"))
                            {
                                plannerTime += _slotDurationMinutes;
                            }
                        }
                    }
                }

                positionBalance[c] = (executiveTime, plannerTime);
            }

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

            return positionBalance.Count > 0 ? (double)controllersWithGoodBalance / positionBalance.Count * 100 : 0;
        }

        private int CalculateEmployeesWithShortage(Dictionary<int, int> controllerWorkLoad, int totalSlots)
        {
            int minRequiredWorkMinutes = (int)(totalSlots * _slotDurationMinutes * 0.75 / controllerWorkLoad.Count);
            int employeesWithShortage = 0;

            foreach (var workload in controllerWorkLoad.Values)
            {
                if (workload < minRequiredWorkMinutes)
                {
                    employeesWithShortage++;
                }
            }

            return employeesWithShortage;
        }

        private Dictionary<string, int> CalculateSlotShortages(CpSolver solver, Dictionary<(int, int, string), IntVar> assignments, List<string> controllers, 
            List<DateTime> timeSlots, Dictionary<int, List<string>> requiredSectors)
        {
            var shortages = new Dictionary<string, int>();

            for (int t = 0; t < timeSlots.Count; t++)
            {
                DateTime slotStart = timeSlots[t];
                DateTime slotEnd = slotStart.AddMinutes(_slotDurationMinutes);
                string timeKey = $"{slotStart:yyyy-MM-dd HH:mm:ss}|{slotEnd:yyyy-MM-dd HH:mm:ss}";

                int shortage = 0;

                foreach (var sector in requiredSectors[t])
                {
                    bool isCovered = false;

                    for (int c = 0; c < controllers.Count; c++)
                    {
                        if (solver.Value(assignments[(c, t, sector)]) > 0.5)
                        {
                            isCovered = true;
                            break;
                        }
                    }

                    if (!isCovered)
                    {
                        shortage++;
                    }
                }

                if (shortage > 0)
                {
                    shortages[timeKey] = shortage;
                }
            }

            return shortages;
        }

        private Dictionary<string, string> BuildConfigurationLabels(List<DateTime> timeSlots)
        {
            var configLabels = new Dictionary<string, string>();

            foreach (var slotStart in timeSlots)
            {
                DateTime slotEnd = slotStart.AddMinutes(_slotDurationMinutes);
                string timeKey = $"{slotStart:yyyy-MM-dd HH:mm:ss}|{slotEnd:yyyy-MM-dd HH:mm:ss}";

                // Koristi _konfiguracije ako je dostupno
                if (_konfiguracije != null)
                {
                    try
                    {
                        // Pronađi konfiguracije za ovaj vremenski slot iz tabele konfiguracije
                        var txConfigs = _konfiguracije.AsEnumerable()
                            .Where(row => row.Field<DateTime>("datumOd") <= slotStart &&
                                          row.Field<DateTime>("datumDo") > slotStart &&
                                          row.Field<string>("ConfigType") == "TX")
                            .Select(row => row.Field<string>("Konfiguracija"))
                            .Where(k => k != null)
                            .Distinct()
                            .ToList();

                        var luConfigs = _konfiguracije.AsEnumerable()
                            .Where(row => row.Field<DateTime>("datumOd") <= slotStart &&
                                          row.Field<DateTime>("datumDo") > slotStart &&
                                          row.Field<string>("ConfigType") == "LU")
                            .Select(row => row.Field<string>("Konfiguracija"))
                            .Where(k => k != null)
                            .Distinct()
                            .ToList();

                        var allConfigs = _konfiguracije.AsEnumerable()
                            .Where(row => row.Field<DateTime>("datumOd") <= slotStart &&
                                          row.Field<DateTime>("datumDo") > slotStart &&
                                          row.Field<string>("ConfigType") == "ALL")
                            .Select(row => row.Field<string>("Konfiguracija"))
                            .Where(k => k != null)
                            .Distinct()
                            .ToList();


                        string label = "";
                        if (txConfigs.Count != 0)
                        {
                            label += $"TX:{string.Join(",", txConfigs)}";
                        }
                        if (luConfigs.Count != 0)
                        {
                            if (!string.IsNullOrEmpty(label))
                            {
                                label += " | ";
                            }
                            label += $"LU:{string.Join(",", luConfigs)}";
                        }

                        if (allConfigs.Count != 0)
                        {
                            label = $"{string.Join(",", allConfigs)}";
                        }

                        configLabels[timeKey] = label;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error getting configuration labels for time slot {TimeSlot}", slotStart);
                        configLabels[timeKey] = "Error";
                    }
                }
                else
                {
                    configLabels[timeKey] = "N/A";
                }
            }

            return configLabels;
        }

        private List<InitialAssignmentDTO> BuildInitialAssignments(DataTable inicijalniRaspored)
        {
            var initialAssignments = new List<InitialAssignmentDTO>();

            foreach (DataRow row in inicijalniRaspored.Rows)
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

        private List<(string controllerCode, int timeSlotIndex, string sector)> IdentifyManualAssignments(DataTable inicijalniRaspored, List<string> controllers, List<DateTime> timeSlots)
        {
            var manualAssignments = new List<(string controllerCode, int timeSlotIndex, string sector)>();

            foreach (DataRow row in inicijalniRaspored.Rows)
            {
                string controllerCode = row.Field<string>("sifra");
                string sector = row.Field<string>("sektor");

                // ako je sektor popunjen, ovo je manuelna dodela
                if (!string.IsNullOrEmpty(sector))
                {
                    DateTime datumOd = row.Field<DateTime>("datumOd");

                    // pronadji odgovarajuci timeslot
                    int timeSlotIndex = timeSlots.FindIndex(ts => ts == datumOd);

                    if (timeSlotIndex >= 0)
                    {
                        manualAssignments.Add((controllerCode, timeSlotIndex, sector));
                    }
                }
            }

            return manualAssignments;
        }

        private void AnalyzeCoverageBeforeSolving(Dictionary<(int, int, string), IntVar> assignments,
                       List<string> controllers, List<DateTime> timeSlots,
                       Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo,
                       DataTable inicijalniRaspored)
        {
            _logger.LogInformation("=== COVERAGE ANALYSIS BEFORE SOLVING ===");

            for (int t = 0; t < timeSlots.Count; t++)
            {
                DateTime slotTime = timeSlots[t];
                int availableControllers = 0;
                int requiredSectorCount = requiredSectors[t].Count;

                // Broji dostupne kontrolore za ovaj slot
                for (int c = 0; c < controllers.Count; c++)
                {
                    var controller = controllerInfo[controllers[c]];
                    bool inShift = IsInShift(controller, timeSlots[t], t, timeSlots.Count);
                    bool isFlagS = IsFlagS(controllers[c], timeSlots[t], inicijalniRaspored);

                    if (inShift && !isFlagS)
                    {
                        availableControllers++;
                    }
                }

                if (availableControllers < requiredSectorCount)
                {
                    _logger.LogWarning("Slot {TimeSlot}: {Available} controllers available, {Required} sectors required - SHORTAGE!",
                        slotTime.ToString("HH:mm"), availableControllers, requiredSectorCount);

                    // Detaljno logiraj ko je dostupan
                    var availableList = new List<string>();
                    for (int c = 0; c < controllers.Count; c++)
                    {
                        var controller = controllerInfo[controllers[c]];
                        bool inShift = IsInShift(controller, timeSlots[t], t, timeSlots.Count);
                        bool isFlagS = IsFlagS(controllers[c], timeSlots[t], inicijalniRaspored);

                        if (inShift && !isFlagS)
                        {
                            string type = controller.IsShiftLeader ? "SS" : controller.IsSupervisor ? "SUP" : "REG";
                            availableList.Add($"{controllers[c]}({type})");
                        }
                    }
                    _logger.LogWarning("Available controllers: {Controllers}", string.Join(", ", availableList));
                    _logger.LogWarning("Required sectors: {Sectors}", string.Join(", ", requiredSectors[t]));
                }
                else
                {
                    _logger.LogDebug("Slot {TimeSlot}: {Available} controllers, {Required} sectors - OK",
                        slotTime.ToString("HH:mm"), availableControllers, requiredSectorCount);
                }
            }

            _logger.LogInformation("=== END COVERAGE ANALYSIS ===");
        }

    }

   
    }
