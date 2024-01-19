using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lib
{
    public delegate Task SessionBeginsEventHandler(object sender, EventArgs evt);

    public delegate Task PartialTranscriptEventHandler(object sender, EventArgs evt);

    public delegate Task FinalTranscriptEventHandler(object sender, EventArgs evt);

    public delegate Task TranscriptEventHandler(object sender, EventArgs evt);

    public delegate Task ErrorEventHandler(object sender, EventArgs evt);

    public delegate Task ClosedEventHandler(object sender, EventArgs evt);

    public class RealtimeTranscriber : IAsyncDisposable, IDisposable
    {
        private readonly string _authorization;
        private readonly RealtimeCredentialType _credentialType;
        private const string RealtimeServiceEndpoint = "wss://api.assemblyai.com/v2/realtime/ws";
        private readonly ClientWebSocket _socket;
        private TaskCompletionSource<bool> _sessionTerminatedTaskCompletionSource;
        public uint SampleRate { get; set; } = 0;
        public IEnumerable<string> WordBoost { get; set; } = Enumerable.Empty<string>();
        public event SessionBeginsEventHandler SessionBegins;
        public event PartialTranscriptEventHandler PartialTranscriptReceived;
        public event FinalTranscriptEventHandler FinalTranscriptReceived;
        public event TranscriptEventHandler TranscriptReceived;
        public event ErrorEventHandler ErrorReceived;
        public event ClosedEventHandler Closed;

        public RealtimeTranscriber(string authorization) : this(authorization, RealtimeCredentialType.ApiKey)
        {
        }

        public RealtimeTranscriber(string authorization, RealtimeCredentialType credentialType)
        {
            _authorization = authorization;
            _credentialType = credentialType;
            _socket = new ClientWebSocket();
        }

        public Task ConnectAsync() => ConnectAsync(CancellationToken.None);

        public async Task ConnectAsync(CancellationToken ct)
        {
            var urlBuilder = new StringBuilder(RealtimeServiceEndpoint);
            var queryPrefix = "?";
            if (SampleRate != 0)
            {
                urlBuilder.AppendFormat("?sample_rate={0}", SampleRate);
                queryPrefix = "&";
            }

            if (WordBoost.Any())
            {
                urlBuilder.AppendFormat("{0}word_boost={1}", queryPrefix, JsonSerializer.Serialize(WordBoost));
                queryPrefix = "&";
            }

            if (_credentialType == RealtimeCredentialType.Token)
            {
                urlBuilder.AppendFormat("{0}token={1}", queryPrefix, _authorization);
            }
            else
            {
                _socket.Options.SetRequestHeader("Authorization", _authorization);
            }

            await _socket.ConnectAsync(new Uri(urlBuilder.ToString()), ct).ConfigureAwait(false);
            Task.Run(async () => await ListenAsync(ct).ConfigureAwait(false), ct);
        }

        private Task ListenAsync() => ListenAsync(CancellationToken.None);

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var buffer = new ArraySegment<byte>(new byte[2048]);
                WebSocketReceiveResult result;

                using var ms = new MemoryStream();
                do
                {
                    result = await _socket.ReceiveAsync(buffer, ct)
                        .ConfigureAwait(false);
                    ms.Write(buffer.Array!, buffer.Offset, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await OnClosed(result)
                        .ConfigureAwait(false);
                    return;
                }

                ms.Seek(0, SeekOrigin.Begin);

                var message = await JsonSerializer.DeserializeAsync<JsonDocument>(ms, (JsonSerializerOptions)null, ct)
                    .ConfigureAwait(false);
                if (message.RootElement.TryGetProperty("error", out var errorProperty))
                {
                    var error = errorProperty.GetString();
                    await OnErrorReceived(error);
                }
                
                // Console.WriteLine(JsonSerializer.Serialize(message));
                if (message.RootElement.TryGetProperty("message_type", out var messageTypeProperty))
                {
                    var messageType = messageTypeProperty.GetString();
                    switch (messageType)
                    {
                        case "SessionBegins":
                            await OnSessionBegins(message)
                                .ConfigureAwait(false);
                            break;
                        case "PartialTranscript":
                            await OnPartialTranscriptReceived(message)
                                .ConfigureAwait(false);
                            await OnTranscriptReceived(message)
                                .ConfigureAwait(false);
                            break;
                        case "FinalTranscript":
                            await OnFinalTranscriptReceived(message)
                                .ConfigureAwait(false);
                            await OnTranscriptReceived(message)
                                .ConfigureAwait(false);
                            break;
                        case "SessionTerminated":
                            OnSessionTerminated(message);
                            break;
                    }
                }
            }

            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            else if (_socket.State != WebSocketState.Open) throw new Exception("WebSocket is not open.");
            throw new Exception();
        }

        private async Task OnSessionBegins(JsonDocument message)
        {
            if (SessionBegins != null)
                await SessionBegins.Invoke(this, null);
        }

        private async Task OnPartialTranscriptReceived(JsonDocument message)
        {
            if (PartialTranscriptReceived != null)
                await PartialTranscriptReceived.Invoke(this, null);
        }

        private async Task OnFinalTranscriptReceived(JsonDocument message)
        {
            if (FinalTranscriptReceived != null)
                await FinalTranscriptReceived.Invoke(this, null);
        }

        private async Task OnTranscriptReceived(JsonDocument message)
        {
            if (TranscriptReceived != null)
                await TranscriptReceived.Invoke(this, null);
        }

        private void OnSessionTerminated(JsonDocument message)
        {
            _sessionTerminatedTaskCompletionSource?.TrySetResult(true);
        }

        private async Task OnClosed(WebSocketReceiveResult result)
        {
            if (Closed != null)
                await Closed.Invoke(this, null);
        }

        private async Task OnErrorReceived(string error)
        {
            if (ErrorReceived != null)
                await ErrorReceived.Invoke(this, null);
        }

        public Task SendAudio(ReadOnlyMemory<byte> audio) => SendAudio(audio, CancellationToken.None);

        public async Task SendAudio(ReadOnlyMemory<byte> audio, CancellationToken ct)
            => await _socket.SendAsync(audio, WebSocketMessageType.Binary, true, ct)
                .ConfigureAwait(false);

        public Task SendAudio(ArraySegment<byte> audio) => SendAudio(audio, CancellationToken.None);

        public async Task SendAudio(ArraySegment<byte> audio, CancellationToken ct)
            => await _socket.SendAsync(audio, WebSocketMessageType.Binary, true, ct)
                .ConfigureAwait(false);

        public void Dispose()
        {
            if (_socket.State == WebSocketState.Open)
            {
                Close(false).Wait();
            }

            _socket.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                await Close(false);
            }

            _socket.Dispose();
        }

        public Task Close() => Close(true);

        public async Task Close(bool waitForSessionTermination)
        {
            var ct = CancellationToken.None;
            if (waitForSessionTermination) _sessionTerminatedTaskCompletionSource = new TaskCompletionSource<bool>();

            var bytes = Encoding.UTF8.GetBytes("{\"terminate_session\": true}");
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);

            if (waitForSessionTermination)
                await _sessionTerminatedTaskCompletionSource.Task
                    .ConfigureAwait(false);

            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct)
                .ConfigureAwait(false);
        }
    }

    public enum RealtimeCredentialType
    {
        ApiKey,
        Token
    }
}