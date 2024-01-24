using Microsoft.Extensions.Configuration;
using Lib;

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

var transcriber = new RealtimeTranscriber((ApiKey)config["AssemblyAI:ApiKey"]!)
{
    SampleRate = 16_000,
    WordBoost = new[] { "word1", "word2" }
};
transcriber.SessionBegins += (sender, args) => Console.WriteLine($"""
                                                                  Session begins:
                                                                  - Session ID: {args.Result.SessionId}
                                                                  - Expires at: {args.Result.ExpiresAt}
                                                                  """);
transcriber.PartialTranscriptReceived += (_, args) => Console.WriteLine("Partial transcript: {0}", args.Result.Text);
transcriber.FinalTranscriptReceived += (_, args) => Console.WriteLine("Final transcript: {0}", args.Result.Text);
transcriber.TranscriptReceived += (_, args) => Console.WriteLine("Transcript: {0}", args.Result.Text);
transcriber.ErrorReceived += (_, args) => Console.WriteLine("Error: {0}", args.Error);
transcriber.Closed += (_, _) => Console.WriteLine("Closed");

await transcriber.ConnectAsync();
await SendAudio();
await transcriber.CloseAsync();

// Mock of streaming audio from a microphone
async Task SendAudio()
{
    await using var fileStream = File.OpenRead("./gore-short.wav");
    var audio = new byte[8192 * 2];
    while (fileStream.Read(audio, 0, audio.Length) > 0)
    {
        await transcriber.SendAudio(audio);
        await Task.Delay(300);
    }
}

Console.WriteLine("Press any key to exit.");
Console.ReadKey();