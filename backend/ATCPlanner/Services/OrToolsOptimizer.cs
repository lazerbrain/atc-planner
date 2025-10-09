using Microsoft.Extensions.Configuration;
using ATCPlanner.Data;
using ATCPlanner.Models;
using ATCPlanner.Services.Constraints;
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
    
    public class OrToolsOptimizer
    {
        private readonly IEnumerable<IOrToolsConstraint> _constraints;
        private readonly ILogger _logger;
        private readonly DataTableFilter _dataTableFilter;
        private readonly DatabaseHandler _databaseHandler;
        private readonly int _slotDurationMinutes;
        private readonly IConfiguration _configuration;

        private readonly int _block30MinPenalty;
        private readonly int _block1HourPenalty;
        private readonly int _block15HourBonus;
        private readonly int _block2HourBonus;
        private readonly int _uncoveredSectorPenalty;
        private readonly int _lastHourWorkPenalty;
        private readonly int _shortBreakPenalty;
        private readonly int _rotationViolationPenalty;
        private readonly int _continuityBonus;
        private readonly int _excessControllerPenalty;
        private readonly int _nightShiftBreakBonus;
        private readonly int _nightShiftWorkPenalty;

        private DataTable? _konfiguracije;
        private List<string>? _controllersWithLicense;

        // Struktura za čuvanje originalnog rasporeda
        private Dictionary<(int controller, int slot), string> _originalAssignments = new Dictionary<(int controller, int slot), string>();

        public OrToolsOptimizer(ILogger logger, DataTableFilter dataTableFilter, DatabaseHandler databaseHandler, int slotDurationMinutes, IConfiguration configuration, IEnumerable<IOrToolsConstraint> constraints)
        {
            _logger = logger;
            _dataTableFilter = dataTableFilter;
            _databaseHandler = databaseHandler;
            _slotDurationMinutes = slotDurationMinutes;
            _configuration = configuration;
            _constraints = constraints;

            // Učitavanje penala iz konfiguracije
            _block30MinPenalty = _configuration.GetValue<int>("OrToolsPenalties:Block30MinPenalty", 100);
            _block1HourPenalty = _configuration.GetValue<int>("OrToolsPenalties:Block1HourPenalty", 50);
            _block15HourBonus = _configuration.GetValue<int>("OrToolsPenalties:Block15HourBonus", -30);
            _block2HourBonus = _configuration.GetValue<int>("OrToolsPenalties:Block2HourBonus", -30);
            _uncoveredSectorPenalty = _configuration.GetValue<int>("OrToolsPenalties:UncoveredSectorPenalty", 50000000);
            _lastHourWorkPenalty = _configuration.GetValue<int>("OrToolsPenalties:LastHourWorkPenalty", 500);
            _shortBreakPenalty = _configuration.GetValue<int>("OrToolsPenalties:ShortBreakPenalty", 300);
            _rotationViolationPenalty = _configuration.GetValue<int>("OrToolsPenalties:RotationViolationPenalty", 200);
            _continuityBonus = _configuration.GetValue<int>("OrToolsPenalties:ContinuityBonus", -200);
            _excessControllerPenalty = _configuration.GetValue<int>("OrToolsPenalties:ExcessControllerPenalty", 100000);
            _nightShiftBreakBonus = _configuration.GetValue<int>("OrToolsPenalties:NightShiftBreakBonus", -1000);
            _nightShiftWorkPenalty = _configuration.GetValue<int>("OrToolsPenalties:NightShiftWorkPenalty", 800);
        }

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

                // kreiraj i resi model
                var model = new CpModel();

                // kreiraj varijable odlucivanja za model
                var assignments = this.CreateAssignmentVariables(model, controllers, timeSlots, requiredSectors);

                // dodaj ogranicenja u model
                foreach (var constraint in _constraints)
                {
                    constraint.Apply(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, inicijalniRaspored, useManualAssignments);
                }

                // definisanje funkcije cilja
                var objective = this.DefineObjective(model, assignments, controllers, timeSlots, requiredSectors, controllerInfo, inicijalniRaspored, useManualAssignments);
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
            Dictionary<int, List<string>> requiredSectors, Dictionary<string, ControllerInfo> controllerInfo, DataTable inicijalniRaspored, bool useManualAssignments)
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
                            bool inShift = IsInShift(controller, timeSlots[t], t, timeSlots.Count);
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
                        objectiveTerms.Add(_uncoveredSectorPenalty * sectorNotCovered);
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

                        objectiveTerms.Add(_lastHourWorkPenalty * assignments[(c, t, sector)]);
                    }
                }
            }

            // ============================================================================
            // 4. PENALI ZA KRATKE PAUZE (manje od 1 sat)
            // ============================================================================
            foreach (var entry in shortBreakVars)
            {
                objectiveTerms.Add(_shortBreakPenalty * entry.Value);
            }

            // ============================================================================
            // 5. PENALI ZA NEPOŠTOVANJE ROTACIJE E/P POZICIJA
            // ============================================================================
            foreach (var entry in shouldWorkOnEVars)
            {
                objectiveTerms.Add(_rotationViolationPenalty * entry.Value);
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
                    bool inShiftPrev = IsInShift(controller, timeSlots[t - 1], t - 1, timeSlots.Count);
                    bool inShiftCurr = IsInShift(controller, timeSlots[t], t, timeSlots.Count);

                    if (inShiftPrev && inShiftCurr)
                    {
                        foreach (var sector in requiredSectors[t - 1].Intersect(requiredSectors[t]))
                        {
                            var continuityBonus = model.NewBoolVar($"continuity_bonus_{c}_{t}_{sector}");
                            model.Add(assignments[(c, t - 1, sector)] == 1).OnlyEnforceIf(continuityBonus);
                            model.Add(assignments[(c, t, sector)] == 1).OnlyEnforceIf(continuityBonus);

                            objectiveTerms.Add(_continuityBonus * continuityBonus);
                        }
                    }
                }
            }

            // ============================================================================
            // 7. VISOK PENAL ZA VIŠAK KONTROLORA NA ISTOM SEKTORU
            // ============================================================================
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

                    objectiveTerms.Add(_excessControllerPenalty * excessControllers);
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
                        if (!IsInShift(controller, timeSlots[t], t, timeSlots.Count))
                            continue;

                        // Bonus za pauze običnih kontrolora u noćnoj smeni
                        objectiveTerms.Add(_nightShiftBreakBonus * assignments[(c, t, "break")]);

                        // Penal za rad običnih kontrolora u noćnoj smeni
                        foreach (var sector in requiredSectors[t])
                        {
                            objectiveTerms.Add(_nightShiftWorkPenalty * assignments[(c, t, sector)]);
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
