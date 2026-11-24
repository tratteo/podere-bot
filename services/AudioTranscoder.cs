using System.ComponentModel;
using System.Diagnostics;

namespace PodereBot.Services;

/// <summary>
/// Converts Telegram audio (OGG/Opus voice notes, MP3 audio files) to MP3.
/// Microsoft.Extensions.AI only forwards <c>audio/mpeg</c> and <c>audio/wav</c> to the chat
/// endpoint: every other media type is dropped from the request without an error, so a failed
/// conversion would silently turn a voice message into text-only.
/// </summary>
internal sealed class AudioTranscoder(ILogger<AudioTranscoder> logger)
{
    private static readonly string[] Arguments =
    [
        "-hide_banner",
        "-loglevel",
        "error",
        "-i",
        "pipe:0",
        "-vn",
        "-ac",
        "1",
        "-ar",
        "24000",
        "-b:a",
        "48k",
        "-f",
        "mp3",
        "pipe:1",
    ];

    public async Task<byte[]> ToMp3Async(byte[] input, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));

        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in Arguments)
            startInfo.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg did not start");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("ffmpeg is not installed", ex);
        }

        using (process)
        {
            try
            {
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                using var output = new MemoryStream();
                var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
                await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token);
                process.StandardInput.Close();
                await copy;
                await process.WaitForExitAsync(timeout.Token);
                var errorText = await error;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"ffmpeg exited with {process.ExitCode}: {errorText.Trim()}");

                logger.LogDebug("converted {input} bytes of audio into {output} bytes of mp3", input.Length, output.Length);
                return output.ToArray();
            }
            catch
            {
                // A conversion that timed out or broke leaves ffmpeg reading from a closed pipe.
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) { }
                }
                throw;
            }
        }
    }
}
