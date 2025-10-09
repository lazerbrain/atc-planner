using System.ComponentModel.DataAnnotations;
﻿using ATCPlanner.Data;
using ATCPlanner.Models;
using ATCPlanner.Services;
using ATCPlanner.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Data;
using static ATCPlanner.Models.RosterResponse;

namespace ATCPlanner.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class RosterController(DatabaseHandler dbHandler, RosterOptimizer rosterOptimizer, OrToolsSessionService orToolsSessionService, ILogger<RosterController> logger, IConfiguration configuration) : ControllerBase
    {
        private readonly DatabaseHandler _dbHandler = dbHandler;
        private readonly RosterOptimizer _rosterOptimizer = rosterOptimizer;
        private readonly ILogger<RosterController> _logger = logger;
        private readonly OrToolsSessionService _orToolsSessionService = orToolsSessionService;
        private readonly IConfiguration _configuration = configuration;

        [HttpPost("optimize")]
        public async Task<IActionResult> OptimizeRoster([FromBody] OptimizationRequest request)
        {
            if (request == null)
            {
                _logger.LogWarning("Received null OptimizationRequest");
                return BadRequest("Invalid request data");
            }

            try
            {
                _logger.LogInformation("Starting optimization for Smena: {Smena}, Datum: {Datum}", request.Smena, request.Datum);

                var (datumStart, datumEnd) = await _dbHandler.GetSmenaDurationAsync(request.Datum, request.Smena!);
                if (!datumStart.HasValue || !datumEnd.HasValue)
                {
                    _logger.LogWarning("Unable to determine shift duration for Smena: {Smena}, {Datum}", request.Datum, request.Smena);
                    return BadRequest("Unable to determine shift duration");
                }

                var (konfiguracije, inicijalniRaspored) = await LoadInitialDataAsync(datumStart.Value, datumEnd.Value);
                if (konfiguracije == null || inicijalniRaspored == null)
                {
                    return BadRequest("No initial schedule or configuration data found for the given time period");
                }

                if (request.UpdatedConfigurations != null && request.UpdatedConfigurations.Count > 0)
                {
                    var updatedKonfiguracije = await ApplyConfigurationUpdatesAsync(request.UpdatedConfigurations, datumStart.Value, datumEnd.Value);
                    if (updatedKonfiguracije == null)
                    {
                        return BadRequest("No configurations found for the given time period after updates");
                    }
                    konfiguracije = updatedKonfiguracije;
                }

                var timeSlots = TimeUtils.CreateTimeSlots(datumStart.Value, datumEnd.Value);
                if (timeSlots == null || timeSlots.Count == 0)
                {
                    _logger.LogWarning("Failed to create time slots for period: {datumStart} to {datumEnd}", datumStart, datumEnd);
                    return BadRequest("Failed to create time slots");
                }

                int slotDuration = _configuration.GetValue<int>("OptimizationSettings:SlotDurationMinutes", 30);
                _rosterOptimizer.SetSlotDuration(slotDuration);

                var optimizationResponse = await _rosterOptimizer.OptimizeRoster(
                    request.Smena!,
                    request.Datum,
                    konfiguracije,
                    inicijalniRaspored,
                    timeSlots,
                    request.MaxExecTime,
                    request.MaxOptimalSolutions,
                    request.MaxZeroShortageSlots,
                    request.UseLNS,
                    request.SelectedOperativeWorkplaces,
                    request.SelectedEmployees,
                    request.UseSimulatedAnnealing,
                    request.UseManualAssignments);

                if (optimizationResponse.OptimizedResults != null && optimizationResponse.OptimizedResults.Count > 0)
                {
                    _logger.LogInformation("Optimization completed successfully");
                    return Ok(new
                    {
                        optimizationResponse.OptimizedResults,
                        optimizationResponse.NonOptimizedResults,
                        optimizationResponse.AllResults,
                        optimizationResponse.InitialAssignments,
                        optimizationResponse.ConfigurationLabels,
                        optimizationResponse.SlotShortages,
                        optimizationResponse.Statistics
                    });
                }
                else
                {
                    _logger.LogWarning("Optimization failed (returned null or empty results)");
                    return BadRequest("Optimization failed");
                }
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Invalid argument provided for roster optimization.");
                return BadRequest(ex.Message);
            }
            catch (System.Data.Common.DbException ex)
            {
                _logger.LogError(ex, "A database error occurred during roster optimization.");
                return StatusCode(503, "A database error occurred. Please try again later.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred during roster optimization");
                return StatusCode(500, "An unexpected error occurred during optimization");
            }
        }

        private async Task<(DataTable? Konfiguracije, DataTable? InicijalniRaspored)> LoadInitialDataAsync(DateTime datumStart, DateTime datumEnd)
        {
            _logger.LogInformation("Loading initial data for period: {datumStart} to {datumEnd}", datumStart, datumEnd);

            var konfiguracije = await _dbHandler.LoadTimeSlotConfigurationsAsync(datumStart, datumEnd);
            if (konfiguracije == null || konfiguracije.Rows.Count == 0)
            {
                _logger.LogWarning("No configurations found for period: {datumStart} to {datumEnd}", datumStart, datumEnd);
                return (null, null);
            }
            _logger.LogInformation("Loaded {konfiguracijeCount} configurations", konfiguracije.Rows.Count);

            var inicijalniRaspored = await _dbHandler.FillByDatumAsync(datumStart, datumEnd);
            if (inicijalniRaspored == null || inicijalniRaspored.Rows.Count == 0)
            {
                _logger.LogWarning("No initial schedule data found for period: {datumStart} to {datumEnd}", datumStart, datumEnd);
                return (konfiguracije, null);
            }
            _logger.LogInformation("Loaded initial schedule with {EntryCount} entries", inicijalniRaspored.Rows.Count);

            return (konfiguracije, inicijalniRaspored);
        }

        private async Task<DataTable?> ApplyConfigurationUpdatesAsync(List<ConfigurationUpdate> updatedConfigurations, DateTime datumStart, DateTime datumEnd)
        {
            _logger.LogInformation("Applying {Count} configuration updates", updatedConfigurations.Count);

            foreach (var update in updatedConfigurations)
            {
                var timeSlotParts = update.TimeSlot.Split('-');
                if (timeSlotParts.Length != 2 || !DateTime.TryParse(timeSlotParts[0], out var startTime) || !DateTime.TryParse(timeSlotParts[1], out var endTime))
                {
                    _logger.LogWarning("Invalid time slot format: {TimeSlot}", update.TimeSlot);
                    continue;
                }

                if (!string.IsNullOrEmpty(update.Configuration))
                {
                    var configParts = update.Configuration.Split(':');
                    if (configParts.Length == 2)
                    {
                        await _dbHandler.UpdateConfigurationAsync(startTime, endTime, configParts[0], configParts[1]);
                        _logger.LogInformation("Updated configuration for slot {startTime} - {endTime}: {Configuration}", startTime, endTime, update.Configuration);
                    }
                    else
                    {
                        _logger.LogWarning("Invalid configuration format: {Configuration}", update.Configuration);
                    }
                }
                else
                {
                    await _dbHandler.RemoveConfigurationAsync(startTime, endTime);
                    _logger.LogInformation("Removed configuration for slot {startTime} - {endTime}", startTime, endTime);
                }
            }

            // Reload configurations after updates
            var reloadedKonfiguracije = await _dbHandler.LoadTimeSlotConfigurationsAsync(datumStart, datumEnd);
            if (reloadedKonfiguracije == null || reloadedKonfiguracije.Rows.Count == 0)
            {
                _logger.LogWarning("No configurations found after updates for period: {datumStart} to {datumEnd}", datumStart, datumEnd);
                return null;
            }

            _logger.LogInformation("Reloaded {konfiguracijeCount} configurations after updates", reloadedKonfiguracije.Rows.Count);
            return reloadedKonfiguracije;
        }

        [HttpGet("get-roster")]
        public async Task<IActionResult> GetInitialRoster([FromQuery] DateTime datum, [FromQuery] string smena)
        {
            try
            {
                _logger.LogInformation("Primljen zahtev za roster. Datum: {Datum}, Smena: {Smena}", datum, smena);

                var (datumStart, datumEnd) = await _dbHandler.GetSmenaDurationAsync(datum, smena);
                if (!datumStart.HasValue || !datumEnd.HasValue)
                {
                    _logger.LogWarning("Nije moguće odrediti trajanje smene za datum {Datum} i smenu {Smena}", datum, smena);
                    return BadRequest("Unable to determine shift duration");
                }

                var initialRoster = await _dbHandler.FillByDatumAsync(datumStart.Value, datumEnd.Value);
                var configurationSchedule = await _dbHandler.LoadTimeSlotConfigurationsAsync(datumStart.Value, datumEnd.Value);

                var response = new RosterResponse
                {
                    InitialRoster = new InitialRoster
                    {
                        ShiftStart = datumStart.Value,
                        ShiftEnd = datumEnd.Value,
                        Roster = MapToRosterEntries(initialRoster!),
                        ConfigurationSchedule = MapToConfigurationEntries(configurationSchedule),
                    },
                    OptimizedRoster = new OptimizedRoster
                    {
                        ShiftStart = datumStart.Value,
                        ShiftEnd = datumEnd.Value,
                        Roster = [], // Ovo će biti prazno dok se ne izvrši optimizacija
                        ConfigurationSchedule = MapToConfigurationEntries(configurationSchedule)
                    }
                };

                return Ok(response);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Invalid argument provided for fetching roster.");
                return BadRequest(ex.Message);
            }
            catch (System.Data.Common.DbException ex)
            {
                _logger.LogError(ex, "A database error occurred while fetching the roster.");
                return StatusCode(503, "A database error occurred. Please try again later.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while fetching the roster");
                return StatusCode(500, "An unexpected error occurred while fetching the roster");
            }
        }

        private static List<RosterEntry> MapToRosterEntries(DataTable dataTable)
        {
            return [.. dataTable.AsEnumerable().Select(row => new RosterEntry
            {
                Sifra = row.Field<string>("sifra"),
                PrezimeIme = row.Field<string>("PrezimeIme"),
                Smena = row.Field<string>("smena"),
                ORM = row.Field<string>("ORM"),
                Redosled = row["Redosled"] != DBNull.Value ? Convert.ToInt16(row["Redosled"]) : 0,
                Par = row.Field<string>("Par"),
                Datum = Convert.ToDateTime(row["Datum"]),
                VremeStart = Convert.ToDateTime(row["VremeStart"]),
                DatumOd = Convert.ToDateTime(row["datumOd"]),
                DatumDo = Convert.ToDateTime(row["datumDo"]),
                Sektor = row.Field<string>("sektor"),
                Flag = row.Field<string>("Flag")
            }).OrderBy(c=> c.Redosled).ThenBy(c => c.VremeStart).ThenBy(c=> c.Smena).ThenBy(c => c.PrezimeIme)];
        }

        private static List<ConfigurationEntry> MapToConfigurationEntries(DataTable dataTable)
        {
            return [.. dataTable.AsEnumerable().Select(row => new ConfigurationEntry
            {
                DatumOd = Convert.ToDateTime(row["datumOd"]),
                DatumDo = Convert.ToDateTime(row["datumDo"]),
                OznakaKonfiguracije = row.Field<string>("Konfiguracija") ?? ""
            })];
        }

        [HttpGet("test-historical-data")]
        public async Task<IActionResult> TestHistoricalData([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate, [FromQuery] string? shiftType)
        {
            try
            {
                // Podrazumevane vrednosti ako nisu pruženi parametri
                var from = startDate ?? DateTime.Now.AddMonths(-3);
                var to = endDate ?? DateTime.Now;

                _logger.LogInformation($"Testing historical data from {from} to {to}, shift: {shiftType ?? "all"}");

                // Pozovite vašu metodu za dobijanje istorijskih podataka
                var historicalData = await _dbHandler.GetHistoricalSchedules(from, to, shiftType);

                // Pripremite statistiku za lakši pregled
                var summary = new
                {
                    TotalSchedules = historicalData.Count,
                    ScheduleDetails = historicalData.Select(s => new
                    {
                        s.ScheduleId,
                        s.Date,
                        s.ShiftType,
                        TotalAssignments = s.Assignments.Count,
                        TotalConfigurations = s.Configurations.Count,
                        SampleControllers = s.Assignments
                            .GroupBy(a => a.ControllerId)
                            .Take(5)
                            .Select(g => g.Key),
                        SampleConfigurations = s.Configurations.Take(5)
                    }).ToList()
                };

                return Ok(summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing historical data");
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        [HttpGet("configurations")]
        public async Task<IActionResult> GetAvailableConfigurations([FromQuery] DateTime date)
        {
            try
            {
                _logger.LogInformation($"Fetching available configurations for date: {date:yyyy-MM-dd}");

                // Učitavanje dostupnih konfiguracija za dati datum iz baze podataka
                var configurations = await _dbHandler.GetAvailableConfigurationsAsync(date);

                if (configurations == null || !configurations.Any())
                {
                    _logger.LogWarning($"No configurations found for date: {date:yyyy-MM-dd}");
                    return Ok(new List<object>()); // Vraćamo praznu listu umesto 404 Not Found
                }

                // Mapa konfiguracija u format koji očekuje frontend
                var formattedConfigurations = configurations.Select(config => new
                {
                    id = config.Id,
                    code = config.Konfiguracija,
                    name = config.Naziv ?? config.Konfiguracija,
                    type = config.Vrsta ?? "TX",
                    cluster = config.Cluster,
                    validFrom = config.VaziOd,
                    validTo = config.VaziDo,
                    sectors = config.Sektori
                });

                return Ok(formattedConfigurations);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while fetching configurations");
                return StatusCode(500, "An error occurred while fetching configurations");
            }
        }

        [HttpPost("create-optimization-session")]
        public async Task<IActionResult> CreateOptimizationSession([FromBody] CreateSessionRequest request)
        {
            try
            {
                var sessionId = await _orToolsSessionService.CreateSession(request.Smena, request.Datum);

                return Ok(new { SessionId = sessionId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating optimization session");
                return StatusCode(500, "An error occurred while creating optimization session");
            }
        }

        [HttpPost("optimize-with-session")]
        public async Task<IActionResult> OptimizeWithSession([FromBody] OptimizeWithSessionRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.SessionId))
            {
                _logger.LogWarning("Received invalid OptimizeWithSessionRequest");
                return BadRequest("Invalid request data");
            }

            try
            {
                _logger.LogInformation("Starting OR-Tools optimization for session: {SessionId}", request.SessionId);

                var (datumStart, datumEnd) = await _dbHandler.GetSmenaDurationAsync(request.OptimizationRequest.Datum, request.OptimizationRequest.Smena!);

                if (!datumStart.HasValue || !datumEnd.HasValue)
                {
                    _logger.LogWarning("Unable to determine shift duration for session: {SessionId}", request.SessionId);
                    return BadRequest("Unable to determine shift duration");
                }

                var konfiguracije = await _dbHandler.LoadTimeSlotConfigurationsAsync(datumStart.Value, datumEnd.Value);
                var inicijalniRaspored = await _dbHandler.FillByDatumAsync(datumStart.Value, datumEnd.Value);
                var timeSlots = TimeUtils.CreateTimeSlots(datumStart.Value, datumEnd.Value);

                if (konfiguracije == null || inicijalniRaspored == null || timeSlots == null || !timeSlots.Any())
                {
                    return BadRequest("Failed to load required data for optimization");
                }

                int slotDuration = _configuration.GetValue<int>("OptimizationSettings:SlotDurationMinutes", 30);
                _rosterOptimizer.SetSlotDuration(slotDuration);

                // Uvek koristimo OR-Tools (useSimulatedAnnealing = false)
                var optimizationResponse = await _rosterOptimizer.OptimizeRoster(
                    request.OptimizationRequest.Smena!,
                    request.OptimizationRequest.Datum,
                    konfiguracije,
                    inicijalniRaspored,
                    timeSlots,
                    request.OptimizationRequest.MaxExecTime,
                    request.OptimizationRequest.MaxOptimalSolutions,
                    request.OptimizationRequest.MaxZeroShortageSlots,
                    request.OptimizationRequest.UseLNS,
                    request.OptimizationRequest.SelectedOperativeWorkplaces,
                    request.OptimizationRequest.SelectedEmployees,
                    useSimulatedAnnealing: false, // Uvek OR-Tools
                    request.OptimizationRequest.UseManualAssignments,
                    request.OptimizationRequest.RandomSeed,
                    request.OptimizationRequest.UseRandomization ?? true);

                // Kreiraj OR-Tools run objekat
                var orToolsRun = new OrToolsOptimizationRun
                {
                    CreatedAt = DateTime.Now,
                    Request = request.OptimizationRequest,
                    Response = optimizationResponse,
                    Parameters = new OrToolsParameters
                    {
                        MaxTimeInSeconds = request.OptimizationRequest.MaxExecTime,
                        MaxOptimalSolutions = request.OptimizationRequest.MaxOptimalSolutions,
                        MaxZeroShortageSlots = request.OptimizationRequest.MaxZeroShortageSlots,
                        UseLNS = request.OptimizationRequest.UseLNS,
                        UseManualAssignments = request.OptimizationRequest.UseManualAssignments,
                        SelectedOperativeWorkplaces = request.OptimizationRequest.SelectedOperativeWorkplaces?.ToList() ?? new List<string>(),
                        SelectedEmployees = request.OptimizationRequest.SelectedEmployees?.ToList() ?? new List<string>(),
                        RandomSeed = request.OptimizationRequest.RandomSeed,
                        UseRandomization = request.OptimizationRequest.UseRandomization ?? true
                    },
                    SolverStatus = optimizationResponse.Statistics.SolutionStatus,
                    ObjectiveValue = optimizationResponse.Statistics.ObjectiveValue,
                    SolvingTime = optimizationResponse.Statistics.WallTime
                };

                // Dodaj run u sesiju
                _orToolsSessionService.AddOptimizationRun(request.SessionId, orToolsRun, request.Description);

                // Vrati odgovor sa informacijama o navigaciji
                var navigationInfo = _orToolsSessionService.GetNavigationInfo(request.SessionId);

                if (optimizationResponse.OptimizedResults != null && optimizationResponse.OptimizedResults.Count > 0)
                {
                    _logger.LogInformation("OR-Tools optimization completed successfully for session: {SessionId}", request.SessionId);
                    return Ok(new
                    {
                        optimizationResponse.OptimizedResults,
                        optimizationResponse.NonOptimizedResults,
                        optimizationResponse.AllResults,
                        optimizationResponse.InitialAssignments,
                        optimizationResponse.ConfigurationLabels,
                        optimizationResponse.SlotShortages,
                        optimizationResponse.Statistics,
                        SessionId = request.SessionId,
                        NavigationInfo = navigationInfo
                    });
                }
                else
                {
                    _logger.LogWarning("OR-Tools optimization failed for session: {SessionId}", request.SessionId);
                    return BadRequest("Optimization failed");
                }
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Invalid argument provided for session-based optimization. SessionId: {SessionId}", request.SessionId);
                return BadRequest(ex.Message);
            }
            catch (System.Data.Common.DbException ex)
            {
                _logger.LogError(ex, "A database error occurred during session-based optimization. SessionId: {SessionId}", request.SessionId);
                return StatusCode(503, "A database error occurred. Please try again later.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred during OR-Tools optimization for session: {SessionId}", request.SessionId);
                return StatusCode(500, "An unexpected error occurred during optimization");
            }
        }

        [HttpGet("navigation-info/{sessionId}")]
        public IActionResult GetNavigationInfo(string sessionId)
        {
            try
            {
                var navigationInfo = _orToolsSessionService.GetNavigationInfo(sessionId);
                return Ok(navigationInfo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting navigation info for session: {SessionId}", sessionId);
                return StatusCode(500, "An error occurred while getting navigation info");
            }
        }

        [HttpPost("navigate-previous/{sessionId}")]
        public IActionResult NavigatePrevious(string sessionId)
        {
            try
            {
                var previousRun = _orToolsSessionService.NavigatePrevious(sessionId);
                if (previousRun != null)
                {
                    var navigationInfo = _orToolsSessionService.GetNavigationInfo(sessionId);
                    return Ok(new
                    {
                        previousRun.Response.OptimizedResults,
                        previousRun.Response.NonOptimizedResults,
                        previousRun.Response.AllResults,
                        previousRun.Response.InitialAssignments,
                        previousRun.Response.ConfigurationLabels,
                        previousRun.Response.SlotShortages,
                        previousRun.Response.Statistics,
                        SessionId = sessionId,
                        NavigationInfo = navigationInfo
                    });
                }
                else
                {
                    return BadRequest("Cannot navigate to previous optimization");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error navigating to previous optimization for session: {SessionId}", sessionId);
                return StatusCode(500, "An error occurred while navigating");
            }
        }

        [HttpPost("navigate-next/{sessionId}")]
        public IActionResult NavigateNext(string sessionId)
        {
            try
            {
                var nextRun = _orToolsSessionService.NavigateNext(sessionId);
                if (nextRun != null)
                {
                    var navigationInfo = _orToolsSessionService.GetNavigationInfo(sessionId);
                    return Ok(new
                    {
                        nextRun.Response.OptimizedResults,
                        nextRun.Response.NonOptimizedResults,
                        nextRun.Response.AllResults,
                        nextRun.Response.InitialAssignments,
                        nextRun.Response.ConfigurationLabels,
                        nextRun.Response.SlotShortages,
                        nextRun.Response.Statistics,
                        SessionId = sessionId,
                        NavigationInfo = navigationInfo
                    });
                }
                else
                {
                    return BadRequest("Cannot navigate to next optimization");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error navigating to next optimization for session: {SessionId}", sessionId);
                return StatusCode(500, "An error occurred while navigating");
            }
        }

        [HttpGet("optimization-history/{sessionId}")]
        public IActionResult GetOptimizationHistory(string sessionId)
        {
            try
            {
                var history = _orToolsSessionService.GetOptimizationHistory(sessionId);
                return Ok(new
                {
                    SessionId = sessionId,
                    History = history.Select(run => new
                    {
                        run.Id,
                        CreatedAt = run.CreatedAt.ToString("o"), // ISO 8601 format
                        run.Description,
                        run.SolverStatus,
                        run.ObjectiveValue,
                        run.SolvingTime,
                        Statistics = new
                        {
                            run.Response.Statistics.SuccessRate,
                            run.Response.Statistics.SlotsWithShortage,
                            run.Response.Statistics.SlotsWithExcess,
                            run.Response.Statistics.FormattedSuccessRate,
                            run.Response.Statistics.SolutionStatus
                        }
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting optimization history for session: {SessionId}", sessionId);
                return StatusCode(500, "An error occurred while getting optimization history");
            }
        }

        [HttpGet("best-run/{sessionId}")]
        public IActionResult GetBestRun(string sessionId)
        {
            try
            {
                var session = _orToolsSessionService.GetSession(sessionId);
                if (session == null)
                {
                    return NotFound("Session not found");
                }

                var bestRun = session.GetBestRun();
                if (bestRun != null)
                {
                    var bestRunIndex = session.OptimizationRuns.IndexOf(bestRun);
                    if (bestRunIndex >= 0)
                    {
                        session.CurrentRunIndex = bestRunIndex;
                        _logger.LogInformation($"Switched to best run (index {bestRunIndex}) in session {sessionId}");
                    }

                    var navigationInfo = _orToolsSessionService.GetNavigationInfo(sessionId);

                    return Ok(new
                    {
                        bestRun.Response.OptimizedResults,
                        bestRun.Response.NonOptimizedResults,
                        bestRun.Response.AllResults,
                        bestRun.Response.InitialAssignments,
                        bestRun.Response.ConfigurationLabels,
                        bestRun.Response.SlotShortages,
                        bestRun.Response.Statistics,
                        SessionId = sessionId,
                        NavigationInfo = navigationInfo,
                        RunInfo = new
                        {
                            bestRun.Id,
                            bestRun.CreatedAt,
                            bestRun.Description,
                            bestRun.SolverStatus,
                            bestRun.ObjectiveValue
                        }
                    });
                }
                else
                {
                    return NotFound("No valid optimization runs found in session");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting best run for session: {SessionId}", sessionId);
                return StatusCode(500, "An error occurred while getting best run");
            }
        }

        [HttpPost("load-run/{sessionId}/{runId}")]
        public IActionResult LoadOptimizationRun(string sessionId, int runId)
        {
            try
            {
                var session = _orToolsSessionService.GetSession(sessionId);
                if (session == null)
                {
                    _logger.LogWarning($"Session {sessionId} not found");
                    return NotFound("Session not found");
                }

                var run = session.OptimizationRuns.FirstOrDefault(r => r.Id == runId);
                if (run == null)
                {
                    _logger.LogWarning($"Run {runId} not found in session {sessionId}");
                    return NotFound($"Run {runId} not found");
                }

                // postavi CurrentRunIndex na izabranu optimizaciju
                var runIndex = session.OptimizationRuns.IndexOf(run);
                session.CurrentRunIndex = runIndex;

                _logger.LogInformation($"Loaded run {runId} (index {runIndex}) in session {sessionId}");

                var navigationInfo = _orToolsSessionService.GetNavigationInfo(sessionId);

                return Ok(new
                {
                    run.Response.OptimizedResults,
                    run.Response.NonOptimizedResults,
                    run.Response.AllResults,
                    run.Response.InitialAssignments,
                    run.Response.ConfigurationLabels,
                    run.Response.SlotShortages,
                    run.Response.Statistics,
                    SessionId = sessionId,
                    NavigationInfo = navigationInfo
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading run {runId} in session {sessionId}");
                return StatusCode(500, "An error occurred while loading optimization run");
            }
        }
    }

    // Request modeli
    public class CreateSessionRequest
    {
        [Required]
        public string Smena { get; set; } = string.Empty;
        [Required]
        public DateTime Datum { get; set; }
    }

    public class OptimizeWithSessionRequest
    {
        [Required]
        public string SessionId { get; set; } = string.Empty;
        [Required]
        public OptimizationRequest OptimizationRequest { get; set; } = new();
        public string? Description { get; set; }
    }
}
