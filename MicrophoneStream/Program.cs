using System.Runtime.InteropServices;
using Lib;
using Microsoft.Extensions.Configuration;
using NAudio.Wave;

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    throw new Exception("Recording microphone with NAudio is only supported on Windows.");
}

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

const int sampleRate = 16_000;
await using var transcriber = new RealtimeTranscriber
{
    ApiKey = config["AssemblyAI:ApiKey"]!,
    SampleRate = sampleRate
};
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
transcriber.ErrorReceived += (_, args) => Console.WriteLine("Real-time error: {0}", args.Error);
transcriber.Closed += (_, args) => Console.WriteLine("Real-time connection closed: {0} - {1}", args.Code, args.Reason);

Console.WriteLine("Connecting to real-time transcript service");
var sessionBeginsMessage = await transcriber.ConnectAsync();
Console.WriteLine($"""
                   Session begins:
                   - Session ID: {sessionBeginsMessage.SessionId}
                   - Expires at: {sessionBeginsMessage.ExpiresAt}
                   """);

// Create a pcm_s16le format with a sample rate of 16 kHz.
var pcmFormat = WaveFormat.CreateCustomFormat(
    tag: WaveFormatEncoding.Pcm,
    sampleRate: sampleRate,
    channels: 1,
    averageBytesPerSecond: 16_000,
    blockAlign: 2,
    bitsPerSample: 16
);

Console.WriteLine("Starting recording");
using var waveIn = new WaveInEvent { WaveFormat = pcmFormat };
waveIn.StartRecording();

waveIn.DataAvailable += (s, a) => { transcriber.SendAudioAsync(a.Buffer); };

Console.WriteLine("Press any key to exit.");
Console.ReadKey();

Console.WriteLine("Stopping recording");
waveIn.StopRecording();

Console.WriteLine("Closing real-time transcript connection");
await transcriber.CloseAsync();