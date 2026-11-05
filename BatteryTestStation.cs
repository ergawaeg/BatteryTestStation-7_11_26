using System.Collections.Concurrent;
using System.Diagnostics;

namespace BatteryTestStation;

public class BatteryTestStation : IDisposable
{
    private readonly StationConfiguration _config;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly SemaphoreSlim _chargingSemaphore;
    private readonly Mutex _calibrationMutex;
    private readonly CountdownEvent _countdownEvent;
    private readonly ConcurrentDictionary<int, CellResult> _results;
    private readonly ConcurrentDictionary<CellStatus, int> _statusCounts;
    private readonly object _consoleLock = new object();
    
    private int _successfulCharges = 0;
    private int _successfulCalibrations = 0;
    private int _canceledChargingWaits = 0;
    private int _canceledCalibrationWaits = 0;
    
    public ProcessingStatistics Statistics { get; private set; }
    public DateTime StartTime { get; private set; }
    public DateTime EndTime { get; private set; }
    
    public BatteryTestStation(StationConfiguration config)
    {
        _config = config;
        _cancellationTokenSource = new CancellationTokenSource();
        _chargingSemaphore = new SemaphoreSlim(config.ChargingSlots, config.ChargingSlots);
        _calibrationMutex = new Mutex();
        _results = new ConcurrentDictionary<int, CellResult>();
        _statusCounts = new ConcurrentDictionary<CellStatus, int>();
        _countdownEvent = new CountdownEvent(config.BatchSize);
        Statistics = new ProcessingStatistics();
        
        InitializeStatusCounts();
    }
    
    private void InitializeStatusCounts()
    {
        foreach (CellStatus status in Enum.GetValues(typeof(CellStatus)))
        {
            _statusCounts[status] = 0;
        }
    }
    
    public void StartProcessing()
    {
        StartTime = DateTime.Now;
        Statistics.TotalCells = _config.BatchSize;
        
        LogEvent("ПАРТИЯ СОЗДАНА", 0, $"Количество ячеек: {_config.BatchSize}, Слотов зарядки: {_config.ChargingSlots}");
        
        // Вывод информации о пуле потоков в начале
        ThreadPool.GetAvailableThreads(out int workerThreads, out int completionPortThreads);
        LogEvent("СОСТОЯНИЕ ПУЛА ПОТОКОВ", 0, 
            $"Рабочие потоки: {workerThreads}, Потоки завершения ввода-вывода: {completionPortThreads}");
        
        // Создание партии ячеек
        var cells = CreateBatch();
        
        // Постановка задач в пул потоков
        foreach (var cell in cells)
        {
            var result = new CellResult
            {
                CellId = cell.Id,
                StartTime = DateTime.Now,
                FinalStatus = CellStatus.Pending
            };
            
            _results.TryAdd(cell.Id, result);
            UpdateStatusCount(CellStatus.Pending, 1);
            
            LogEvent("ЯЧЕЙКА ПОСТАВЛЕНА В ОЧЕРЕДЬ", cell.Id, $"Id: {cell.Id}");
            
            // Используем ThreadPool.QueueUserWorkItem как требуется в ТЗ
            ThreadPool.QueueUserWorkItem(state =>
            {
                var (cellToProcess, token) = ((BatteryCell, CancellationToken))state!;
                ProcessCell(cellToProcess, token);
            }, (cell, _cancellationTokenSource.Token));
        }
        
        // Настройка автоматической отмены, если задана
        if (_config.AutoCancelTimeoutMs.HasValue)
        {
            LogEvent("АВТОМАТИЧЕСКАЯ ОТМЕНА", 0, 
                $"Таймаут: {_config.AutoCancelTimeoutMs.Value} мс");
            
            Task.Delay(_config.AutoCancelTimeoutMs.Value)
                .ContinueWith(_ => 
                {
                    if (!_cancellationTokenSource.IsCancellationRequested)
                    {
                        LogEvent("АВТООТМЕНА", 0, "Запущена автоматическая отмена");
                        _cancellationTokenSource.Cancel();
                    }
                });
        }
    }
    
