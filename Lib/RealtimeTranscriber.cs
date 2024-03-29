﻿using System;
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
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Lib
{
    public delegate void SessionBeginsEventHandler(RealtimeTranscriber sender, SessionBeginsEventArgs evt);

    public delegate void PartialTranscriptEventHandler(RealtimeTranscriber sender, PartialTranscriptEventArgs evt);

    public delegate void FinalTranscriptEventHandler(RealtimeTranscriber sender, FinalTranscriptEventArgs evt);

    public delegate void TranscriptEventHandler(RealtimeTranscriber sender, TranscriptEventArgs evt);

    public delegate void ErrorEventHandler(RealtimeTranscriber sender, ErrorEventArgs evt);

    public delegate void ClosedEventHandler(RealtimeTranscriber sender, ClosedEventArgs evt);

    public class RealtimeTranscriber : IAsyncDisposable, IDisposable
    {
        private const string RealtimeServiceEndpoint = "wss://api.assemblyai.com/v2/realtime/ws";
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private TaskCompletionSource<bool> _sessionTerminatedTaskCompletionSource;
        private Channel<Transcript> _transcriptChannel;
        private Channel<PartialTranscript> _partialTranscriptChannel;
        private Channel<FinalTranscript> _finalTranscriptChannel;

        /// <summary>
        /// Use your AssemblyAI API key to authenticate with the AssemblyAI real-time transcriber.
        /// </summary>
        public string ApiKey { private get; set; }

        /// <summary>
        /// Use a temporary auth token to authenticate with the AssemblyAI real-time transcriber.
        /// Learn <see href="https://www.assemblyai.com/docs/guides/real-time-streaming-transcription#creating-temporary-authentication-tokens">how to generate a temporary token here</see>.
        /// </summary>
        public string Token { private get; set; }

        /// <summary>
        /// The sample rate of the streamed audio
        /// </summary>
        public uint SampleRate { get; set; }

        /// <summary>
        /// Add up to 2500 characters of custom vocabulary
        /// </summary>
        public IEnumerable<string> WordBoost { get; set; } = Enumerable.Empty<string>();

        /// <summary>
        /// The encoding of the audio data
        /// </summary>
        public string Encoding { get; set; }

        /// <summary>
        /// Event for when the real-time session begins
        /// </summary>
        public event SessionBeginsEventHandler SessionBegins;
        
        /// <summary>
        /// Event for when a partial transcript is received.
        /// </summary>
        public event PartialTranscriptEventHandler PartialTranscriptReceived;
        
        /// <summary>
        /// Event for when a final transcript is received.
        /// </summary>
        public event FinalTranscriptEventHandler FinalTranscriptReceived;
        
        /// <summary>
        /// Event for when a partial or final transcript is received.
        /// </summary>
        public event TranscriptEventHandler TranscriptReceived;
        
        /// <summary>
        /// Event for when an error is received from the real-time service.
        /// </summary>
        public event ErrorEventHandler ErrorReceived;
        
        /// <summary>
        /// Event for when the connection with the real-time service is closed.
        /// </summary>
        public event ClosedEventHandler Closed;

        /// <summary>
        /// Connect to AssemblyAI's real-time transcription service, and start listening for messages.
        /// </summary>
        /// <returns>The session begins message</returns>
        public Task<SessionBeginsMessage> ConnectAsync() => ConnectAsync(CancellationToken.None);

        /// <summary>
        /// Connect to AssemblyAI's real-time transcription service, and start listening for messages.
        /// </summary>
        /// <param name="ct">Cancellation token to cancel connecting to the service and stop listening for messages.</param>
        /// <returns>The session begins message</returns>
        public async Task<SessionBeginsMessage> ConnectAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(Token) && string.IsNullOrEmpty(ApiKey))
            {
                throw new Exception("You must configure ApiKey or Token to authenticate the real-time transcriber.");
            }
            
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

            if (!string.IsNullOrEmpty(Encoding))
            {
                urlBuilder.AppendFormat("{0}encoding={1}", queryPrefix, Encoding);
                queryPrefix = "&";
            }

            if (!string.IsNullOrEmpty(Token))
            {
                urlBuilder.AppendFormat("{0}token={1}", queryPrefix, Token);
            }
            else
            {
                _socket.Options.SetRequestHeader("Authorization", ApiKey);
            }

            await _socket.ConnectAsync(new Uri(urlBuilder.ToString()), ct).ConfigureAwait(false);
            var jsonDocument = await ReceiveJsonMessageAsync<JsonDocument>(ct).ConfigureAwait(false);
            if (jsonDocument.RootElement.TryGetProperty("error", out var errorProperty))
            {
                var error = errorProperty.GetString();
                OnErrorReceived(error);
                var closeMessage = await ReceiveCloseMessage(ct).ConfigureAwait(false);
                OnClosed(closeMessage);
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

            var sessionBeginsMessage = jsonDocument.Deserialize<SessionBeginsMessage>();
            OnSessionBegins(sessionBeginsMessage);

            _transcriptChannel = Channel.CreateUnbounded<Transcript>();
            _partialTranscriptChannel = Channel.CreateUnbounded<PartialTranscript>();
            _finalTranscriptChannel = Channel.CreateUnbounded<FinalTranscript>();

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Run(async () => await ListenAsync(ct).ConfigureAwait(false), ct);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

            return sessionBeginsMessage;
        }

        /// <summary>
        /// Listen for JSON messages from the real-time service.
        /// </summary>
        private Task ListenAsync() => ListenAsync(CancellationToken.None);

        /// <summary>
        /// Listen for JSON messages from the real-time service.
        /// </summary>
        /// <param name="ct">Token to stop listening for messages.</param>
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
                    OnClosed(result);
                    return;
                }

                ms.Seek(0, SeekOrigin.Begin);

                var message = await JsonSerializer.DeserializeAsync<JsonDocument>(ms, (JsonSerializerOptions)null, ct)
                    .ConfigureAwait(false);
                if (message.RootElement.TryGetProperty("error", out var errorProperty))
                {
                    var error = errorProperty.GetString();
                    OnErrorReceived(error);
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
        }

        /// <summary>
        /// Receive a close message from the real-time service, and throw exception if a different message is received.
        /// </summary>
        /// <param name="ct"></param>
        /// <returns>The result of the close message.</returns>
        /// <exception cref="Exception"></exception>
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

        /// <summary>
        /// Receive a JSON message of the given type. An exception is thrown if a close message is received instead.
        /// </summary>
        /// <param name="ct"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns>The object of type T deserialized from the JSON message.</returns>
        /// <exception cref="Exception"></exception>
        private async Task<T> ReceiveJsonMessageAsync<T>(CancellationToken ct)
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

            var message = await JsonSerializer.DeserializeAsync<T>(ms, (JsonSerializerOptions)null, ct)
                .ConfigureAwait(false);
            return message;
        }

        /// <summary>
        /// Called when session begins message is received. Calls the SessionBegins event.
        /// </summary>
        /// <param name="sessionBeginsMessage"></param>
        private void OnSessionBegins(SessionBeginsMessage sessionBeginsMessage)
        {
            SessionBegins?.Invoke(this, new SessionBeginsEventArgs(sessionBeginsMessage));
        }

        /// <summary>
        /// Called when a partial transcript message is received. Calls the PartialTranscriptReceived event.
        /// </summary>
        /// <param name="transcript"></param>
        private async Task OnPartialTranscriptReceived(PartialTranscript transcript)
        {
            await _partialTranscriptChannel.Writer.WriteAsync(transcript);
            PartialTranscriptReceived?.Invoke(this, new PartialTranscriptEventArgs(transcript));
        }

        /// <summary>
        /// Called when a final transcript message is received. Calls the FinalTranscriptReceived event.
        /// </summary>
        /// <param name="transcript"></param>
        private async Task OnFinalTranscriptReceived(FinalTranscript transcript)
        {
            await _finalTranscriptChannel.Writer.WriteAsync(transcript);
            FinalTranscriptReceived?.Invoke(this, new FinalTranscriptEventArgs(transcript));
        }

        /// <summary>
        /// Called when a partial or a final transcript message is received. Calls the TranscriptReceived event.
        /// </summary>
        /// <param name="transcript"></param>
        private async Task OnTranscriptReceived(Transcript transcript)
        {
            await _transcriptChannel.Writer.WriteAsync(transcript);
            TranscriptReceived?.Invoke(this, new TranscriptEventArgs(transcript));
        }

        /// <summary>
        /// Called when the session terminated message is received. Completes the session terminated task.
        /// </summary>
        private void OnSessionTerminated()
        {
            _sessionTerminatedTaskCompletionSource?.TrySetResult(true);
        }

        private void OnClosed(WebSocketReceiveResult result)
        {
            TryCompleteChannels();
            Closed?.Invoke(this, new ClosedEventArgs
            {
                Code = (int)result.CloseStatus!,
                Reason = result.CloseStatusDescription
            });
        }

        /// <summary>
        /// Called when an error message is received. Calls the ErrorReceived event.
        /// </summary>
        /// <param name="error"></param>
        private void OnErrorReceived(string error)
        {
            ErrorReceived?.Invoke(this, new ErrorEventArgs
            {
                Error = error
            });
        }

        /// <summary>
        /// Send audio to the real-time service.
        /// </summary>
        /// <param name="audio">Audio to transcribe</param>
        /// <returns></returns>
        public Task SendAudioAsync(ReadOnlyMemory<byte> audio) => SendAudioAsync(audio, CancellationToken.None);

        /// <summary>
        /// Send audio to the real-time service.
        /// </summary>
        /// <param name="audio">Audio to transcribe</param>
        /// <param name="ct">Token to cancel the send operation.</param>
        public async Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
            => await _socket.SendAsync(audio, WebSocketMessageType.Binary, true, ct)
                .ConfigureAwait(false);

        /// <summary>
        /// Send audio to the real-time service.
        /// </summary>
        /// <param name="audio">Audio to transcribe</param>
        /// <returns></returns>
        public Task SendAudioAsync(ArraySegment<byte> audio) => SendAudioAsync(audio, CancellationToken.None);

        /// <summary>
        /// Send audio to the real-time service.
        /// </summary>
        /// <param name="audio">Audio to transcribe</param>
        /// <param name="ct">Token to cancel the send operation.</param>
        public async Task SendAudioAsync(ArraySegment<byte> audio, CancellationToken ct)
            => await _socket.SendAsync(audio, WebSocketMessageType.Binary, true, ct)
                .ConfigureAwait(false);

        /// <summary>
        /// Experimental! Get a reader to read partial transcripts.
        /// </summary>
        /// <returns>Reader to read partial transcripts</returns>
        public PartialTranscriptReader GetPartialTranscriptReader()
            => new PartialTranscriptReader(_partialTranscriptChannel.Reader);

        /// <summary>
        /// Experimental! Get a reader to read final transcripts.
        /// </summary>
        /// <returns>Reader to read final transcripts</returns>
        public FinalTranscriptReader GetFinalTranscriptReader()
            => new FinalTranscriptReader(_finalTranscriptChannel.Reader);

        /// <summary>
        /// Experimental! Get a reader to read partial and final transcripts.
        /// </summary>
        /// <returns>Reader to read partial and final transcripts</returns>
        public TranscriptReader GetTranscriptReader()
            => new TranscriptReader(_transcriptChannel.Reader);

        /// <summary>
        /// Terminates the real-time transcription session, closes the connection, and disposes the real-time transcriber.
        /// </summary>
        /// <remarks>
        /// Any remaining partial or final transcripts will not be received.
        /// To receive remaining partial or final transcripts, call <see cref="CloseAsync()" /> before disposing.
        /// </remarks>
        public void Dispose()
        {
            if (_socket.State == WebSocketState.Open)
            {
                CloseAsync(false).Wait();
            }

            _socket.Dispose();
        }

        /// <summary>
        /// Terminates the real-time transcription session, closes the connection, and disposes the real-time transcriber.
        /// </summary>
        /// <remarks>
        /// Any remaining partial or final transcripts will not be received.
        /// To receive remaining partial or final transcripts, call <see cref="CloseAsync()" /> before disposing.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                await CloseAsync(false);
            }

            _socket.Dispose();
        }

        /// <summary>
        /// Terminate the real-time transcription session and close the connection.
        /// </summary>
        /// <returns></returns>
        public Task CloseAsync() => CloseAsync(true);

        /// <summary>
        /// Terminate the real-time transcription session and close the connection.
        /// </summary>
        /// <param name="waitForSessionTerminated">Wait to receive pending transcripts and session terminated message.</param>
        public async Task CloseAsync(bool waitForSessionTerminated)
        {
            var ct = CancellationToken.None;
            if (waitForSessionTerminated)
            {
                _sessionTerminatedTaskCompletionSource = new TaskCompletionSource<bool>();
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"terminate_session\": true}");
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);

            if (waitForSessionTerminated)
            {
                await _sessionTerminatedTaskCompletionSource.Task
                    .ConfigureAwait(false);
            }

            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct)
                .ConfigureAwait(false);

            TryCompleteChannels();
        }

        /// <summary>
        /// Complete the channels if present and set to null.
        /// </summary>
        private void TryCompleteChannels()
        {
            // channels may not be initialized, and may be completed already
            _transcriptChannel?.Writer.TryComplete();
            _partialTranscriptChannel?.Writer.TryComplete();
            _finalTranscriptChannel?.Writer.TryComplete();
            _transcriptChannel = null;
            _partialTranscriptChannel = null;
            _finalTranscriptChannel = null;
        }
    }

    public sealed class FinalTranscriptReader : DecoratorReader<FinalTranscript>
    {
        internal FinalTranscriptReader(ChannelReader<FinalTranscript> inner) : base(inner)
        {
        }
    }
    
    public sealed class PartialTranscriptReader : DecoratorReader<PartialTranscript>
    {
        internal PartialTranscriptReader(ChannelReader<PartialTranscript> inner) : base(inner)
        {
        }
    }
    
    public sealed class TranscriptReader : DecoratorReader<Transcript>
    {
        internal TranscriptReader(ChannelReader<Transcript> inner) : base(inner)
        {
        }
    }

    public class DecoratorReader<T> : ChannelReader<T>
    {
        private readonly ChannelReader<T> _inner;

        internal DecoratorReader(ChannelReader<T> inner)
        {
            _inner = inner;
        }
        
        public override bool TryRead(out T item) => _inner.TryRead(out item);

        public ValueTask<bool> WaitToReadAsync() => WaitToReadAsync(CancellationToken.None);

        // ReSharper disable once OptionalParameterHierarchyMismatch
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken)
            => _inner.WaitToReadAsync(cancellationToken);
    }

    /// <summary>
    /// The newly started real-time transcription session.
    /// </summary>
    public sealed class SessionBeginsMessage
    {
        [JsonPropertyName("session_id")] public Guid SessionId { get; set; }
        [JsonPropertyName("expires_at")] public DateTime ExpiresAt { get; set; }
    }

    public class RealtimeTranscriberEventArgs<T> : EventArgs
    {
        internal RealtimeTranscriberEventArgs(T result)
        {
            Result = result;
        }

        public T Result { get; }
    }

    /// <summary>
    /// Event arguments for the newly started real-time transcription session.
    /// </summary>
    public sealed class SessionBeginsEventArgs : RealtimeTranscriberEventArgs<SessionBeginsMessage>
    {
        internal SessionBeginsEventArgs(SessionBeginsMessage result) : base(result)
        {
        }
    }

    /// <summary>
    /// Event arguments for a partial transcript.
    /// </summary>
    public sealed class PartialTranscriptEventArgs : RealtimeTranscriberEventArgs<PartialTranscript>
    {
        internal PartialTranscriptEventArgs(PartialTranscript result) : base(result)
        {
        }
    }


    /// <summary>
    /// Event arguments for a final transcript.
    /// </summary>
    public sealed class FinalTranscriptEventArgs : RealtimeTranscriberEventArgs<FinalTranscript>
    {
        internal FinalTranscriptEventArgs(FinalTranscript result) : base(result)
        {
        }
    }

    /// <summary>
    /// Event arguments for a partial or final partial transcript.
    /// </summary>
    public sealed class TranscriptEventArgs : RealtimeTranscriberEventArgs<Transcript>
    {
        internal TranscriptEventArgs(Transcript result) : base(result)
        {
        }
    }

    /// <summary>
    /// A final or partial transcript
    /// </summary>
    public abstract class Transcript
    {
        [JsonPropertyName("text")] public string Text { get; set; }
    }

    /// <summary>
    /// A final transcript
    /// </summary>
    public class FinalTranscript : Transcript
    {
    }

    /// <summary>
    /// A partial transcript
    /// </summary>
    public class PartialTranscript : Transcript
    {
    }

    /// <summary>
    /// Event arguments for an error sent by the real-time service.
    /// </summary>
    public sealed class ErrorEventArgs : EventArgs
    {
        internal ErrorEventArgs()
        {
        }

        public string Error { get; set; }
    }

    /// <summary>
    /// Event arguments for when the connection with the real-time service is closed.
    /// </summary>
    public sealed class ClosedEventArgs : EventArgs
    {
        internal ClosedEventArgs()
        {
        }

        public int Code { get; internal set; }
        public string Reason { get; internal set; }
    }
}