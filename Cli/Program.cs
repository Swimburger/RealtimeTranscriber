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
transcriber.SessionBegins += async (object sender, EventArgs args) => Console.WriteLine("Session begins");
transcriber.PartialTranscriptReceived += async (object sender, EventArgs args) => Console.WriteLine("Partial transcript");
transcriber.FinalTranscriptReceived += async (object sender, EventArgs args) => Console.WriteLine("Final transcript");
transcriber.TranscriptReceived += async (object sender, EventArgs args) => Console.WriteLine("Transcript");
transcriber.ErrorReceived += async (object sender, EventArgs args) => Console.WriteLine("Error");
transcriber.Closed += async (object sender, EventArgs args) => Console.WriteLine("Close");

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