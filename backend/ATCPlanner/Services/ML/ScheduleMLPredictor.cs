using ATCPlanner.Data;
using Microsoft.ML;

namespace ATCPlanner.Services.ML
{
    /// <summary>
    /// Glavna klasa za mašinsko učenje kod rasporeda kontrolora letenja
    /// </summary>
    public class ScheduleMLPredictor
    {
        private readonly ILogger<ScheduleMLPredictor> _logger;
        private readonly DatabaseHandler _databaseHandler;
        private readonly MLContext _mlContext;

        public ScheduleMLPredictor(ILogger<ScheduleMLPredictor> logger, DatabaseHandler databaseHandler, MLContext mlContext)
        {
            _logger = logger;
            _databaseHandler = databaseHandler;
            _mlContext = new MLContext(seed: 42); // fiksni seed za ponovljivost rezultata
        }

        /// <summary>
        /// Inicijalizuje ML prediktor, ucitava istorijske podatke i trenira model
        /// </summary>
        //public async Task Initialize(int historyMonths = 12)
        //{
        //    try
        //    {
        //        _logger.LogInformation($"Initializing ML predictor with {historyMonths} months of history");

        //        // 1. Ucitavanje istorijskih podataka
        //        var startDate = DateTime.Now.AddMonths(-historyMonths);
        //        var historicalData = await _databaseHandler.GetHistoricalSchedules(_databaseHandler, )
        //    }
        //}
    }
}
