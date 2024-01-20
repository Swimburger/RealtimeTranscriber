using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Lib
{
    public delegate Task SessionBeginsEventHandler(RealtimeTranscriber sender, SessionBeginsEventArgs evt);

    public delegate Task PartialTranscriptEventHandler(RealtimeTranscriber sender, PartialTranscriptEventArgs evt);

    public delegate Task FinalTranscriptEventHandler(RealtimeTranscriber sender, FinalTranscriptEventArgs evt);

    public delegate Task TranscriptEventHandler(RealtimeTranscriber sender, TranscriptEventArgs evt);

    public delegate Task ErrorEventHandler(RealtimeTranscriber sender, ErrorEventArgs evt);

    public delegate Task ClosedEventHandler(RealtimeTranscriber sender, ClosedEventArgs evt);

    public class RealtimeTranscriber : IAsyncDisposable, IDisposable
    {
        private const string RealtimeServiceEndpoint = "wss://api.assemblyai.com/v2/realtime/ws";
        private readonly string _authorization;
        private readonly RealtimeCredentialType _credentialType;
        private readonly ClientWebSocket _socket;
        private TaskCompletionSource<bool> _sessionTerminatedTaskCompletionSource;
        private Channel<Transcript> transcriptChannel;
        private Channel<PartialTranscript> partialTranscriptChannel;
        private Channel<FinalTranscript> finalTranscriptChannel;
        public uint SampleRate { get; set; }
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

        public async Task<JsonDocument> ConnectAsync(CancellationToken ct)
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
            var jsonDocument = await ReceiveJsonMessageAsync<JsonDocument>(ct).ConfigureAwait(false);
            if (jsonDocument.RootElement.TryGetProperty("error", out var errorProperty))
            {
                var error = errorProperty.GetString();
                await OnErrorReceived(error).ConfigureAwait(false);
                var closeMessage = await ReceiveCloseMessage(ct).ConfigureAwait(false);
                await OnClosed(closeMessage).ConfigureAwait(false);
                throw new Exception(error);
            }

            if (!jsonDocument.RootElement.TryGetProperty("message_type", out var messageTypeProperty))
            {
                throw new Exception("Real-time service sent unexpected message.");
            }

            if (messageTypeProperty.GetString() != "SessionBegins")
            {
                throw new Exception("Real-time service sent unexpected message.");
            }

            await OnSessionBegins(jsonDocument).ConfigureAwait(false);
            
            transcriptChannel = Channel.CreateUnbounded<Transcript>();
            partialTranscriptChannel = Channel.CreateUnbounded<PartialTranscript>();
            finalTranscriptChannel = Channel.CreateUnbounded<FinalTranscript>();

            Task.Run(async () => await ListenAsync(ct).ConfigureAwait(false), ct);

            return jsonDocument;
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
                            throw new Exception("Real-time service sent an unexpected message.");
                        case "PartialTranscript":
                            var partialTranscript = message.Deserialize<PartialTranscript>();
                            await OnPartialTranscriptReceived(partialTranscript)
                                .ConfigureAwait(false);
                            await OnTranscriptReceived(partialTranscript)
                                .ConfigureAwait(false);
                            break;
                        case "FinalTranscript":
                            var finalTranscript = message.Deserialize<FinalTranscript>();
                            await OnFinalTranscriptReceived(finalTranscript)
                                .ConfigureAwait(false);
                            await OnTranscriptReceived(finalTranscript)
                                .ConfigureAwait(false);
                            break;
                        case "SessionTerminated":
                            OnSessionTerminated();
                            break;
                    }
                }
            }

            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            else if (_socket.State != WebSocketState.Open) throw new Exception("WebSocket is not open.");
            throw new Exception();
        }

        private async Task<WebSocketReceiveResult> ReceiveCloseMessage(CancellationToken ct)
        {
            var buffer = new ArraySegment<byte>(new byte[2048]);
            var result = await _socket.ReceiveAsync(buffer, ct)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return result;
            }

            throw new Exception("Expected close message not received.");
        }

        private async Task<T1> ReceiveJsonMessageAsync<T1>(CancellationToken ct)
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
                throw new Exception("Unexpected close message received.");
            }

            ms.Seek(0, SeekOrigin.Begin);

            var message = await JsonSerializer.DeserializeAsync<T1>(ms, (JsonSerializerOptions)null, ct)
                .ConfigureAwait(false);
            return message;
        }

        private async Task OnSessionBegins(JsonDocument message)
        {
            if (SessionBegins != null)
            {
                var sessionId = message.RootElement.GetProperty("session_id").GetGuid();
                var expiresAt = message.RootElement.GetProperty("expires_at").GetDateTime();
                await SessionBegins.Invoke(this, new SessionBeginsEventArgs
                {
                    SessionId = sessionId,
                    ExpiresAt = expiresAt
                });
            }
        }

        private async Task OnPartialTranscriptReceived(PartialTranscript transcript)
        {
            await partialTranscriptChannel.Writer.WriteAsync(transcript);
            if (PartialTranscriptReceived != null)
            {
                await PartialTranscriptReceived.Invoke(this, new PartialTranscriptEventArgs
                {
                    Transcript = transcript
                });
            }
        }

        private async Task OnFinalTranscriptReceived(FinalTranscript transcript)
        {
            await finalTranscriptChannel.Writer.WriteAsync(transcript);
            if (FinalTranscriptReceived != null)
            {
                await FinalTranscriptReceived.Invoke(this, new FinalTranscriptEventArgs
                {
                    Transcript = transcript
                });
            }
        }

        private async Task OnTranscriptReceived(Transcript transcript)
        {
            await transcriptChannel.Writer.WriteAsync(transcript);
            if (TranscriptReceived != null)
            {
                await TranscriptReceived.Invoke(this, new TranscriptEventArgs
                {
                    Transcript = transcript
                });
            }
        }

        private void OnSessionTerminated()
        {
            _sessionTerminatedTaskCompletionSource?.TrySetResult(true);
        }

        private async Task OnClosed(WebSocketReceiveResult result)
        {
            TryCompleteChannels();
            if (Closed != null)
            {
                await Closed.Invoke(this, new ClosedEventArgs
                {
                    Code = (int)result.CloseStatus!,
                    Reason = result.CloseStatusDescription
                });
            }
        }

        private async Task OnErrorReceived(string error)
        {
            if (ErrorReceived != null)
            {
                await ErrorReceived.Invoke(this, new ErrorEventArgs
                {
                    Error = error
                });
            }
        }

        public Task SendAudio(ReadOnlyMemory<byte> audio) => SendAudio(audio, CancellationToken.None);

        public async Task SendAudio(ReadOnlyMemory<byte> audio, CancellationToken ct)
            => await _socket.SendAsync(audio, WebSocketMessageType.Binary, true, ct)
                .ConfigureAwait(false);

        public Task SendAudio(ArraySegment<byte> audio) => SendAudio(audio, CancellationToken.None);

        public async Task SendAudio(ArraySegment<byte> audio, CancellationToken ct)
            => await _socket.SendAsync(audio, WebSocketMessageType.Binary, true, ct)
                .ConfigureAwait(false);

        public IAsyncEnumerable<Transcript> GetPartialTranscriptsAsync() => GetPartialTranscriptsAsync(CancellationToken.None);
        public IAsyncEnumerable<Transcript> GetPartialTranscriptsAsync(CancellationToken ct) 
            => partialTranscriptChannel.Reader.ReadAllAsync(ct);

        public IAsyncEnumerable<Transcript> GetFinalTranscriptsAsync() => GetFinalTranscriptsAsync(CancellationToken.None);
        public IAsyncEnumerable<Transcript> GetFinalTranscriptsAsync(CancellationToken ct) 
            => finalTranscriptChannel.Reader.ReadAllAsync(ct);
        public IAsyncEnumerable<Transcript> GetTranscriptsAsync() => GetTranscriptsAsync(CancellationToken.None);
        public IAsyncEnumerable<Transcript> GetTranscriptsAsync(CancellationToken ct) 
            => transcriptChannel.Reader.ReadAllAsync(ct);
        
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
            
            TryCompleteChannels();
        }

        private void TryCompleteChannels()
        {
            transcriptChannel?.Writer.TryComplete();
            partialTranscriptChannel?.Writer.TryComplete();
            finalTranscriptChannel?.Writer.TryComplete();
            transcriptChannel = null;
            partialTranscriptChannel = null;
            finalTranscriptChannel = null;
        }
    }

    public enum RealtimeCredentialType
    {
        ApiKey,
        Token
    }

    public sealed class SessionBeginsEventArgs : EventArgs
    {
        internal SessionBeginsEventArgs(){}
        public Guid SessionId { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    public sealed class PartialTranscriptEventArgs : EventArgs
    {
        internal PartialTranscriptEventArgs(){}
        public PartialTranscript Transcript { get; set; }
    }

    public sealed class FinalTranscriptEventArgs : EventArgs
    {
        internal FinalTranscriptEventArgs(){}
        public FinalTranscript Transcript { get; set; }
    }


    public sealed class TranscriptEventArgs : EventArgs
    {
        internal TranscriptEventArgs(){}
        public Transcript Transcript { get; set; }
    }
    
    public class Transcript
    {
        [JsonPropertyName("text")] public string Text { get; set; }
    }
    
    public class FinalTranscript : Transcript
    {
    }

    public class PartialTranscript : Transcript
    {
    }

    public sealed class ErrorEventArgs : EventArgs
    {
        internal ErrorEventArgs(){}
        public string Error { get; set; }
    }

    public sealed class ClosedEventArgs : EventArgs
    {
        internal ClosedEventArgs(){}
        public int Code { get; set; }
        public string Reason { get; set; }
    }
}