using Microsoft.Extensions.Configuration;
using Lib;

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

var transcriber = new RealtimeTranscriber(config["AssemblyAI:ApiKey"]!)
{
    SampleRate = 16_000,
    WordBoost = new[] { "word1", "word2" }
};
transcriber.SessionBegins
    += async (sender, args) => Console.WriteLine($"""
                                                  Session begins:
                                                  - Session ID: {args.SessionId}
                                                  - Expires at: {args.ExpiresAt}
                                                  """);
transcriber.PartialTranscriptReceived +=
    async (sender, args) => Console.WriteLine("Partial transcript: {0}", args.Transcript.Text);
transcriber.FinalTranscriptReceived += 
    async (sender, args) => Console.WriteLine("Final transcript: {0}", args.Transcript.Text);
transcriber.TranscriptReceived += 
    async (sender, args) => Console.WriteLine("Transcript: {0}", args.Transcript.Text);
transcriber.ErrorReceived += 
    async (sender, args) => Console.WriteLine("Error: {0}", args.Error);
transcriber.Closed += 
    async (sender, args) => Console.WriteLine("Closed");

await transcriber.ConnectAsync();

await SendAudio();

await transcriber.Close();

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