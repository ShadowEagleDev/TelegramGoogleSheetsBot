using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Polly;
using Polly.Retry;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public static class Program
{
    static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                if (context.HostingEnvironment.IsDevelopment())
                {
                    config.AddUserSecrets<TelegramBotWorker>();
                }
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.AddSingleton(hostContext.Configuration);

                services.AddSingleton<GoogleSheetsHelper>(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var logger = sp.GetRequiredService<ILogger<GoogleSheetsHelper>>();
                    var spreadsheetId = config["BotConfiguration:SpreadsheetId"] ?? throw new ArgumentNullException("SpreadsheetId not configured.");
                    var credentialsPath = config["Google:CredentialsPath"] ?? throw new ArgumentNullException("Google:CredentialsPath not configured.");

                    return new GoogleSheetsHelper(
                        credentialsPath,
                        "TelegramSupportBot",
                        spreadsheetId,
                        logger
                    );
                });

                services.AddSingleton<ITelegramBotClient>(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var botToken = config["BotConfiguration:BotToken"] ?? throw new ArgumentNullException("BotToken not configured.");
                    return new TelegramBotClient(botToken);
                });

                services.AddHostedService<TelegramBotWorker>();
            })
            .Build();

        await host.RunAsync();
    }
}

/// <summary>
/// Helper для записи кейсов в Google Sheets.
/// Обёрнут в retry-политику, содержит логику подготовки и безопасной записи строки.
/// </summary>
public class GoogleSheetsHelper
{
    private const string SheetName = "Лист1";
    private const int MaxDataRow = 199;
    private readonly string _spreadsheetId;
    private readonly SheetsService _sheetsService;
    private readonly ILogger<GoogleSheetsHelper> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;

    // Простая атомарная последовательность для генерации читабельных ID (в формате MMddHHmm-XX).
    private static int _atomicSeq = 0;

    private static string GenerateReadableCaseId_Atomic()
    {
        var minuteKey = DateTime.UtcNow.AddHours(3).ToString("MMddHHmm");
        var next = Interlocked.Increment(ref _atomicSeq);
        var seq = ((next - 1) % 99) + 1;
        return $"{minuteKey}-{seq:00}";
    }

    public GoogleSheetsHelper(string credentialsPath, string applicationName, string spreadsheetId, ILogger<GoogleSheetsHelper> logger)
    {
        _spreadsheetId = spreadsheetId;
        _logger = logger;

        try
        {
            GoogleCredential credential = GoogleCredential.FromFile(credentialsPath)
                .CreateScoped(SheetsService.Scope.Spreadsheets);

            _sheetsService = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = applicationName,
            });
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Критическая ошибка при инициализации Google Sheets. Убедитесь, что файл по пути '{CredentialsPath}' существует и доступен.", credentialsPath);
            throw;
        }

        // Retry для HTTP/Google API ошибок и типичных сетевых таймаутов
        _retryPolicy = Policy
            .Handle<Google.GoogleApiException>(ex => ex.Error.Code == 429 || ex.Error.Code >= 500)
            .Or<Exception>(ex => ex is TimeoutException || ex is System.Net.Http.HttpRequestException)
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning(exception, "Ошибка при обращении к Google Sheets API. Попытка {RetryCount}. Задержка {TimeSpan}...", retryCount, timeSpan);
                });
    }

    /// <summary>
    /// Формирует строку значений и записывает её в первую пустую строку листа.
    /// Возвращает сгенерированный ID кейса.
    /// </summary>
    public async Task<string> AppendRowAsync(string timestamp, string operatorName, string customerInfo, string problemText, string status)
    {
        var newCaseId = GenerateReadableCaseId_Atomic();

        var values = new List<object?>
        {
            SanitizeForSheets(newCaseId),    // A
            SanitizeForSheets(timestamp),    // B
            null,                            // C
            null,                            // D
            SanitizeForSheets(operatorName), // E
            SanitizeForSheets(customerInfo), // F
            SanitizeForSheets(problemText),  // G
            SanitizeForSheets(status),       // H
            false,                           // I
            false                            // J
        };

        await _retryPolicy.ExecuteAsync(async () =>
        {
            var targetRow = await FindFirstEmptyRowAsync();
            var range = $"{SheetName}!A{targetRow}";
            _logger.LogInformation("Найдена пустая строка {TargetRow}. Попытка записи кейса {CaseId} в диапазон {Range}", targetRow, newCaseId, range);

            var valueRange = new ValueRange { Values = new List<IList<object>> { values! } };
            var updateRequest = _sheetsService.Spreadsheets.Values.Update(valueRange, _spreadsheetId, range);
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;

            await updateRequest.ExecuteAsync();
        });

        _logger.LogInformation("Строка для кейса {CaseId} успешно добавлена.", newCaseId);
        return newCaseId;
    }

    /// <summary>
    /// Возвращает индекс первой пустой строки в столбце A (1-based).
    /// Бросает исключение, если все строки до MaxDataRow заняты.
    /// </summary>
    private async Task<int> FindFirstEmptyRowAsync()
    {
        var range = $"{SheetName}!A1:A{MaxDataRow}";
        var request = _sheetsService.Spreadsheets.Values.Get(_spreadsheetId, range);
        ValueRange response = await request.ExecuteAsync();

        if (response.Values == null || response.Values.Count == 0)
        {
            return 1;
        }

        for (int i = 0; i < response.Values.Count; i++)
        {
            if (response.Values[i] == null || response.Values[i].Count == 0 || string.IsNullOrEmpty(response.Values[i][0]?.ToString()))
            {
                return i + 1;
            }
        }

        if (response.Values.Count < MaxDataRow)
        {
            return response.Values.Count + 1;
        }

        _logger.LogError("Все строки до {MaxDataRow} в Google Sheets заняты. Невозможно добавить новую запись.", MaxDataRow);
        throw new Exception($"Не удалось найти пустую строку для записи. Таблица заполнена до строки {MaxDataRow}.");
    }

    /// <summary>
    /// Простая санитизация для предотвращения интерпретации формул в Google Sheets.
    /// </summary>
    private static string SanitizeForSheets(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        if (input.StartsWith("=") || input.StartsWith("+") || input.StartsWith("-") || input.StartsWith("@"))
        {
            return "'" + input;
        }
        return input;
    }
}

