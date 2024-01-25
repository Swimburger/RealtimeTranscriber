using NAudio.Wave;
using Microsoft.Extensions.Configuration;
using Lib;

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

await using var transcriber = new RealtimeTranscriber((ApiKey)config["AssemblyAI:ApiKey"]!)
{
    SampleRate = 16_000,
    WordBoost = new[] { "word1", "word2" }
};
transcriber.SessionBegins += (sender, args) => Console.WriteLine($"""
                                                                  Session begins:
                                                                  - Session ID: {args.Result.SessionId}
                                                                  - Expires at: {args.Result.ExpiresAt}
                                                                  """);
transcriber.PartialTranscriptReceived += (_, args) =>
{
    // don't do anything if nothing was said
    if (string.IsNullOrEmpty(args.Result.Text)) return;
    // clear existing output on line
    Console.Write("\r".PadLeft(Console.WindowWidth - Console.CursorLeft - 1));
    Console.Write(args.Result.Text);
};
transcriber.FinalTranscriptReceived += (_, args) =>
{
    // clear existing output on line
    Console.Write("\r".PadLeft(Console.WindowWidth - Console.CursorLeft - 1));
    Console.Write(args.Result.Text);
    Console.WriteLine();
};
transcriber.ErrorReceived += (_, args) => Console.WriteLine("Error: {0}", args.Error);
transcriber.Closed += (_, _) => Console.WriteLine("Closed");

await transcriber.ConnectAsync();

using var waveIn = new WaveInEvent()
{
    WaveFormat = WaveFormat.CreateCustomFormat(
        tag: WaveFormatEncoding.Pcm,
        sampleRate: 16_000,
        channels: 1,
        averageBytesPerSecond: 16_000,
        blockAlign: 2,
        bitsPerSample: 16
    )
};

waveIn.StartRecording();

waveIn.DataAvailable += (s, a) => { transcriber.SendAudioAsync(a.Buffer); };

Console.WriteLine("Press any key to exit.");
Console.ReadKey();

waveIn.StopRecording();
await transcriber.CloseAsync();