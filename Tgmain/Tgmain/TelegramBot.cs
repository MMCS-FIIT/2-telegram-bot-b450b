using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Tgmain;

public class TelegramBot
{
    private const string BotToken = "8627231316:AAEk-WX2whLRbmh1qVTz5qz2zpcgQA_g0wA";
    private static readonly HttpClient HttpClient = new();
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "bot-log.txt");
    private readonly string[] _agreeWords = { "да", "давай", "ок", "ага", "yes", "ok" };
    private readonly string[] _supported = { "RUB", "USD", "EUR", "GBP" };

    public async Task Run()
    {
        var botClient = new TelegramBotClient(BotToken);
        using CancellationTokenSource cts = new();

        ReceiverOptions receiverOptions = new()
        {
            AllowedUpdates = new[] { UpdateType.Message }
        };

        botClient.StartReceiving(
            updateHandler: OnMessageReceived,
            pollingErrorHandler: OnErrorOccured,
            receiverOptions: receiverOptions,
            cancellationToken: cts.Token
        );

        var me = await botClient.GetMeAsync(cancellationToken: cts.Token);
        Console.WriteLine($"Бот @{me.Username} запущен. Для остановки нажмите Esc.");

        while (Console.ReadKey().Key != ConsoleKey.Escape)
        {
        }

        cts.Cancel();
    }

    private async Task OnMessageReceived(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (message == null)
        {
            return;
        }

        var chatId = message.Chat.Id;

        if (message.Text == null)
        {
            await botClient.SendTextMessageAsync(
                chatId,
                "Я пока работаю только с текстом. Напиши /help",
                cancellationToken: cancellationToken);
            await WriteLog(chatId, "[non-text]");
            return;
        }

        var messageText = message.Text;
        var normalized = messageText.Trim().ToLowerInvariant();
        await WriteLog(chatId, normalized);
        Console.WriteLine($"[{chatId}] {normalized}");

        try
        {
            if (normalized == "/start")
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    "Привет! Я простой бот-обменник.\n" +
                    "Могу показать курс и посчитать конвертацию.\n\n" +
                    "Напиши /help, чтобы увидеть команды.",
                    cancellationToken: cancellationToken);
                return;
            }

            if (normalized == "/help" || _agreeWords.Contains(normalized))
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    "Команды:\n" +
                    "/rates RUB - курс RUB к USD, EUR, GBP\n" +
                    "/rates USD - курс USD к RUB, EUR, GBP\n\n" +
                    "Конвертация:\n" +
                    "100 RUB USD\n" +
                    "100 usd to rub\n\n" +
                    "Поддерживаются: RUB, USD, EUR, GBP",
                    cancellationToken: cancellationToken);
                return;
            }

            if (normalized == "/rates")
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    "Нужно указать валюту. Пример: /rates USD",
                    cancellationToken: cancellationToken);
                return;
            }

            if (normalized.StartsWith("/rates "))
            {
                var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await botClient.SendTextMessageAsync(chatId, "Пример: /rates USD", cancellationToken: cancellationToken);
                    return;
                }

                var from = parts[1].ToUpperInvariant();
                var ratesText = await GetRates(from, cancellationToken);
                await botClient.SendTextMessageAsync(chatId, ratesText, cancellationToken: cancellationToken);
                return;
            }

            var converted = await TryConvert(normalized, cancellationToken);
            if (converted is not null)
            {
                await botClient.SendTextMessageAsync(chatId, converted, cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendTextMessageAsync(
                chatId,
                "Не понял запрос. Напиши /help.\nПример: 120 RUB USD",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteLog(chatId, $"ERROR: {ex.Message}");
            await botClient.SendTextMessageAsync(
                chatId,
                "Упс, произошла ошибка. Попробуйте позже.",
                cancellationToken: cancellationToken);
        }
    }

    private async Task<string> GetRates(string fromCurrency, CancellationToken cancellationToken)
    {
        if (!IsCurrencyCode(fromCurrency))
        {
            return "Код валюты должен быть из 3 букв. Пример: /rates RUB";
        }

        if (!_supported.Contains(fromCurrency))
        {
            return "Эта валюта не поддерживается. Доступны: RUB, USD, EUR, GBP";
        }

        var usdRates = await GetUsdRates(cancellationToken);
        if (usdRates == null)
        {
            return "Не получилось получить курсы от внешнего API.";
        }

        if (!usdRates.ContainsKey(fromCurrency))
        {
            return "Не нашел курс для выбранной валюты.";
        }

        var fromRate = usdRates[fromCurrency];
        var lines = new List<string> { $"Курсы для {fromCurrency}:" };
        foreach (var target in _supported)
        {
            if (target == fromCurrency)
            {
                continue;
            }

            if (!usdRates.ContainsKey(target))
            {
                continue;
            }

            var targetRate = usdRates[target];
            var crossRate = targetRate / fromRate;
            lines.Add($"{target}: {crossRate:0.####}");
        }

        return string.Join('\n', lines);
    }

    private async Task<string?> TryConvert(string input, CancellationToken cancellationToken)
    {
        // Простой парсинг формата:
        // 100 USD RUB
        // 100 USD to RUB
        var match = Regex.Match(
            input,
            @"^\s*(\d+(?:[.,]\d+)?)\s+([a-zA-Z]{3})(?:\s+to)?\s+([a-zA-Z]{3})\s*$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        var amountText = match.Groups[1].Value.Replace(',', '.');
        if (!decimal.TryParse(amountText, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
        {
            return "Сумма введена неверно. Пример: 100 RUB USD";
        }

        var from = match.Groups[2].Value.ToUpperInvariant();
        var to = match.Groups[3].Value.ToUpperInvariant();

        if (!_supported.Contains(from) || !_supported.Contains(to))
        {
            return "Поддерживаются только RUB, USD, EUR, GBP";
        }

        var usdRates = await GetUsdRates(cancellationToken);
        if (usdRates == null)
        {
            return "Не получилось выполнить конвертацию.";
        }

        if (!usdRates.ContainsKey(from) || !usdRates.ContainsKey(to))
        {
            return "Не нашел курс для одной из валют.";
        }

        var fromRate = usdRates[from];
        var toRate = usdRates[to];
        if (fromRate == 0)
        {
            return "Ошибка курса исходной валюты.";
        }

        var result = amount * (toRate / fromRate);
        return $"{amount:0.##} {from} = {result:0.####} {to}";
    }

    private async Task<Dictionary<string, decimal>?> GetUsdRates(CancellationToken cancellationToken)
    {
        var url = "https://open.er-api.com/v6/latest/USD";
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("rates", out var ratesElement))
        {
            return null;
        }

        var map = new Dictionary<string, decimal>();
        foreach (var rate in ratesElement.EnumerateObject())
        {
            if (rate.Value.TryGetDecimal(out var decimalValue))
            {
                map[rate.Name] = decimalValue;
                continue;
            }

            if (rate.Value.TryGetDouble(out var doubleValue))
            {
                map[rate.Name] = (decimal)doubleValue;
            }
        }

        return map;
    }

    private static bool IsCurrencyCode(string value)
    {
        return Regex.IsMatch(value, @"^[A-Z]{3}$");
    }

    private async Task WriteLog(long chatId, string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | chat:{chatId} | {text}{Environment.NewLine}";
        await System.IO.File.AppendAllTextAsync(_logPath, line);
    }

    private Task OnErrorOccured(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        string errorMessage;
        if (exception is ApiRequestException apiRequestException)
        {
            errorMessage = $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}";
        }
        else
        {
            errorMessage = exception.ToString();
        }

        Console.WriteLine(errorMessage);
        return Task.CompletedTask;
    }
}
