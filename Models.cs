namespace BatteryTestStation;

public enum CellStatus
{
    Pending,
    Diagnostics,
    WaitingForCharger,
    Charging,
    WaitingForCalibration,
    Calibration,
    Completed,
    Canceled,
    Faulted
}

public class BatteryCell
{
    public int Id { get; set; }
    public int DiagnosticIterations { get; set; }
    public int ChargeDurationMs { get; set; }
    public int CalibrationDurationMs { get; set; }
    
    public BatteryCell(int id, int diagnosticIterations, int chargeDurationMs, int calibrationDurationMs)
    {
        Id = id;
        DiagnosticIterations = diagnosticIterations;
        ChargeDurationMs = chargeDurationMs;
        CalibrationDurationMs = calibrationDurationMs;
    }
}

public class CellResult
{
    public int CellId { get; set; }
    public CellStatus FinalStatus { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string? ErrorMessage { get; set; }
    
    public TimeSpan Duration => EndTime - StartTime;
}

public class StationConfiguration
{
    public int BatchSize { get; set; } = 20;
    public int ChargingSlots { get; set; } = 4;
    public int DiagnosticIterationsPerCell { get; set; } = 100000;
    public int ChargeDurationMinMs { get; set; } = 300;
    public int ChargeDurationMaxMs { get; set; } = 900;
    public int CalibrationDurationMinMs { get; set; } = 100;
    public int CalibrationDurationMaxMs { get; set; } = 300;
    public int? AutoCancelTimeoutMs { get; set; } = null;
    
    public static StationConfiguration Default => new StationConfiguration();
}

public class ProcessingStatistics
{
    public int TotalCells { get; set; }
    public int CompletedCount { get; set; }
    public int CanceledCount { get; set; }
    public int FaultedCount { get; set; }
    public int SuccessfulCharges { get; set; }
    public int SuccessfulCalibrations { get; set; }
    public int CanceledChargingWaits { get; set; }
    public int CanceledCalibrationWaits { get; set; }
    public TimeSpan TotalProcessingTime { get; set; }
    
    public void PrintStatistics()
    {
        Console.WriteLine("\n=== ИТОГОВАЯ СТАТИСТИКА ===");
        Console.WriteLine($"Всего ячеек: {TotalCells}");
        Console.WriteLine($"Успешно завершено: {CompletedCount}");
        Console.WriteLine($"Отменено: {CanceledCount}");
        Console.WriteLine($"Завершено с ошибкой: {FaultedCount}");
        Console.WriteLine($"Успешных зарядок: {SuccessfulCharges}");
        Console.WriteLine($"Успешных калибровок: {SuccessfulCalibrations}");
        Console.WriteLine($"Прерванных ожиданий зарядки: {CanceledChargingWaits}");
        Console.WriteLine($"Прерванных ожиданий калибровки: {CanceledCalibrationWaits}");
        Console.WriteLine($"Общее время выполнения: {TotalProcessingTime:hh\\:mm\\:ss\\.fff}");
        
        // Проверка сумм
        int sum = CompletedCount + CanceledCount + FaultedCount;
        Console.WriteLine($"Сумма статусов ({sum}) должна равняться {TotalCells}: {(sum == TotalCells ? "✓" : "✗")}");
    }
}