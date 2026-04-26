namespace Tgmain;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var bot = new TelegramBot();
        await bot.Run();
    }
}