    public void WaitForCompletion()
    {
        LogEvent("ОЖИДАНИЕ ЗАВЕРШЕНИЯ", 0, "Ожидание завершения обработки всех ячеек...");
        
        try
        {
            // Ожидание завершения всех ячеек
            _countdownEvent.Wait(_cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            LogEvent("ОТМЕНА", 0, "Запрошена отмена обработки");
        }
        
        // Дополнительное ожидание при отмене (максимум 10 секунд)
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            LogEvent("ОЖИДАНИЕ ЗАВЕРШЕНИЯ ПРИ ОТМЕНЕ", 0, 
                "Ожидание завершения текущих операций...");
            
            bool completed = _countdownEvent.Wait(TimeSpan.FromSeconds(10));
            if (!completed)
            {
                LogEvent("ПРЕДУПРЕЖДЕНИЕ", 0, 
                    "Не все ячейки завершились в течение 10 секунд после отмены");
            }
        }
        
        EndTime = DateTime.Now;
        Statistics.TotalProcessingTime = EndTime - StartTime;
        
        // Заполнение итоговой статистики
        CalculateStatistics();
        
        // Вывод информации о пуле потоков в конце
        ThreadPool.GetAvailableThreads(out int workerThreads, out int completionPortThreads);
        LogEvent("СОСТОЯНИЕ ПУЛА ПОТОКОВ", 0, 
            $"Рабочие потоки: {workerThreads}, Потоки завершения ввода-вывода: {completionPortThreads}");
        
        LogEvent("ОБРАБОТКА ПАРТИИ ЗАВЕРШЕНА", 0, 
            $"Общее время: {Statistics.TotalProcessingTime:hh\\:mm\\:ss\\.fff}");
    }
    
    private List<BatteryCell> CreateBatch()
    {
        var cells = new List<BatteryCell>();
        var random = new Random();
        
        for (int i = 1; i <= _config.BatchSize; i++)
        {
            int chargeDuration = random.Next(
                _config.ChargeDurationMinMs, 
                _config.ChargeDurationMaxMs + 1);
            
            int calibrationDuration = random.Next(
                _config.CalibrationDurationMinMs, 
                _config.CalibrationDurationMaxMs + 1);
            
            var cell = new BatteryCell(
                i,
                _config.DiagnosticIterationsPerCell,
                chargeDuration,
                calibrationDuration);
            
            cells.Add(cell);
        }
        
        return cells;
    }
    
