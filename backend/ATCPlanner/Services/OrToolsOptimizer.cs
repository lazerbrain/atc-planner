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

        private static readonly HashSet<string> NON_OPERATIONAL_SECTORS = new HashSet<string>
        {
            "break", "SS", "SUP", "FMP", "SBY", "BRF"
        };

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

                // Ako se NE koriste manuelne dodele, očisti sve sektore iz tabele ***
                if (!useManualAssignments)
                {
                    _logger.LogWarning("useManualAssignments = FALSE - CLEARING all sectors from inicijalniRaspored table");

                    int clearedCount = 0;
                    foreach (DataRow row in inicijalniRaspored.Rows)
                    {
                        string currentSector = row.Field<string>("sektor");
                        if (!string.IsNullOrEmpty(currentSector))
                        {
                            row["sektor"] = DBNull.Value; // Postavi na NULL
                            clearedCount++;
                        }
                    }

                    _logger.LogWarning($"Cleared {clearedCount} sectors from inicijalniRaspored table");

                    // Verifikuj da su stvarno očišćeni
                    int remainingAssignments = inicijalniRaspored.AsEnumerable().Count(row => !string.IsNullOrEmpty(row.Field<string>("sektor")));
                    _logger.LogWarning($"After clearing, {remainingAssignments} sectors remain (should be 0)");
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

                var manualAssignments = IdentifyManualAssignments(inicijalniRaspored, controllers, timeSlots);
                var manualAssignmentsByController = new Dictionary<int, Dictionary<int, string>>();

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

                // kreiraj i resi model
                var model = new CpModel();

                // kreiraj varijable odlucivanja za model
                var assignments = this.CreateAssignmentVariables(model, controllers, timeSlots, requiredSectors);

                // dodaj ogranicenja u model
                this.AddConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, inicijalniRaspored, useManualAssignments);

                // definisanje funkcije cilja
                var objective = this.DefineObjective(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, inicijalniRaspored, manualAssignmentsByController, useManualAssignments);
                model.Minimize(objective);

                // resavanje modela
                _logger.LogInformation("Solving CP-SAT model");
                //var solver = new CpSolver
                //{
                //    StringParameters = $"max_time_in_seconds:{maxExecTime};log_search_progress:true"
                //};
                var solver = new CpSolver();

                // Dodaj randomizaciju
                //   string solverParams = $"max_time_in_seconds:{maxExecTime};log_search_progress:true";
                string solverParams = $"max_time_in_seconds:{maxExecTime};log_search_progress:true;num_workers:8;relative_gap_limit:0.02;cp_model_presolve:true";

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
                        IsFMP = (firstRow.Field<string>("ORM") ?? "") == "FMP",
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

        // Zameni CreateAssignmentVariables metodu u OrToolsOptimizer.cs

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

                    // *** DODATO: Dodaj sve neoperativne sektore (SS, SUP, FMP) za svaki slot ***
                    // Ovo omogućava manuelne dodele na ove sektore
                    foreach (var nonOpSector in NON_OPERATIONAL_SECTORS)
                    {
                        if (nonOpSector != "break") // break je već dodat
                        {
                            var key = (c, t, nonOpSector);
                            if (!assignments.ContainsKey(key)) // proveri da već nije dodat u requiredSectors
                            {
                                assignments[key] = model.NewBoolVar($"controller_{c}_slot_{t}_sector_{nonOpSector}");
                            }
                        }
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
                    bool inShiftPrev = IsInShift(controller, timeSlots[t - 1], t - 1, timeSlots.Count, manualAssignmentsByController, c);
                    bool inShiftCurr = IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c);

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
                        if (!IsInShift(controller, timeSlots[t + i], t + i, timeSlots.Count, manualAssignmentsByController, c))
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
                    bool inShiftCurrent = IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c);
                    bool inShiftNext = t + 1 < timeSlots.Count && IsInShift(controller, timeSlots[t + 1], t + 1, timeSlots.Count, manualAssignmentsByController, c);

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
                            if (!IsInShift(controller, timeSlots[t - i], t - i, timeSlots.Count, manualAssignmentsByController, c))
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
                            if (t + 2 < timeSlots.Count && IsInShift(controller, timeSlots[t + 2], t + 2, timeSlots.Count, manualAssignmentsByController, c))
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
                        if (!IsInShift(controller, timeSlots[t + i], t + i, timeSlots.Count, manualAssignmentsByController, c))
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
                        IsInShift(controller, timeSlots[t + 4], t + 4, timeSlots.Count, manualAssignmentsByController, c) &&
                        IsInShift(controller, timeSlots[t + 5], t + 5, timeSlots.Count, manualAssignmentsByController, c))
                    {
                        model.Add(assignments[(c, t + 4, "break")] + assignments[(c, t + 5, "break")] >= 2)
                             .OnlyEnforceIf(works4Slots);
                    }
                    else if (t + 4 < timeSlots.Count &&
                            IsInShift(controller, timeSlots[t + 4], t + 4, timeSlots.Count, manualAssignmentsByController, c))
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
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c) ||
                        !IsInShift(controller, timeSlots[t + 1], t + 1, timeSlots.Count, manualAssignmentsByController, c))
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
                        if (!IsInShift(controller, timeSlots[t + 1 + len], t + 1 + len, timeSlots.Count, manualAssignmentsByController, c))
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
                            !IsInShift(controller, timeSlots[t + 1 + i], t + 1 + i, timeSlots.Count, manualAssignmentsByController, c))
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
                                !IsInShift(controller, timeSlots[breakSlot], breakSlot, timeSlots.Count, manualAssignmentsByController, c))
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
                    if (!IsInShift(controller, timeSlots[t - 1], t - 1, timeSlots.Count, manualAssignmentsByController, c) ||
                        !IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c) ||
                        !IsInShift(controller, timeSlots[t + 1], t + 1, timeSlots.Count, manualAssignmentsByController, c))
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
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c) ||
                        !IsInShift(controller, timeSlots[t + 1], t + 1, timeSlots.Count, manualAssignmentsByController, c) ||
                        !IsInShift(controller, timeSlots[t + 2], t + 2, timeSlots.Count, manualAssignmentsByController, c))
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
            Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, Dictionary<int, Dictionary<int, string>> manualAssignmentsByController,
            bool useManualAssignments)
        {
            _logger.LogInformation("Adding SS/SUP mutual exclusion constraint...");

            var ssControllers = new List<int>();
            var supControllers = new List<int>();

            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];
                if (controller.IsShiftLeader)
                {
                    ssControllers.Add(c);
                    _logger.LogInformation($"Identified SS controller: {controllers[c]}");
                }
                else if (controller.IsSupervisor)
                {
                    supControllers.Add(c);
                    _logger.LogInformation($"Identified SUP controller: {controllers[c]}");
                }
            }

            _logger.LogInformation($"Found {ssControllers.Count} SS and {supControllers.Count} SUP controllers");

            // SAMO ograničenje: SS i SUP ne rade istovremeno
            for (int t = 0; t < timeSlots.Count; t++)
            {
                // Proveri da li ima manuelnih dodela za SS/SUP u ovom slotu
                bool hasManualSSAssignment = ssControllers.Any(ssC =>
                    HasManualAssignment(ssC, t, manualAssignmentsByController) &&
                    GetManualAssignment(ssC, t, manualAssignmentsByController) != "break");

                bool hasManualSUPAssignment = supControllers.Any(supC =>
                    HasManualAssignment(supC, t, manualAssignmentsByController) &&
                    GetManualAssignment(supC, t, manualAssignmentsByController) != "break");

                // **Preskoči mutual exclusion constraint ako oba imaju manualne dodele**
                if (useManualAssignments && hasManualSSAssignment && hasManualSUPAssignment)
                {
                    _logger.LogWarning($"Slot {t} ({timeSlots[t]:HH:mm}): Skipping SS/SUP mutual exclusion - both have manual work assignments");
                    continue;  // <-- preskoči ovaj slot
                }

                var ssWorkingVars = new List<IntVar>();
                var supWorkingVars = new List<IntVar>();

                // SS kontrolori
                foreach (int ssC in ssControllers)
                {
                    if (!IsInShift(controllerInfo[controllers[ssC]], timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, ssC))
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
                    if (!IsInShift(controllerInfo[controllers[supC]], timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, supC))
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

                // KLJUČNO: Maksimalno jedan od SS ili SUP može raditi
                if (ssWorkingVars.Count > 0 && supWorkingVars.Count > 0)
                {
                    var allSpecialControllers = new List<IntVar>();
                    allSpecialControllers.AddRange(ssWorkingVars);
                    allSpecialControllers.AddRange(supWorkingVars);

                    model.Add(LinearExpr.Sum(allSpecialControllers) <= 1);

                    _logger.LogDebug($"Slot {t} ({timeSlots[t]:HH:mm}): SS and SUP cannot work together");
                }
                else if (ssWorkingVars.Count > 0)
                {
                    model.Add(LinearExpr.Sum(ssWorkingVars) <= 1);
                }
                else if (supWorkingVars.Count > 0)
                {
                    model.Add(LinearExpr.Sum(supWorkingVars) <= 1);
                }
            }

            _logger.LogInformation("SS/SUP mutual exclusion constraints completed.");
        }



        private void AddGuaranteedWorkForAllControllers(
            CpModel model,
            Dictionary<(int, int, string), IntVar> assignments,
            List<string> controllers,
            List<DateTime> timeSlots,
            Dictionary<int, List<string>> requiredSectors,
            Dictionary<string, ControllerInfo> controllerInfo,
            Dictionary<int, Dictionary<int, string>> manualAssignmentsByController,
            DataTable inicijalniRaspored,
            bool useManualAssignments)
        {
            _logger.LogInformation("Adding guaranteed work constraints for all selected controllers...");

            // Identifikuj SS i SUP kontrolore
            var ssControllers = new List<int>();
            var supControllers = new List<int>();
            var regularControllers = new List<int>();

            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];
                if (controller.IsShiftLeader)
                {
                    ssControllers.Add(c);
                    _logger.LogInformation($"✓ SS controller: {controllers[c]}");
                }
                else if (controller.IsSupervisor)
                {
                    supControllers.Add(c);
                    _logger.LogInformation($"✓ SUP controller: {controllers[c]}");
                }
                else
                {
                    regularControllers.Add(c);
                    if (controller.IsFMP)
                    {
                        _logger.LogInformation($"✓ FMP controller: {controllers[c]} (treated as regular)");
                    }
                }
            }

            _logger.LogInformation($"Controllers: {regularControllers.Count} regular, {ssControllers.Count} SS, {supControllers.Count} SUP");

            // ============================================
            // PRAVILO 1: SVI kontrolori MORAJU raditi bar minimum slotova
            // IZMENA: Više ne preskačemo SS/SUP čak i ako imaju manuelne dodele
            // ============================================
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];
                var workSlots = new List<IntVar>();
                int totalAvailableSlots = 0;
                int manualWorkSlots = 0;
                int manualNonOperationalSlots = 0; // Brojač svih neoperativnih dodela (FMP, SS, SUP, break)

                for (int t = 0; t < timeSlots.Count; t++)
                {
                    // *** PRVO proveri Flag=S koristeći IsFlagS metodu ***
                    if (IsFlagS(controllers[c], timeSlots[t], inicijalniRaspored))
                    {
                        continue;  // Slot sa Flag=S se NE računa kao dostupan
                    }

                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c))
                        continue;

                    // *** Slot je dostupan - povećaj brojač ***
                    totalAvailableSlots++;

                    if (useManualAssignments && HasManualAssignment(c, t, manualAssignmentsByController))
                    {
                        string manualAssignment = GetManualAssignment(c, t, manualAssignmentsByController)!;

                        if (NON_OPERATIONAL_SECTORS.Contains(manualAssignment))
                        {
                            // Slot je ručno dodeljen neoperativnom sektoru (FMP, SS, SUP, break, itd.)
                            // Ovaj slot se NE računa u workSlots jer je fiksan
                            manualNonOperationalSlots++;
                            continue;
                        }
                        else
                        {
                            // Slot je ručno dodeljen operativnom sektoru
                            manualWorkSlots++;
                            continue;
                        }
                    }

                    // *** SAMO potpuno slobodni slotovi (bez manuelne dodele) se dodaju u workSlots ***
                    var isWorking = model.NewBoolVar($"is_working_{c}_{t}");
                    var sectorVars = new List<IntVar>();

                    foreach (var sector in requiredSectors[t])
                    {
                        sectorVars.Add(assignments[(c, t, sector)]);
                    }

                    if (sectorVars.Count > 0)
                    {
                        model.Add(LinearExpr.Sum(sectorVars) >= 1).OnlyEnforceIf(isWorking);
                        model.Add(LinearExpr.Sum(sectorVars) == 0).OnlyEnforceIf(isWorking.Not());
                        workSlots.Add(isWorking);
                    }
                }

                if (totalAvailableSlots > 0)
                {
                    // Različiti minimum za SS/SUP vs obične kontrolore
                    int minWork;

                    if (controller.IsShiftLeader || controller.IsSupervisor)
                    {
                        // *** IZMENA: SS i SUP sa manuelnim dodelama ne moraju ispunjavati minWork constraint ***
                        // Ako imaju manuelne neoperativne dodele (SS, SUP sektore), preskoči postavljanje minWork
                        if (manualNonOperationalSlots > 0)
                        {
                            string controllerType = controller.IsShiftLeader ? "SS" : "SUP";
                            _logger.LogInformation($"{controllerType} controller {controllers[c]}: Has {manualNonOperationalSlots} manual non-operational slots. " +
                                                  $"Skipping minWork constraint for manual assignments.");
                            continue; // Preskoči postavljanje constraint-a
                        }

                        // SS i SUP: bar 25% dostupnih slotova (manje od običnih)
                        minWork = Math.Max(1, totalAvailableSlots / 4);
                        string controllerType2 = controller.IsShiftLeader ? "SS" : "SUP";
                        _logger.LogInformation($"{controllerType2} controller {controllers[c]}: min work = {minWork} slots (25% of {totalAvailableSlots})");
                    }
                    else
                    {
                        // Obični kontrolori: bar 50% ako ima ≤4 slota, inače 25%
                        // FMP i regularni kontrolori
                        minWork = Math.Max(1, totalAvailableSlots / 4);
                        if (totalAvailableSlots <= 4)
                        {
                            minWork = Math.Max(1, totalAvailableSlots / 2);
                        }

                        if (controller.IsFMP)
                        {
                            _logger.LogDebug($"FMP controller {controllers[c]}: min work = {minWork} slots (same as regular)");
                        }
                        else
                        {
                            _logger.LogDebug($"Regular controller {controllers[c]}: min work = {minWork} slots");
                        }
                    }

                    // *** Proveri da li je već ispunjen minimum ***
                    if (manualWorkSlots >= minWork)
                    {
                        _logger.LogDebug($"Controller {controllers[c]}: Already has {manualWorkSlots} manual work slots (>= {minWork} required)");
                        continue;
                    }

                    int remainingRequired = minWork - manualWorkSlots;
                    int freeSlots = workSlots.Count;

                    // *** KLJUČNA IZMENA: Samo dodaj constraint ako ima SLOBODNIH slotova ***
                    if (freeSlots > 0)
                    {
                        // Ima slobodnih slotova - može se dodati constraint
                        model.Add(LinearExpr.Sum(workSlots) >= remainingRequired);

                        if (controller.IsFMP)
                        {
                            _logger.LogInformation($"✓ FMP Controller {controllers[c]}: Must work >= {remainingRequired} more slots " +
                                                  $"(has {manualWorkSlots} manual operational, {freeSlots} free slots, " +
                                                  $"{manualNonOperationalSlots} locked non-operational, " +
                                                  $"needs {minWork} total out of {totalAvailableSlots} available)");
                        }
                        else
                        {
                            _logger.LogInformation($"✓ Controller {controllers[c]}: Must work >= {remainingRequired} more slots " +
                                                  $"(has {manualWorkSlots} manual, {freeSlots} free, needs {minWork} total out of {totalAvailableSlots} available)");
                        }
                    }
                    else
                    {
                        // *** Nema slobodnih slotova - ne može se dodati constraint ***
                        if (manualWorkSlots < minWork)
                        {
                            // Nema dovoljno operativnog rada i nema slobodnih slotova
                            if (controller.IsFMP)
                            {
                                _logger.LogInformation($"ℹ️ FMP Controller {controllers[c]}: Has {manualWorkSlots} manual operational work slots, " +
                                                      $"needs {minWork} minimum, but NO free slots available " +
                                                      $"({manualNonOperationalSlots} slots locked as FMP non-operational). " +
                                                      $"Skipping work constraint - optimization will respect locked slots.");
                            }
                            else
                            {
                                _logger.LogWarning($"⚠️ Controller {controllers[c]}: Has only {manualWorkSlots} manual work slots, " +
                                                  $"needs {minWork}, but no free slots for automatic assignment " +
                                                  $"({manualNonOperationalSlots} non-operational slots).");
                            }
                        }
                        else
                        {
                            // Ima dovoljno operativnog rada kroz manuelne dodele
                            _logger.LogDebug($"Controller {controllers[c]}: All requirements satisfied via manual assignments " +
                                           $"({manualWorkSlots} manual operational >= {minWork} required)");
                        }
                    }
                }
            }

            // ============================================
            // PRAVILO 2: SS i SUP rade MAKSIMALNO 70% od maksimuma regularnih kontrolora
            // Ovo ih ne sprečava da rade, samo ograničava da ne rade VIŠE od običnih
            // ============================================
            var regularWorkloadVars = new List<IntVar>();
            var ssWorkloadVars = new List<IntVar>();
            var supWorkloadVars = new List<IntVar>();

            // Kreiraj varijable za ukupno radno vreme REGULARNIH kontrolora
            foreach (int c in regularControllers)
            {
                var controller = controllerInfo[controllers[c]];
                var workSlots = new List<IntVar>();
                int manualWorkSlots = 0;

                for (int t = 0; t < timeSlots.Count; t++)
                {
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c))
                        continue;

                    if (useManualAssignments && HasManualAssignment(c, t, manualAssignmentsByController))
                    {
                        string manualAssignment = GetManualAssignment(c, t, manualAssignmentsByController)!;

                        if (NON_OPERATIONAL_SECTORS.Contains(manualAssignment))
                        {
                            continue;
                        }
                        else
                        {
                            manualWorkSlots++;
                            continue;
                        }
                    }

                    foreach (var sector in requiredSectors[t])
                    {
                        workSlots.Add(assignments[(c, t, sector)]);
                    }
                }

                if (workSlots.Count > 0 || manualWorkSlots > 0)
                {
                    var totalWork = model.NewIntVar(0, workSlots.Count + manualWorkSlots, $"regular_work_{c}");

                    if (workSlots.Count > 0)
                    {
                        model.Add(totalWork == LinearExpr.Sum(workSlots) + manualWorkSlots);
                    }
                    else
                    {
                        model.Add(totalWork == manualWorkSlots);
                    }

                    regularWorkloadVars.Add(totalWork);
                }
            }

            // Kreiraj varijable za ukupno radno vreme SS kontrolora
            foreach (int c in ssControllers)
            {
                var controller = controllerInfo[controllers[c]];
                var workSlots = new List<IntVar>();
                int manualWorkSlots = 0;

                for (int t = 0; t < timeSlots.Count; t++)
                {
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c))
                        continue;

                    if (useManualAssignments && HasManualAssignment(c, t, manualAssignmentsByController))
                    {
                        string manualAssignment = GetManualAssignment(c, t, manualAssignmentsByController)!;

                        if (NON_OPERATIONAL_SECTORS.Contains(manualAssignment))
                        {
                            continue;
                        }
                        else
                        {
                            manualWorkSlots++;
                            continue;
                        }
                    }

                    foreach (var sector in requiredSectors[t])
                    {
                        workSlots.Add(assignments[(c, t, sector)]);
                    }
                }

                if (workSlots.Count > 0 || manualWorkSlots > 0)
                {
                    var totalWork = model.NewIntVar(0, workSlots.Count + manualWorkSlots, $"ss_work_{c}");

                    if (workSlots.Count > 0)
                    {
                        model.Add(totalWork == LinearExpr.Sum(workSlots) + manualWorkSlots);
                    }
                    else
                    {
                        model.Add(totalWork == manualWorkSlots);
                    }

                    ssWorkloadVars.Add(totalWork);
                }
            }

            // Kreiraj varijable za ukupno radno vreme SUP kontrolora
            foreach (int c in supControllers)
            {
                var controller = controllerInfo[controllers[c]];
                var workSlots = new List<IntVar>();
                int manualWorkSlots = 0;

                for (int t = 0; t < timeSlots.Count; t++)
                {
                    if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c))
                        continue;

                    if (useManualAssignments && HasManualAssignment(c, t, manualAssignmentsByController))
                    {
                        string manualAssignment = GetManualAssignment(c, t, manualAssignmentsByController)!;

                        if (NON_OPERATIONAL_SECTORS.Contains(manualAssignment))
                        {
                            continue;
                        }
                        else
                        {
                            manualWorkSlots++;
                            continue;
                        }
                    }

                    foreach (var sector in requiredSectors[t])
                    {
                        workSlots.Add(assignments[(c, t, sector)]);
                    }
                }

                if (workSlots.Count > 0 || manualWorkSlots > 0)
                {
                    var totalWork = model.NewIntVar(0, workSlots.Count + manualWorkSlots, $"sup_work_{c}");

                    if (workSlots.Count > 0)
                    {
                        model.Add(totalWork == LinearExpr.Sum(workSlots) + manualWorkSlots);
                    }
                    else
                    {
                        model.Add(totalWork == manualWorkSlots);
                    }

                    supWorkloadVars.Add(totalWork);
                }
            }

            // SS i SUP rade maksimalno 70% prosečnog rada regularnih kontrolora
            if (regularWorkloadVars.Count > 0 && (ssWorkloadVars.Count > 0 || supWorkloadVars.Count > 0))
            {
                // Nađi maksimum regularnih kontrolora
                var maxRegularWork = model.NewIntVar(0, timeSlots.Count, "max_regular_work");

                foreach (var regWork in regularWorkloadVars)
                {
                    model.Add(maxRegularWork >= regWork);
                }

                // SS radi maksimalno 70% maksimuma regularnih
                foreach (var ssWork in ssWorkloadVars)
                {
                    var ssWork10 = model.NewIntVar(0, timeSlots.Count * 10, $"ss_work_times_10");
                    var maxWork7 = model.NewIntVar(0, timeSlots.Count * 7, $"max_work_times_7");

                    model.Add(ssWork10 == ssWork * 10);
                    model.Add(maxWork7 == maxRegularWork * 7);
                    model.Add(ssWork10 <= maxWork7);

                    _logger.LogInformation("✓ SS works max 70% of regular controllers");
                }

                // SUP radi maksimalno 70% maksimuma regularnih
                foreach (var supWork in supWorkloadVars)
                {
                    var supWork10 = model.NewIntVar(0, timeSlots.Count * 10, $"sup_work_times_10");
                    var maxWork7 = model.NewIntVar(0, timeSlots.Count * 7, $"max_work_times_7_sup");

                    model.Add(supWork10 == supWork * 10);
                    model.Add(maxWork7 == maxRegularWork * 7);
                    model.Add(supWork10 <= maxWork7);

                    _logger.LogInformation("✓ SUP works max 70% of regular controllers");
                }
            }

            _logger.LogInformation("Guaranteed work constraints added successfully.");
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

                // Debug: Proveri da li SS/SUP imaju manualne dodele
                _logger.LogInformation("=== SS/SUP Manual Assignments Check ===");
                foreach (var (controllerCode, timeSlotIndex, sector) in manualAssignments)
                {
                    int controllerIndex = controllers.IndexOf(controllerCode);
                    if (controllerIndex < 0) continue;

                    var controller = controllerInfo[controllerCode];
                    if (controller.IsShiftLeader || controller.IsSupervisor)
                    {
                        _logger.LogWarning($"🔍 {(controller.IsShiftLeader ? "SS" : "SUP")} {controllerCode} " +
                                          $"has manual assignment at slot {timeSlotIndex}: {sector}");
                    }
                }
                _logger.LogInformation("=== End SS/SUP Check ===");

                // Korak 2: Primeni manuelne dodele kao čvrsta ograničenja
                // Korak 2: Primeni manuelne dodele kao čvrsta ograničenja
                foreach (var (controllerCode, timeSlotIndex, sector) in manualAssignments)
                {
                    int controllerIndex = controllers.IndexOf(controllerCode);
                    if (controllerIndex < 0) continue;

                    var controller = controllerInfo[controllerCode];
                    bool isSpecial = controller.IsShiftLeader || controller.IsSupervisor; // UKLONI IsFMP odavde
                    bool isNonOperationalSector = NON_OPERATIONAL_SECTORS.Contains(sector);

                    string controllerType = controller.IsShiftLeader ? "SS" :
                                           controller.IsSupervisor ? "SUP" :
                                           controller.IsFMP ? "FMP" : "REG";

                    _logger.LogInformation($"✅ Processing manual assignment: {controllerType} " +
                                          $"Controller {controllerCode} at slot {timeSlotIndex} on sector {sector}");

                    if (sector == "break")
                    {
                        // Break je uvek hard constraint
                        model.Add(assignments[(controllerIndex, timeSlotIndex, "break")] == 1);

                        // Ne sme biti dodeljen nijednom sektoru
                        foreach (var otherSector in requiredSectors[timeSlotIndex])
                        {
                            model.Add(assignments[(controllerIndex, timeSlotIndex, otherSector)] == 0);
                        }

                        // Dodaj i ne-operativne sektore
                        foreach (var nonOpSector in NON_OPERATIONAL_SECTORS)
                        {
                            if (nonOpSector != "break")
                            {
                                model.Add(assignments[(controllerIndex, timeSlotIndex, nonOpSector)] == 0);
                            }
                        }

                        _logger.LogInformation($"🔒 LOCKED: Controller {controllerCode} on BREAK at slot {timeSlotIndex}");
                    }
                    else if (isNonOperationalSector)
                    {
                        // *** NEOPERATIVNI SEKTORI (SS, SUP, FMP) - hard constraint ***
                        model.Add(assignments[(controllerIndex, timeSlotIndex, sector)] == 1);
                        model.Add(assignments[(controllerIndex, timeSlotIndex, "break")] == 0);

                        // Ne sme biti dodeljen radnim sektorima
                        foreach (var otherSector in requiredSectors[timeSlotIndex])
                        {
                            model.Add(assignments[(controllerIndex, timeSlotIndex, otherSector)] == 0);
                        }

                        // Ne sme biti dodeljen drugim neoperativnim sektorima
                        foreach (var nonOpSector in NON_OPERATIONAL_SECTORS)
                        {
                            if (nonOpSector != sector && nonOpSector != "break")
                            {
                                model.Add(assignments[(controllerIndex, timeSlotIndex, nonOpSector)] == 0);
                            }
                        }

                        _logger.LogInformation($"🔒 LOCKED: {controllerType} {controllerCode} " +
                                              $"MUST be on NON-OP sector {sector} at slot {timeSlotIndex}");
                    }
                    else
                    {
                        // RADNI SEKTOR - uvek hard constraint (za sve kontrolore)
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

                        // Ne sme biti dodeljen ne-operativnim sektorima
                        foreach (var nonOpSector in NON_OPERATIONAL_SECTORS)
                        {
                            if (nonOpSector != "break")
                            {
                                model.Add(assignments[(controllerIndex, timeSlotIndex, nonOpSector)] == 0);
                            }
                        }

                        _logger.LogInformation($"🔒 LOCKED: {controllerType} {controllerCode} " +
                                              $"MUST be on {sector} at slot {timeSlotIndex}");
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

            //2.OSNOVNO: Svaki kontrolor je dodeljen tačno jednom sektoru ili pauzi
            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 0; t < timeSlots.Count; t++)
                {
                    var controller = controllerInfo[controllers[c]];
                    bool inShift = IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c);

                    if (!inShift)
                    {
                        model.Add(assignments[(c, t, "break")] == 1);
                        foreach (var sector in requiredSectors[t])
                        {
                            model.Add(assignments[(c, t, sector)] == 0);
                        }
                        // Dodaj i neoperativne sektore
                        foreach (var nonOpSector in NON_OPERATIONAL_SECTORS)
                        {
                            if (nonOpSector != "break")
                            {
                                model.Add(assignments[(c, t, nonOpSector)] == 0);
                            }
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
                            // Dodaj i neoperativne sektore
                            foreach (var nonOpSector in NON_OPERATIONAL_SECTORS)
                            {
                                if (nonOpSector != "break")
                                {
                                    model.Add(assignments[(c, t, nonOpSector)] == 0);
                                }
                            }
                        }
                        else
                        {
                            // *** IZMENA: Dodaj SVE moguće sektore (regular + neoperativni) ***
                            var taskVars = new List<IntVar> { assignments[(c, t, "break")] };

                            // Dodaj regularne sektore
                            taskVars.AddRange(requiredSectors[t].Select(s => assignments[(c, t, s)]));

                            // Dodaj neoperativne sektore (SS, SUP, FMP)
                            foreach (var nonOpSector in NON_OPERATIONAL_SECTORS)
                            {
                                if (nonOpSector != "break")
                                {
                                    taskVars.Add(assignments[(c, t, nonOpSector)]);
                                }
                            }

                            // Kontrolor može biti dodeljen TAČNO jednom: break ILI regular sektor ILI neoperativni sektor
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
            AddGuaranteedWorkForAllControllers(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController, inicijalniRaspored, useManualAssignments);
            //AddRotationConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController);
            AddSupervisorShiftLeaderConstraints(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, manualAssignmentsByController, useManualAssignments);

        }

      

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
                            else if (controller.IsFMP)
                                controllerType = "FMP";
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


        private bool IsInShift(ControllerInfo controller, DateTime slotTime, int slotIndex, int totalSlots,
            Dictionary<int, Dictionary<int, string>>? manualAssignmentsByController = null, int? controllerIndex = null)
        {
            // Osnovni uslov - vreme slota je između početka i kraja smene
            bool inShift = slotTime >= controller.ShiftStart && slotTime < controller.ShiftEnd;

            // Dodatni uslov za smenu tipa M - ne radi u poslednja dva slota
            if (inShift && controller.ShiftType == "M" && slotIndex >= totalSlots - 2)
            {
                // *** KLJUČNA LOGIKA ***
                // Proveri da li postoji manualna dodela
                bool hasManualAssignment = false;

                if (manualAssignmentsByController != null &&
                    controllerIndex.HasValue &&
                    manualAssignmentsByController.ContainsKey(controllerIndex.Value) &&
                    manualAssignmentsByController[controllerIndex.Value].ContainsKey(slotIndex))
                {
                    string manualSector = manualAssignmentsByController[controllerIndex.Value][slotIndex];
                    // Proveri da li je manuelna dodela sektor (nije pauza)
                    if (!string.IsNullOrEmpty(manualSector) && manualSector != "break")
                    {
                        hasManualAssignment = true;
                    }
                }

                if (hasManualAssignment)
                {
                    // IMA manuelnu dodelu za rad - DOZVOLI rad u poslednjem satu
                    _logger.LogInformation(
                        $"Controller index {controllerIndex.Value} has manual WORK assignment in last hour of M shift at slot {slotIndex} - ALLOWING work");
                    return true;
                }
                else
                {
                    // NEMA manuelnu dodelu ili je dodela pauza - BLOKIRAJ rad u poslednjem satu
                    _logger.LogDebug(
                        $"Controller index {controllerIndex?.ToString() ?? "?"} - M shift restriction applied for slot {slotIndex} (no manual work assignment)");
                    inShift = false;
                }
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
            Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, DataTable inicijalniRaspored, 
            Dictionary<int, Dictionary<int, string>> manualAssignmentsByController, bool useManualAssignments)
        {
            // *** SAMO AKO SE KORISTE MANUELNE DODELE, popuni manualAssignmentSet ***
            var manualAssignmentSet = new HashSet<(int controllerIndex, int timeSlotIndex, string sector)>();

            if (useManualAssignments)
            {
                // Identifikuj manuelne dodele
                var manualAssignments = IdentifyManualAssignments(inicijalniRaspored, controllers, timeSlots);

                foreach (var (controllerCode, timeSlotIndex, sector) in manualAssignments)
                {
                    int controllerIndex = controllers.IndexOf(controllerCode);
                    if (controllerIndex >= 0)
                    {
                        manualAssignmentSet.Add((controllerIndex, timeSlotIndex, sector));
                    }
                }

                _logger.LogInformation($"DefineObjective: Using manual assignments - {manualAssignmentSet.Count} assignments will be excluded from penalties");
            }
            else
            {
                _logger.LogInformation("DefineObjective: NOT using manual assignments - manualAssignmentSet is EMPTY, all assignments will receive penalties");
            }

            // Inicijalizacija liste termina za funkciju cilja
            var objectiveTerms = new List<LinearExpr>();


            // ============================================================================
            // 1. PENALI ZA NEPOKRIVENE SEKTORE (NAJVIŠI PRIORITET)
            // ============================================================================
            for (int t = 0; t < timeSlots.Count; t++)
            {
                foreach (var sector in requiredSectors[t])
                {
                    var sectorVars = new List<IntVar>();
                    for (int c = 0; c < controllers.Count; c++)
                    {
                        if (assignments.TryGetValue((c, t, sector), out var assignment))
                        {
                            var controller = controllerInfo[controllers[c]];
                            bool inShift = IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c);
                            bool isFlagS = IsFlagS(controllers[c], timeSlots[t], inicijalniRaspored);

                            if (inShift && !isFlagS)
                            {
                                sectorVars.Add(assignment);
                            }
                        }
                    }

                    var sectorNotCovered = model.NewBoolVar($"sector_not_covered_{t}_{sector}");
                    if (sectorVars.Count > 0)
                    {
                        model.Add(LinearExpr.Sum(sectorVars) == 0).OnlyEnforceIf(sectorNotCovered);
                        model.Add(LinearExpr.Sum(sectorVars) > 0).OnlyEnforceIf(sectorNotCovered.Not());
                        objectiveTerms.Add(UNCOVERED_SECTOR_PENALTY * sectorNotCovered);
                    }
                }
            }

            // ============================================================================
            // 2. NOVI - MINIMALNI PENALI ZA SS I SUP (umesto starih visokih penala)
            // ============================================================================
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                if (controller.IsShiftLeader || controller.IsSupervisor)
                {
                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        foreach (var sector in requiredSectors[t])
                        {
                            // Preskočimo manuelne dodele SAMO ako se koriste
                            if (useManualAssignments && manualAssignmentSet.Contains((c, t, sector)))
                            {
                                continue;
                            }

                            // DRASTIČNO smanjeni penali - samo blaga preferencija za regularne
                            // SS: 50 (bilo 2000 + 200 = 2200)
                            // SUP: 30 (bilo 500 + 100 = 600)
                            int penalty = controller.IsShiftLeader ? 50 : 30;
                            objectiveTerms.Add(penalty * assignments[(c, t, sector)]);
                        }
                    }
                }
            }

            // ============================================================================
            // 3. PENALI ZA RAD U POSLEDNJEM SATU SMENE
            // ============================================================================
            const int LAST_HOUR_PENALTY = 500;
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                // Za poslednja 2 slota (jedan sat)
                for (int t = Math.Max(0, timeSlots.Count - 2); t < timeSlots.Count; t++)
                {
                    foreach (var sector in requiredSectors[t])
                    {
                        // *** NE preskačemo manuelne dodele ovde - penal se primenjuje uvek ***
                        // *** Osim ako je useManualAssignments=true, onda ih preskačemo ***
                        if (useManualAssignments && manualAssignmentSet.Contains((c, t, sector)))
                        {
                            continue;
                        }

                        objectiveTerms.Add(LAST_HOUR_PENALTY * assignments[(c, t, sector)]);
                    }
                }
            }

            // ============================================================================
            // 4. PENALI ZA KRATKE PAUZE (manje od 1 sat)
            // ============================================================================
            foreach (var entry in shortBreakVars)
            {
                objectiveTerms.Add(300 * entry.Value);
            }

            // ============================================================================
            // 5. PENALI ZA NEPOŠTOVANJE ROTACIJE E/P POZICIJA
            // ============================================================================
            foreach (var entry in shouldWorkOnEVars)
            {
                objectiveTerms.Add(200 * entry.Value);
            }

            // Bonus za pravilno rotiranje pozicija
            for (int c = 0; c < controllers.Count; c++)
            {
                for (int t = 2; t < timeSlots.Count; t++)
                {
                    foreach (var sector in requiredSectors[t].Where(s => s.EndsWith("E") || s.EndsWith("P")))
                    {
                        string baseSector = sector[..^1];
                        string currentPosition = sector[^1..];
                        string alternativePosition = (currentPosition == "E") ? "P" : "E";
                        string alternativeSector = baseSector + alternativePosition;

                        if (requiredSectors[t].Contains(alternativeSector))
                        {
                            // Bonus za rotaciju pozicija
                            objectiveTerms.Add(-100 * assignments[(c, t, alternativeSector)]);
                        }
                    }
                }
            }

            // ============================================================================
            // 6. KONTINUITET SEKTORA - bonus
            // ============================================================================
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                for (int t = 1; t < timeSlots.Count; t++)
                {
                    bool inShiftPrev = IsInShift(controller, timeSlots[t - 1], t - 1, timeSlots.Count, manualAssignmentsByController, c);
                    bool inShiftCurr = IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c);

                    if (inShiftPrev && inShiftCurr)
                    {
                        foreach (var sector in requiredSectors[t - 1].Intersect(requiredSectors[t]))
                        {
                            var continuityBonus = model.NewBoolVar($"continuity_bonus_{c}_{t}_{sector}");
                            model.Add(assignments[(c, t - 1, sector)] == 1).OnlyEnforceIf(continuityBonus);
                            model.Add(assignments[(c, t, sector)] == 1).OnlyEnforceIf(continuityBonus);

                            objectiveTerms.Add(-200 * continuityBonus);
                        }
                    }
                }
            }

            // ============================================================================
            // 7. VISOK PENAL ZA VIŠAK KONTROLORA NA ISTOM SEKTORU
            // ============================================================================
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

                    var controllersOnSector = model.NewIntVar(0, controllers.Count, $"controllers_on_{t}_{sector}");
                    model.Add(controllersOnSector == LinearExpr.Sum(sectorVars));

                    var excessControllers = model.NewBoolVar($"excess_{t}_{sector}");
                    model.Add(controllersOnSector > 1).OnlyEnforceIf(excessControllers);
                    model.Add(controllersOnSector <= 1).OnlyEnforceIf(excessControllers.Not());

                    objectiveTerms.Add(EXCESS_CONTROLLERS_PENALTY * excessControllers);
                }
            }

            // ============================================================================
            // 8. POSEBNA LOGIKA ZA NOĆNU SMENU (ako postoji)
            // ============================================================================
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];

                if (controller.ShiftType == "N" && !controller.IsShiftLeader && !controller.IsSupervisor)
                {
                    foreach (int t in nightShiftSlots)
                    {
                        if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count, manualAssignmentsByController, c))
                            continue;

                        // Bonus za pauze običnih kontrolora u noćnoj smeni
                        objectiveTerms.Add(-1000 * assignments[(c, t, "break")]);

                        // Penal za rad običnih kontrolora u noćnoj smeni
                        foreach (var sector in requiredSectors[t])
                        {
                            objectiveTerms.Add(800 * assignments[(c, t, sector)]);
                        }
                    }
                }
            }



            // Bonusi/penali za duge pauze i rad u noćnoj smeni
            foreach (var entry in nightShiftLongBreaks)
            {
                objectiveTerms.Add(-2000 * entry.Value);
            }

            foreach (var entry in nightShiftLongWorkPeriods)
            {
                objectiveTerms.Add(3000 * entry.Value);
            }

            if (nightShiftWorkloadDifference != null)
            {
                objectiveTerms.Add(1000 * nightShiftWorkloadDifference);
            }

            foreach (var entry in nightShiftSectorTypeCoverage)
            {
                objectiveTerms.Add(-500 * entry.Value);
            }

            // ============================================================================
            // 9. FMP KONTROLORI - posebni penali/bonusi
            // ============================================================================
            for (int c = 0; c < controllers.Count; c++)
            {
                var controller = controllerInfo[controllers[c]];
                bool isFMP = controller.ORM == "FMP";
                bool hasLicense = _controllersWithLicense?.Contains(controllers[c]) ?? false;

                if (isFMP && hasLicense)
                {
                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        foreach (var sector in requiredSectors[t].Where(s => s.Contains("FMP")))
                        {
                            objectiveTerms.Add(-500 * assignments[(c, t, sector)]);
                        }

                        foreach (var sector in requiredSectors[t].Where(s => !s.Contains("FMP") && !s.Equals("break")))
                        {
                            objectiveTerms.Add(200 * assignments[(c, t, sector)]);
                        }
                    }
                }
                else if (isFMP && !hasLicense)
                {
                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        foreach (var sector in requiredSectors[t].Where(s => !s.Equals("break")))
                        {
                            objectiveTerms.Add(5000 * assignments[(c, t, sector)]);
                        }
                    }
                }
                else
                {
                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        foreach (var sector in requiredSectors[t].Where(s => s.Contains("FMP")))
                        {
                            objectiveTerms.Add(2000 * assignments[(c, t, sector)]);
                        }
                    }
                }
            }

            // ============================================================================
            // 10. DODATNI PENALI/BONUSI
            // ============================================================================

            // Bonus za preferirane radne blokove
            foreach (var (key, preferredBlockVar) in preferredWorkBlocks)
            {
                objectiveTerms.Add(preferredBlockVar * -20);
            }

            // Penal za fragmentovan rad
            foreach (var (key, fragmentVar) in fragmentedWorkPenalties)
            {
                objectiveTerms.Add(fragmentVar * 30);
            }

            // ============================================================================
            // KRAJ - Vrati sumu svih termina
            // ============================================================================
            return LinearExpr.Sum(objectiveTerms);
        }

        private OptimizationResponse CreateOptimizationResponse(CpSolver solver, Dictionary<(int, int, string), IntVar> assignments, CpSolverStatus status, List<string> controllers,
    List<DateTime> timeSlots, Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, DataTable inicijalniRaspored, DateTime datum)
        {
            var optimizedResults = new List<OptimizationResultDTO>();

            if (status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible)
            {
                // IDENTIFIKUJ MANUELNE DODELE DA BIH ZNAO ŠTA JE MANUELNO DODELJENO
                var manualAssignments = IdentifyManualAssignments(inicijalniRaspored, controllers, timeSlots);
                var manualAssignmentsByController = new Dictionary<int, Dictionary<int, string>>();

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

                // konvertuj resenje u listu rezultata optimizacije
                for (int c = 0; c < controllers.Count; c++)
                {
                    var controllerCode = controllers[c];
                    var controllerData = controllerInfo[controllerCode];

                    // *** UZMI REDOSLED I PAR IZ INICIJALNOG RASPOREDA ***
                    var controllerRow = inicijalniRaspored.AsEnumerable()
                        .FirstOrDefault(row => row.Field<string>("sifra") == controllerCode);

                    int redosled = 0;
                    string? par = null;

                    if (controllerRow != null)
                    {
                        redosled = controllerRow["Redosled"] != DBNull.Value ? Convert.ToInt32(controllerRow["Redosled"]) : 0;
                        par = controllerRow.Field<string>("Par");
                    }

                    for (int t = 0; t < timeSlots.Count; t++)
                    {
                        DateTime timeSlot = timeSlots[t];
                        bool inShift = timeSlot >= controllerData.ShiftStart && timeSlot < controllerData.ShiftEnd;

                        // *** KLJUČNA IZMENA: Proveri da li je M smena u poslednjem satu ***
                        bool isLastHourM = controllerData.ShiftType == "M" && t >= timeSlots.Count - 2;

                        // Proveri da li ima manuelnu dodelu za ovaj slot
                        bool hasManualAssignment = manualAssignmentsByController.ContainsKey(c) &&
                                                   manualAssignmentsByController[c].ContainsKey(t);

                        // *** NOVA LOGIKA: Ako je M smena u poslednjem satu ***
                        // *** NOVA LOGIKA: Ako je M smena u poslednjem satu ***
                        if (inShift && isLastHourM)
                        {
                            if (hasManualAssignment)
                            {
                                string manualSector = manualAssignmentsByController[c][t];

                                // PROVERI DA LI JE ZAISTA SEKTOR (nije pauza)
                                if (!string.IsNullOrEmpty(manualSector) && manualSector != "break")
                                {
                                    // IMA MANUELNU DODELU ZA RAD - dodaj kao normalan slot sa sektorom
                                    optimizedResults.Add(new OptimizationResultDTO
                                    {
                                        Sifra = controllerCode,
                                        PrezimeIme = controllerData.Name,
                                        Smena = controllerData.ShiftType,
                                        Datum = datum,
                                        DatumOd = timeSlot,
                                        DatumDo = timeSlot.AddMinutes(_slotDurationMinutes),
                                        Sektor = manualSector,
                                        ORM = controllerData.ORM,
                                        Flag = this.IsFlagS(controllerCode, timeSlot, inicijalniRaspored) ? "S" : null,
                                        Redosled = redosled,
                                        Par = par,
                                        VremeStart = controllerData.VremeStart
                                    });

                                    _logger.LogInformation($"M shift last hour: Controller {controllerCode} has MANUAL WORK assignment: {manualSector}");
                                }
                                else
                                {
                                    // Manuelna dodela je PAUZA - dodaj prazan slot
                                    optimizedResults.Add(new OptimizationResultDTO
                                    {
                                        Sifra = controllerCode,
                                        PrezimeIme = controllerData.Name,
                                        Smena = controllerData.ShiftType,
                                        Datum = datum,
                                        DatumOd = timeSlot,
                                        DatumDo = timeSlot.AddMinutes(_slotDurationMinutes),
                                        Sektor = "",
                                        ORM = controllerData.ORM,
                                        Flag = "M_LAST_HOUR",
                                        Redosled = redosled,
                                        Par = par,
                                        VremeStart = controllerData.VremeStart
                                    });
                                }
                            }
                            else
                            {
                                // NEMA MANUELNU DODELU - dodaj PRAZAN slot
                                optimizedResults.Add(new OptimizationResultDTO
                                {
                                    Sifra = controllerCode,
                                    PrezimeIme = controllerData.Name,
                                    Smena = controllerData.ShiftType,
                                    Datum = datum,
                                    DatumOd = timeSlot,
                                    DatumDo = timeSlot.AddMinutes(_slotDurationMinutes),
                                    Sektor = "",
                                    ORM = controllerData.ORM,
                                    Flag = "M_LAST_HOUR",
                                    Redosled = redosled,
                                    Par = par,
                                    VremeStart = controllerData.VremeStart
                                });

                                _logger.LogDebug($"M shift last hour: Controller {controllerCode} - empty slot added");
                            }

                            continue; // Preskoči normalno procesiranje za ovaj slot
                        }

                        // *** ORIGINALNI KOD ZA SVE OSTALE SLOTOVE ***
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
                                Sektor = assignedSector, // null ili prazan ako je na pauzi (break)
                                ORM = controllerData.ORM,
                                Flag = this.IsFlagS(controllerCode, timeSlot, inicijalniRaspored) ? "S" : null,
                                Redosled = redosled,
                                Par = par,
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
                            string type = controller.IsShiftLeader ? "SS" : controller.IsSupervisor ? "SUP" : controller.IsFMP ? "FMP" : "REG";
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