/// <summary>
/// Хранит очередь сообщений для одного чата, токен группировки (для debounce) и состояние.
/// Потокобезопасен: очередь — ConcurrentQueue, токены защищены блокировкой при сбросе.
/// </summary>
public class PendingCase : IDisposable
{
    public ConcurrentQueue<Message> Messages { get; } = new ConcurrentQueue<Message>();
    public DateTime CreationTime { get; } = DateTime.UtcNow;
    public CancellationTokenSource GroupingCts { get; set; } = new CancellationTokenSource();
    public bool IsAwaitingOperatorName { get; set; } = false;
    public bool IsManualCreation { get; init; }

    private readonly object _ctsLock = new object();
    private bool _disposed = false;

    /// <summary>
    /// Отменяет текущую задержку группировки сообщений и выставляет новый токен.
    /// </summary>
    public void ResetGroupingToken()
    {
        lock (_ctsLock)
        {
            if (_disposed) return;
            try
            {
                GroupingCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // игнорируем — уже диспознуто
            }
            GroupingCts = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// Освобождает токен и помечает объект как уничтоженный.
    /// </summary>
    public void Dispose()
    {
        lock (_ctsLock)
        {
            if (_disposed) return;

            try
            {
                if (!GroupingCts.IsCancellationRequested)
                {
                    GroupingCts.Cancel();
                }
            }
            catch (ObjectDisposedException) { }

            try
            {
                GroupingCts.Dispose();
            }
            catch { }

            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Основной воркер бота — принимает обновления, группирует пересланные/ручные сообщения,
/// запрашивает у оператора имя и записывает кейс в Google Sheets.
/// </summary>
public class TelegramBotWorker : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TelegramBotWorker> _logger;
    private readonly GoogleSheetsHelper _sheetsHelper;
    private readonly ConcurrentDictionary<long, PendingCase> _pendingCases = new ConcurrentDictionary<long, PendingCase>();

    private const int MAX_PENDING_CASES = 1000;
    private static readonly TimeSpan PENDING_CASE_TTL = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MESSAGE_GROUPING_DELAY = TimeSpan.FromSeconds(5);

    public TelegramBotWorker(ITelegramBotClient botClient, ILogger<TelegramBotWorker> logger, GoogleSheetsHelper sheetsHelper)
    {
        _botClient = botClient;
        _logger = logger;
        _sheetsHelper = sheetsHelper;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _botClient.GetMe(stoppingToken);
        _logger.LogInformation("Бот @{BotUsername} запущен.", me.Username);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }
        };

        _botClient.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync, receiverOptions, stoppingToken);

        // Запускаем фоновую задачу очистки устаревших кейсов
        _ = CleanupExpiredCasesAsync(stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            if (update.Type == UpdateType.Message && update.Message is { } message)
            {
                await HandleMessageAsync(botClient, message, cancellationToken);
            }
            else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } callbackQuery)
            {
                await HandleCallbackQueryAsync(botClient, callbackQuery, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Произошла ошибка при обработке обновления {UpdateId}", update.Id);
        }
    }

    /// <summary>
    /// Обрабатывает входящее сообщение:
    /// - если сообщение не переслано, предлагает создать кейс вручную;
    /// - если переслано или кейс уже существует — группирует сообщения (debounce) и запрашивает имя оператора.
    /// </summary>
    private async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        // Если уже ожидаем имя оператора — обрабатываем вход как имя.
        if (_pendingCases.TryGetValue(chatId, out var existingCase) && existingCase.IsAwaitingOperatorName)
        {
            await ProcessCaseRegistration(botClient, message, existingCase, cancellationToken);
            return;
        }

        // Новое ручное создание (сообщение не переслано и кейса ещё нет)
        if (message.ForwardFrom == null && !_pendingCases.ContainsKey(chatId))
        {
            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                new []
                {
                    InlineKeyboardButton.WithCallbackData("Да, создать", "manual_case_yes"),
                    InlineKeyboardButton.WithCallbackData("Нет, отмена", "manual_case_no"),
                }
            });