    private void ProcessCell(BatteryCell cell, CancellationToken token)
    {
        CellResult? result = null;
        
        try
        {
            // Получаем или создаем результат для ячейки
            if (!_results.TryGetValue(cell.Id, out result))
            {
                result = new CellResult
                {
                    CellId = cell.Id,
                    StartTime = DateTime.Now,
                    FinalStatus = CellStatus.Pending
                };
                _results.TryAdd(cell.Id, result);
            }
            
            // Проверка отмены перед началом обработки
            token.ThrowIfCancellationRequested();
            
            // 8.1. Диагностика
            UpdateCellStatus(cell.Id, CellStatus.Diagnostics, result);
            LogEvent("ДИАГНОСТИКА НАЧАТА", cell.Id);
            
            PerformDiagnostics(cell, token);
            
            LogEvent("ДИАГНОСТИКА ЗАВЕРШЕНА", cell.Id);
            
            // 8.2. Зарядка
            UpdateCellStatus(cell.Id, CellStatus.WaitingForCharger, result);
            LogEvent("ОЖИДАНИЕ СЛОТА ЗАРЯДКИ", cell.Id);
            
            bool chargingSlotAcquired = false;
            try
            {
                chargingSlotAcquired = _chargingSemaphore.Wait(0, token);
                if (!chargingSlotAcquired)
                {
                    // Ожидаем с учетом токена отмены
                    chargingSlotAcquired = _chargingSemaphore.Wait(Timeout.Infinite, token);
                }
                
                if (chargingSlotAcquired)
                {
                    UpdateCellStatus(cell.Id, CellStatus.Charging, result);
                    LogEvent("СЛОТ ЗАРЯДКИ ПОЛУЧЕН", cell.Id);
                    LogEvent("ЗАРЯДКА НАЧАТА", cell.Id, 
                        $"Длительность: {cell.ChargeDurationMs} мс");
                    
                    Interlocked.Increment(ref _successfulCharges);
                    
                    // Симуляция зарядки с проверкой отмены
                    SimulateOperation(cell.ChargeDurationMs, token);
                    
                    LogEvent("ЗАРЯДКА ЗАВЕРШЕНА", cell.Id);
                }
            }
            finally
            {
                if (chargingSlotAcquired)
                {
                    _chargingSemaphore.Release();
                    LogEvent("СЛОТ ЗАРЯДКИ ОСВОБОЖДЕН", cell.Id);
                }
                else
                {
                    Interlocked.Increment(ref _canceledChargingWaits);
                }
            }
            
            // Проверка отмены после зарядки
            token.ThrowIfCancellationRequested();
            
            // 8.3. Калибровка
            UpdateCellStatus(cell.Id, CellStatus.WaitingForCalibration, result);
            LogEvent("ОЖИДАНИЕ КАЛИБРОВОЧНОГО СТЕНДА", cell.Id);
            
            bool calibrationMutexAcquired = false;
            try
            {
                // Используем WaitHandle.WaitAny для ожидания мьютекса или отмены
                WaitHandle[] waitHandles = new WaitHandle[]
                {
                    _calibrationMutex,
                    token.WaitHandle
                };
                
                int signaledHandle = WaitHandle.WaitAny(waitHandles);
                
                if (signaledHandle == 1) // Токен отмены
                {
                    LogEvent("ОЖИДАНИЕ КАЛИБРОВКИ ПРЕРВАНО", cell.Id, "Запрошена отмена");
                    Interlocked.Increment(ref _canceledCalibrationWaits);
                    throw new OperationCanceledException(token);
                }
                
                // Мьютекс получен
                calibrationMutexAcquired = true;
                
                // Дополнительная проверка отмены
                if (token.IsCancellationRequested)
                {
                    LogEvent("КАЛИБРОВКА ОТМЕНЕНА", cell.Id, "Отмена запрошена после получения мьютекса");
                    Interlocked.Increment(ref _canceledCalibrationWaits);
                    throw new OperationCanceledException(token);
                }
                
                UpdateCellStatus(cell.Id, CellStatus.Calibration, result);
                LogEvent("КАЛИБРОВОЧНЫЙ СТЕНД ПОЛУЧЕН", cell.Id);
                LogEvent("КАЛИБРОВКА НАЧАТА", cell.Id, 
                    $"Длительность: {cell.CalibrationDurationMs} мс");
                
                Interlocked.Increment(ref _successfulCalibrations);
                
                // Симуляция калибровки с проверкой отмены
                SimulateOperation(cell.CalibrationDurationMs, token);
                
                LogEvent("КАЛИБРОВКА ЗАВЕРШЕНА", cell.Id);
            }
            finally
            {
                if (calibrationMutexAcquired)
                {
                    _calibrationMutex.ReleaseMutex();
                    LogEvent("КАЛИБРОВОЧНЫЙ СТЕНД ОСВОБОЖДЕН", cell.Id);
                }
            }
            
            // Успешное завершение
            UpdateCellStatus(cell.Id, CellStatus.Completed, result);
            LogEvent("ЯЧЕЙКА ЗАВЕРШЕНА", cell.Id, "Все этапы выполнены успешно");
        }
        catch (OperationCanceledException)
        {
            if (result != null)
            {
                UpdateCellStatus(cell.Id, CellStatus.Canceled, result);
                LogEvent("ЯЧЕЙКА ОТМЕНЕНА", cell.Id, "Обработка прервана");
            }
        }
        catch (Exception ex)
        {
            if (result != null)
            {
                UpdateCellStatus(cell.Id, CellStatus.Faulted, result);
                result.ErrorMessage = ex.Message;
                LogEvent("ПРОИЗОШЛА ОШИБКА", cell.Id, $"Ошибка: {ex.Message}");
            }
        }
        finally
        {
            // Завершаем обработку ячейки
            if (result != null)
            {
                result.EndTime = DateTime.Now;
            }
            
            // Сигнализируем о завершении обработки ячейки
            _countdownEvent.Signal();
        }
    }
    
