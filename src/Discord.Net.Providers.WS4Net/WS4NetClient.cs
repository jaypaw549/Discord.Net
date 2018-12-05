using Discord.Net.WebSockets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebSocket4Net;
using WS4NetSocket = WebSocket4Net.WebSocket;

namespace Discord.Net.Providers.WS4NetAsync
{
    internal class WS4NetClient : IWebSocketClient, IDisposable
    {
        public event Func<byte[], int, int, Task> BinaryMessage;
        public event Func<string, Task> TextMessage;
        public event Func<Exception, Task> Closed;

        private readonly SemaphoreSlim _lock;
        private readonly Dictionary<string, string> _headers;
        private WS4NetSocket _client;
        private CancellationTokenSource _disconnectCancelTokenSource;
        private CancellationTokenSource _cancelTokenSource;
        private CancellationToken _cancelToken, _parentToken;

        private TaskCompletionSource<bool> _connection_promise;
        private bool _connected = false;

        private bool _isDisposed;

        public WS4NetClient()
        {
            _headers = new Dictionary<string, string>();
            _lock = new SemaphoreSlim(1, 1);
            _disconnectCancelTokenSource = new CancellationTokenSource();
            _cancelToken = CancellationToken.None;
            _parentToken = CancellationToken.None;
            _connection_promise = null;
        }
        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    DisconnectInternalAsync(true).GetAwaiter().GetResult();
                    _lock?.Dispose();
                    _cancelTokenSource?.Dispose();
                }
                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public async Task ConnectAsync(string host)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await ConnectInternalAsync(host).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task ConnectInternalAsync(string host)
        {
            await DisconnectInternalAsync().ConfigureAwait(false);

            _disconnectCancelTokenSource?.Dispose();
            _cancelTokenSource?.Dispose();
            _client?.Dispose();

            _disconnectCancelTokenSource = new CancellationTokenSource();
            _cancelTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_parentToken, _disconnectCancelTokenSource.Token);
            _cancelToken = _cancelTokenSource.Token;

            _client = new WS4NetSocket(host, "", customHeaderItems: _headers.ToList())
            {
                EnableAutoSendPing = false,
                NoDelay = true,
                Proxy = null
            };

            _client.MessageReceived += OnTextMessage;
            _client.DataReceived += OnBinaryMessage;
            _client.Opened += OnConnected;
            _client.Closed += OnClosed;

            if (!_connected) // _connected serves as our ManualResetEventSlim set value
            {
                _client.Error += OnError;

                //_connection promise serves as our blocking mechanism, use that if we aren't completed yet.
                if (_connection_promise?.Task.IsCompleted ?? true)
                    _connection_promise = new TaskCompletionSource<bool>();
            }

            _client.Open();

            if (!_connected)
                try
                {
                    //Set our _connected value here so as to not violate our lock.
                    _connected = await _connection_promise.Task.ConfigureAwait(false);
                }
                finally
                {
                    _client.Error -= OnError;
                }

            if (!_connected)
                throw new Exception("Unable to connect!");
        }

        public async Task DisconnectAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await DisconnectInternalAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        private Task DisconnectInternalAsync(bool isDisposing = false)
        {
            _disconnectCancelTokenSource.Cancel();
            if (_client == null)
                return Task.Delay(0);

            if (_client.State == WebSocketState.Open)
            {
                try { _client.Close(1000, ""); }
                catch { }
            }

            _client.MessageReceived -= OnTextMessage;
            _client.DataReceived -= OnBinaryMessage;
            _client.Opened -= OnConnected;
            _client.Closed -= OnClosed;

            try { _client.Dispose(); }
            catch { }
            _client = null;

            _connected = false;
            return Task.Delay(0);
        }

        public void SetHeader(string key, string value)
        {
            _headers[key] = value;
        }

        public void SetCancelToken(CancellationToken cancelToken)
        {
            _cancelTokenSource?.Dispose();
            _parentToken = cancelToken;
            _cancelTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_parentToken, _disconnectCancelTokenSource.Token);
            _cancelToken = _cancelTokenSource.Token;
        }

        public async Task SendAsync(byte[] data, int index, int count, bool isText)
        {
            await _lock.WaitAsync(_cancelToken).ConfigureAwait(false);
            try
            {
                if (isText)
                    _client.Send(Encoding.UTF8.GetString(data, index, count));
                else
                    _client.Send(data, index, count);
            }
            finally
            {
                _lock.Release();
            }
        }

        private void OnTextMessage(object sender, MessageReceivedEventArgs e)
        {
            TextMessage(e.Message).GetAwaiter().GetResult();
        }

        private void OnBinaryMessage(object sender, DataReceivedEventArgs e)
        {
            BinaryMessage(e.Data, 0, e.Data.Length).GetAwaiter().GetResult();
        }

        private void OnConnected(object sender, object e)
        {
            _connection_promise?.TrySetResult(true);
        }

        private void OnClosed(object sender, object e)
        {
            var ex = (e as SuperSocket.ClientEngine.ErrorEventArgs)?.Exception ?? new Exception("Unexpected close");
            Closed(ex).GetAwaiter().GetResult();
        }

        private void OnError(object sender, object e)
        {
            //Task.Run(() => _connection_promise?.TrySetResult(false));
            Task.Run(() => _connection_promise?.TrySetException((e as SuperSocket.ClientEngine.ErrorEventArgs)?.Exception ?? new Exception("Unexpected exception while connecting!")));
        }
    }
}
