using System.Diagnostics;

namespace BatteryTestStation;

internal class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== СТАНЦИЯ ИСПЫТАНИЯ АККУМУЛЯТОРНЫХ ЯЧЕЕК ===");
        Console.WriteLine("Техническое задание: Системное программирование");
        Console.WriteLine(".NET 8.0 Консольное приложение\n");
        
        // Парсинг аргументов командной строки
        var config = ParseCommandLineArgs(args);
        PrintConfiguration(config);
        
        BatteryTestStation? station = null;
        
        // Настройка обработки Ctrl+C
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // Предотвращаем немедленное завершение процесса
            Console.WriteLine("\n[ПРЕРЫВАНИЕ] Обнаружено нажатие Ctrl+C");
            
            if (station != null)
            {
                station.CancelProcessing();
            }
        };
        
        try
        {
            // Создание и запуск станции
            station = new BatteryTestStation(config);
            
            Console.WriteLine("\nНажмите Ctrl+C для аварийной остановки...\n");
            
            // Запуск обработки
            station.StartProcessing();
            
            // Ожидание завершения
            station.WaitForCompletion();
            
            // Вывод итоговой статистики
            station.Statistics.PrintStatistics();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nКРИТИЧЕСКАЯ ОШИБКА: {ex.Message}");
            Debug.WriteLine($"StackTrace: {ex.StackTrace}");
        }
        finally
        {
            station?.Dispose();
        }
        
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
        {
            Console.WriteLine("\nНажмите любую клавишу для выхода...");
            Console.ReadKey();
        }
    }
    
    private static StationConfiguration ParseCommandLineArgs(string[] args)
    {
        var config = StationConfiguration.Default;
        
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "-b":
                case "--batch":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int batchSize))
                    {
                        config.BatchSize = batchSize;
                        i++;
                    }
                    break;
                    
                case "-s":
                case "--slots":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int slots))
                    {
                        config.ChargingSlots = slots;
                        i++;
                    }
                    break;
                    
                case "-d":
                case "--diagnostics":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int iterations))
                    {
                        config.DiagnosticIterationsPerCell = iterations;
                        i++;
                    }
                    break;
                    
                case "-t":
                case "--timeout":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int timeout))
                    {
                        config.AutoCancelTimeoutMs = timeout;
                        i++;
                    }
                    break;
                    
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }
        
        return config;
    }
    
    private static void PrintConfiguration(StationConfiguration config)
    {
        Console.WriteLine("=== КОНФИГУРАЦИЯ СТАНЦИИ ===");
        Console.WriteLine($"Количество ячеек в партии: {config.BatchSize}");
        Console.WriteLine($"Количество слотов зарядки: {config.ChargingSlots}");
        Console.WriteLine($"Диагностических итераций на ячейку: {config.DiagnosticIterationsPerCell}");
        Console.WriteLine($"Диапазон длительности зарядки: {config.ChargeDurationMinMs}-{config.ChargeDurationMaxMs} мс");
        Console.WriteLine($"Диапазон длительности калибровки: {config.CalibrationDurationMinMs}-{config.CalibrationDurationMaxMs} мс");
        
        if (config.AutoCancelTimeoutMs.HasValue)
        {
            Console.WriteLine($"Автоматическая отмена через: {config.AutoCancelTimeoutMs.Value} мс");
        }
        else
        {
            Console.WriteLine("Автоматическая отмена: выключена");
        }
    }
    
    private static void PrintHelp()
    {
        Console.WriteLine("\nИспользование: BatteryTestStation [опции]");
        Console.WriteLine("\nОпции:");
        Console.WriteLine("  -b, --batch <число>       Количество ячеек в партии (по умолчанию: 20)");
        Console.WriteLine("  -s, --slots <число>       Количество слотов зарядки (по умолчанию: 4)");
        Console.WriteLine("  -d, --diagnostics <число> Диагностических итераций на ячейку (по умолчанию: 100000)");
        Console.WriteLine("  -t, --timeout <мс>        Автоматическая отмена через указанное время в мс");
        Console.WriteLine("  -h, --help                Показать эту справку");
        Console.WriteLine("\nПримеры:");
        Console.WriteLine("  BatteryTestStation -b 30 -s 5 -t 5000");
        Console.WriteLine("  BatteryTestStation --batch 15 --slots 3");
    }
}