    private void PerformDiagnostics(BatteryCell cell, CancellationToken token)
    {
        // Выполняем вычислительную диагностику с периодической проверкой отмены
        for (int i = 0; i < cell.DiagnosticIterations; i++)
        {
            // Выполняем простые арифметические операции
            double dummy = Math.Sqrt(i * 1.0) * Math.Sin(i * 0.01);
            
            // Периодическая проверка отмены (каждые 1000 итераций)
            if (i % 1000 == 0)
            {
                token.ThrowIfCancellationRequested();
            }
        }
        
        // Финальная проверка
        token.ThrowIfCancellationRequested();
    }
    
    private void SimulateOperation(int durationMs, CancellationToken token)
    {
        // Используем Stopwatch для точного измерения времени
        var stopwatch = Stopwatch.StartNew();
        
        while (stopwatch.ElapsedMilliseconds < durationMs)
        {
            // Периодически проверяем токен отмены
            if (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
            
            // Короткая задержка для снижения нагрузки на ЦП
            Thread.Sleep(1);
        }
    }
    
    private void UpdateCellStatus(int cellId, CellStatus newStatus, CellResult result)
    {
        result.FinalStatus = newStatus;
        
        // Потокобезопасное обновление счетчиков статусов
        UpdateStatusCount(newStatus, 1);
        
        // Для предыдущего статуса уменьшаем счетчик (если это не первый статус)
        if (result.FinalStatus != CellStatus.Pending)
        {
            // Находим предыдущий статус из списка доступных статусов
            // В реальном приложении здесь должна быть более сложная логика отслеживания
        }
    }
    
    private void UpdateStatusCount(CellStatus status, int delta)
    {
        _statusCounts.AddOrUpdate(status, delta, (key, oldValue) => oldValue + delta);
    }
    
    private void CalculateStatistics()
    {
        Statistics.CompletedCount = _statusCounts[CellStatus.Completed];
        Statistics.CanceledCount = _statusCounts[CellStatus.Canceled];
        Statistics.FaultedCount = _statusCounts[CellStatus.Faulted];
        Statistics.SuccessfulCharges = _successfulCharges;
        Statistics.SuccessfulCalibrations = _successfulCalibrations;
        Statistics.CanceledChargingWaits = _canceledChargingWaits;
        Statistics.CanceledCalibrationWaits = _canceledCalibrationWaits;
    }
    
    private void LogEvent(string eventType, int cellId, string? details = null)
    {
        lock (_consoleLock)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string threadId = Environment.CurrentManagedThreadId.ToString().PadLeft(3);
            string cellInfo = cellId > 0 ? $"Ячейка {cellId.ToString().PadLeft(3)}" : "СТАНЦИЯ";
            
            string message = $"[{timestamp}] [Поток {threadId}] {cellInfo}: {eventType}";
            if (!string.IsNullOrEmpty(details))
            {
                message += $" - {details}";
            }
            
            Console.WriteLine(message);
        }
    }
    
    public void CancelProcessing()
    {
        if (!_cancellationTokenSource.IsCancellationRequested)
        {
            LogEvent("ЗАПРОШЕНА АВАРИЙНАЯ ОСТАНОВКА", 0);
            _cancellationTokenSource.Cancel();
        }
    }
    
    public void Dispose()
    {
        _cancellationTokenSource.Dispose();
        _chargingSemaphore.Dispose();
        _calibrationMutex.Dispose();
        _countdownEvent.Dispose();
    }
}