            var manualCase = new PendingCase { IsManualCreation = true };
            manualCase.Messages.Enqueue(message);
            _pendingCases[chatId] = manualCase;

            await botClient.SendMessage(
                chatId: chatId,
                text: "Сообщение не является пересланным. Создать кейс вручную?",
                replyMarkup: inlineKeyboard,
                cancellationToken: cancellationToken);
            return;
        }

        // Сообщение переслано или кейс уже существует — добавляем в очередь и перезапускаем debounce.
        if (message.ForwardFrom != null || _pendingCases.ContainsKey(chatId))
        {
            if (_pendingCases.Count >= MAX_PENDING_CASES && !_pendingCases.ContainsKey(chatId))
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: "Бот в данный момент перегружен. Пожалуйста, попробуйте позже.",
                    cancellationToken: cancellationToken);
                return;
            }

            var pendingCase = _pendingCases.GetOrAdd(chatId, _ => new PendingCase { IsManualCreation = false });

            pendingCase.ResetGroupingToken();
            pendingCase.Messages.Enqueue(message);

            var groupingToken = pendingCase.GroupingCts.Token;

            // Debounce: ждём короткую паузу, затем переводим кейс в состояние ожидания имени оператора.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(MESSAGE_GROUPING_DELAY, groupingToken);
                    if (groupingToken.IsCancellationRequested) return;

                    if (_pendingCases.TryGetValue(chatId, out var finalCase))
                    {
                        finalCase.IsAwaitingOperatorName = true;

                        // Снимок очереди для подсчёта и сборки текста
                        var snapshot = finalCase.Messages.ToArray();
                        var messageCount = snapshot.Length;

                        var text = messageCount > 1
                            ? $"Принято {messageCount} сообщений. Они будут объединены.\n\nТеперь, пожалуйста, отправьте ваше имя."
                            : "Сообщение принято. Теперь, пожалуйста, отправьте ваше имя.";

                        await botClient.SendMessage(
                            chatId: chatId,
                            text: text,
                            cancellationToken: cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ожидаемо: группировка была перезапущена или отменена
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка в задаче группировки сообщений для чата {ChatId}", chatId);
                }
            }, cancellationToken);
        }
    }

    /// <summary>
    /// Обработка нажатий inline-кнопок при ручном создании кейса.
    /// </summary>
    private async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var chatId = callbackQuery.Message!.Chat.Id;

        await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);

        if (!_pendingCases.TryGetValue(chatId, out var pendingCase) || !pendingCase.IsManualCreation)
        {
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Это действие уже неактуально.", cancellationToken: cancellationToken);
            return;
        }

        if (callbackQuery.Data == "manual_case_yes")
        {
            pendingCase.IsAwaitingOperatorName = true;
            await botClient.SendMessage(
                chatId: chatId,
                text: "Понял. Теперь, пожалуйста, отправьте ваше имя для регистрации кейса.",
                cancellationToken: cancellationToken);
        }
        else if (callbackQuery.Data == "manual_case_no")
        {
            if (_pendingCases.TryRemove(chatId, out var removedCase))
            {
                removedCase.Dispose();
            }
            await botClient.SendMessage(
                chatId: chatId,
                text: "Действие отменено.",
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Принимает сообщение с именем оператора, собирает текст кейса и записывает в Google Sheets.
    /// В конце — корректно освобождает ресурсы PendingCase.
    /// </summary>
    private async Task ProcessCaseRegistration(ITelegramBotClient botClient, Message operatorMessage, PendingCase caseToProcess, CancellationToken cancellationToken)
    {
        var chatId = operatorMessage.Chat.Id;
        var operatorName = operatorMessage.Text;
        if (string.IsNullOrWhiteSpace(operatorName))
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "❌ Пожалуйста, отправьте ваше имя в виде текста.",
                cancellationToken: cancellationToken);

            // Возвращаем кейс в словарь (если он был удалён ранее) и не диспозим.
            _pendingCases.TryAdd(chatId, caseToProcess);
            return;
        }

        _logger.LogInformation("Оператор {OperatorName} регистрирует кейс из {MessageCount} сообщений.", operatorName, caseToProcess.Messages.Count);

        // Получаем упорядоченный снимок сообщений
        var messagesSnapshot = caseToProcess.Messages.ToArray();
        var firstMessage = messagesSnapshot.First();

        string customerInfo = caseToProcess.IsManualCreation || firstMessage.ForwardFrom == null
            ? "Создан вручную"
            : $"{firstMessage.ForwardFrom.FirstName} {firstMessage.ForwardFrom.LastName} (@{firstMessage.ForwardFrom.Username})";

        string problemText = string.Join("\n---\n", messagesSnapshot
            .Select(m => m.Text ?? m.Caption ?? "Сообщение не содержит текста (возможно, медиа).")
            .Where(t => !string.IsNullOrEmpty(t)));

        if (string.IsNullOrWhiteSpace(problemText))
        {
            problemText = "Сообщения не содержат текста.";
        }

        var timestamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

        try
        {
            var caseId = await _sheetsHelper.AppendRowAsync(timestamp, operatorName, customerInfo, problemText, "Новый");

            var successMessage =
                $"✅ Кейс зарегистрирован\\!\n\n" +
                $"*ID Кейса:* `{caseId}`\n" +
                $"*Оператор:* `{EscapeMarkdownV2(operatorName)}`\n" +
                $"*Сообщений в кейсе:* `{caseToProcess.Messages.Count}`";

            await botClient.SendMessage(
                chatId: chatId,
                text: successMessage,
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: cancellationToken);

            // После успешной записи освобождаем ресурсы кейса
            caseToProcess.Dispose();
            _pendingCases.TryRemove(chatId, out _);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критическая ошибка при записи кейса в Google таблицу для оператора {OperatorName}", operatorName);
            await botClient.SendMessage(
                chatId: chatId,
                text: "❌ Произошла критическая ошибка при записи в Google таблицу. Проверьте логи сервера.",
                cancellationToken: cancellationToken);

            // В случае ошибки тоже освобождаем ресурсы, чтобы не было утечек
            caseToProcess.Dispose();
            _pendingCases.TryRemove(chatId, out _);
        }
    }

    /// <summary>
    /// Периодически удаляет устаревшие (TTL) PendingCase из памяти.
    /// </summary>
    private async Task CleanupExpiredCasesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);

            var expiredKeys = _pendingCases
                .Where(p => (DateTime.UtcNow - p.Value.CreationTime) > PENDING_CASE_TTL)
                .Select(p => p.Key)
                .ToList();

            if (expiredKeys.Any())
            {
                _logger.LogInformation("Очистка: найдено {ExpiredCount} устаревших кейсов. Удаляю...", expiredKeys.Count);
                foreach (var key in expiredKeys)
                {
                    if (_pendingCases.TryRemove(key, out var removedCase))
                    {
                        removedCase.Dispose();
                    }
                }
            }
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiEx => $"Ошибка Telegram API: [{apiEx.ErrorCode}] {apiEx.Message}",
            _ => exception.ToString()
        };

        _logger.LogError(exception, "Ошибка при получении обновлений от Telegram: {ErrorMessage}", errorMessage);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Экранирует текст для MarkdownV2 (Telegram). Возвращает пустую строку для null/пустого входа.
    /// </summary>
    private static string EscapeMarkdownV2(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text);
        var specialChars = new[] { "_", "*", "[", "]", "(", ")", "~", "`", ">", "#", "+", "-", "=", "|", "{", "}", ".", "!", "\\" };

        foreach (var ch in specialChars)
        {
            sb.Replace(ch, "\\" + ch);
        }
        return sb.ToString();
    }
}