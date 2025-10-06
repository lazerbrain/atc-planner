using ATCPlanner.Models;
using System.Collections.Concurrent;

namespace ATCPlanner.Services
{
    public class OrToolsSessionService
    {
        private readonly ILogger<OrToolsSessionService> _logger;
        private readonly ConcurrentDictionary<string, OrToolsOptimizationSession> _sessions = new();

        // Cleanup timer za stare verzije
        private readonly Timer _cleanupTimer;
        private readonly TimeSpan _sessionTimeout = TimeSpan.FromHours(12);

        public OrToolsSessionService(ILogger<OrToolsSessionService> logger)
        {
            this._logger = logger;

            // pokretni cleanup timer svakih 2 sata
            _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromHours(2), TimeSpan.FromHours(2));
        }

        private void CleanupExpiredSessions(object? state)
        {
            var expiredSessions = _sessions.Where(kvp => DateTime.Now - kvp.Value.CreatedAt > _sessionTimeout).Select(kvp => kvp.Key).ToList();

            foreach (var sessionId in expiredSessions)
            {
                RemoveSession(sessionId);
            }

            if (expiredSessions.Any())
            {
                _logger.LogInformation($"Cleaned up {expiredSessions.Count} expired OR-Tools sessions");
            }
        }

        public void RemoveSession(string sessionId) {
            if (_sessions.TryRemove(sessionId, out _))
            {
                _logger.LogInformation($"Removed OR-Tools session {sessionId}");
            }
        }

        public string CreateSession(string smena, DateTime datum)
        {
            var session = new OrToolsOptimizationSession
            {
                Smena = smena,
                Datum = datum
            };

            _sessions.TryAdd(session.SessionId, session);
            _logger.LogInformation($"Created OR-Tools session {session.SessionId} for {smena} on {datum:yyyy-MM-dd}");


            return session.SessionId;
        }

        public OrToolsOptimizationSession? GetSession(string sessionId)
        {
            _sessions.TryGetValue(sessionId, out var session);

            return session;
        }

        public void AddOptimizationRun(string sessionId, OrToolsOptimizationRun run, string? description = null)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                if (!string.IsNullOrEmpty(description))
                {
                    run.Description = description;
                }
                else
                {
                    // automatski generisi opis na osnovu parametara
                    run.Description = GenerateRunDescription(run);
                }

                session.AddOptimizationRun(run);
                _logger.LogInformation($"Added OR-Tools run {run.Id} to session {sessionId}. Status: {run.SolverStatus}, Objective: {run.ObjectiveValue:F2}");
            }
            else
            {
                _logger.LogWarning($"Session {sessionId} not found when trying to add optimization run");
            }
        }

        public string GenerateRunDescription(OrToolsOptimizationRun run)
        {
            var parts = new List<string>();

            parts.Add($"OR-Tools");
            parts.Add($"Max time: {run.Parameters.MaxTimeInSeconds}s");

            if (run.Parameters.MaxOptimalSolutions.HasValue)
            {
                parts.Add($"Max optimal: {run.Parameters.MaxOptimalSolutions}");
            }

            if (run.Parameters.MaxZeroShortageSlots.HasValue)
            {
                parts.Add($"Max zero shortage: {run.Parameters.MaxZeroShortageSlots}");
            }

            if (run.Parameters.UseLNS)
            {
                parts.Add("LNS");
            }

            if (!run.Parameters.UseManualAssignments)
            {
                parts.Add("No manual");
            }

            if (run.Parameters.SelectedEmployees.Any())
            {
                parts.Add($"{run.Parameters.SelectedEmployees.Count} employees");
            }

            return string.Join(", ", parts);
        }

        public OrToolsOptimizationRun? GetCurrentRun(string sessionId)
        {
            return GetSession(sessionId)?.GetCurrentRun();
        }

        public OrToolsOptimizationRun? NavigateNext(string sessionId)
        {
            var session = GetSession(sessionId);
            if (session != null)
            {
                var result = session.NavigateNext();
                if (result != null)
                {
                    _logger.LogInformation($"Navigated to next OR-Tools run in session {sessionId}: Run {result.Id}");
                }
                return result;
            }
            return null;
        }

        public OrToolsOptimizationRun? NavigatePrevious(string sessionId)
        {
            var session = GetSession(sessionId);
            if (session != null)
            {
                var result = session.NavigatePrevious();
                if (result != null)
                {
                    _logger.LogInformation($"Navigated to previous OR-Tools run in session {sessionId}: Run {result.Id}");
                }
                return result;
            }
            return null;
        }

        public OrToolsNavigationInfo? GetNavigationInfo(string sessionId)
        {
            var session = GetSession(sessionId);
            if (session != null)
            {
                var currentRun = session.GetCurrentRun();
                return new OrToolsNavigationInfo
                {
                    CanNavigatePrevious = session.CanNavigatePrevious(),
                    CanNavigateNext = session.CanNavigateNext(),
                    CurrentRunNumber = session.CurrentRunIndex + 1,
                    TotalRuns = session.OptimizationRuns.Count,
                    CurrentRunDescription = currentRun?.Description ?? "",
                    CurrentRunTimestamp = currentRun?.CreatedAt ?? DateTime.MinValue,
                    SolverStatus = currentRun?.SolverStatus ?? "",
                    ObjectiveValue = currentRun?.ObjectiveValue ?? 0,
                    SuccessRate = currentRun?.Response.Statistics.SuccessRate ?? 0,
                    SlotsWithShortage = currentRun?.Response.Statistics.SlotsWithShortage ?? 0
                };
            }

            return new OrToolsNavigationInfo();
        }

        public List<OrToolsOptimizationRun> GetOptimizationHistory(string sessionId)
        {
            return GetSession(sessionId)?.OptimizationRuns ?? new List<OrToolsOptimizationRun>();
        }

        public OrToolsOptimizationRun? GetBestRun(string sessionId)
        {
            return GetSession(sessionId)?.GetBestRun();
        }

        public bool HasMultipleRuns(string sessionId)
        {
            var session = GetSession(sessionId);
            return session != null && session.OptimizationRuns.Count > 1;
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }

    }
}
