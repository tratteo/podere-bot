using PodereBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/start", Description = "❓")]
internal class StartCommand(ILogger<StartCommand> logger, IConfiguration configuration, UserDirectory users)
    : Command(logger, configuration, users)
{
    protected override async Task ExecuteInternal()
    {
        await Arguments.Client.SendMessage(
            Arguments.Message.Chat.Id,
            $"""
            Per i comandi usa il pannello accanto alla tastiera 🐈
            
            <b>📖 Regole</b>
            <blockquote>1. Se entri dal cancello automatico, aspetta che si chiuda prima di proseguire (controlla che non escano i cani)</blockquote>
            <blockquote>2. I cani si spostano, basta muoversi in maniera costante e lineare, no accelerazioni o frenate brusche</blockquote>
            <blockquote>3. Chiudi sempre il cancello pedonale</blockquote>
            <blockquote>4. Se non c'è troppo fango, lascia la macchina nel parco di fronte alla legnaia</blockquote>
            """,
            parseMode: ParseMode.Html,
            disableNotification: true
        );
    }
}
