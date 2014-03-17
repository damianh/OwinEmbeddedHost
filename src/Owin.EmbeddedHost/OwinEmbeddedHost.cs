// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.
// Portions taken from the KatanaProject. Licence Apache 2.

namespace Owin.EmbeddedHost
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Owin;
    using Microsoft.Owin.Hosting;
    using Microsoft.Owin.Hosting.Engine;
    using Microsoft.Owin.Hosting.ServerFactory;
    using Microsoft.Owin.Hosting.Services;

    public sealed class OwinEmbeddedHost : IDisposable
    {
        private Func<IDictionary<string, object>, Task> _next;
        private IDisposable _started;
        private bool _disposed;

        private OwinEmbeddedHost()
        {
        }

        public void Dispose()
        {
            _disposed = true;
            _started.Dispose();
        }

        public static OwinEmbeddedHost Create(Action<IAppBuilder> startup)
        {
            var server = new OwinEmbeddedHost();
            server.Configure(startup);
            return server;
        }

        private void Configure(Action<IAppBuilder> startup, StartOptions options = null)
        {
            // Compare with WebApp.StartImplementation
            if (startup == null)
            {
                throw new ArgumentNullException("startup");
            }

            options = options ?? new StartOptions();
            if (string.IsNullOrWhiteSpace(options.AppStartup))
            {
                // Populate AppStartup for use in host.AppName
                options.AppStartup = startup.Method.ReflectedType.FullName;
            }

            var testServerFactory = new OwinEmbeddedServerFactory();
            IServiceProvider services = ServicesFactory.Create();
            var engine = services.GetService<IHostingEngine>();
            var context = new StartContext(options)
            {
                ServerFactory = new ServerFactoryAdapter(testServerFactory),
                Startup = startup
            };
            _started = engine.Start(context);
            _next = testServerFactory.Invoke;
        }

        public async Task Invoke(IDictionary<string, object> environment)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
            var owinContext = new OwinContext(environment);
            owinContext.Response.Headers.Append("Server", "OwinEmbedded");
            owinContext.Response.Headers.Set("Date", DateTimeOffset.UtcNow.ToString("r"));
            owinContext.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0");
            owinContext.Response.Headers.Append("Cache-Control", "post-check=0, pre-check=0");
            owinContext.Response.Headers.Set("Pragma", "no-cache");
            await _next.Invoke(environment);
        }

        private class OwinEmbeddedServerFactory
        {
            private Func<IDictionary<string, object>, Task> app;
            private IDictionary<string, object> properties;

            public IDisposable Create(Func<IDictionary<string, object>, Task> app,
                IDictionary<string, object> properties)
            {
                this.app = app;
                this.properties = properties;
                return new Disposable();
            }

            public Task Invoke(IDictionary<string, object> env)
            {
                return app.Invoke(env);
            }

            private class Disposable : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }

        // This steam accepts writes from the server/app, buffers them internally, and returns the data via Reads
        // when requested by the client. This mimics a network stream.
        private class ResponseStream : Stream
        {
            private static readonly Task CanceledTask;
            private readonly ConcurrentQueue<byte[]> _bufferedData;
            private readonly Action _onFirstWrite;
            private readonly SemaphoreSlim _readLock;
            private readonly SemaphoreSlim _writeLock;
            private Exception _abortException;
            private bool _aborted;
            private bool _disposed;
            private bool _firstWrite;
            private TaskCompletionSource<object> _readWaitingForData;
            private ArraySegment<byte> _topBuffer;

            static ResponseStream()
            {
                var tcs = new TaskCompletionSource<int>();
                tcs.SetCanceled();
                CanceledTask = tcs.Task;
            }

            internal ResponseStream(Action onFirstWrite)
            {
                if (onFirstWrite == null)
                {
                    throw new ArgumentNullException("onFirstWrite");
                }
                _onFirstWrite = onFirstWrite;
                _firstWrite = true;

                _readLock = new SemaphoreSlim(1, 1);
                _writeLock = new SemaphoreSlim(1, 1);
                _bufferedData = new ConcurrentQueue<byte[]>();
                _readWaitingForData = new TaskCompletionSource<object>();
            }

            public override bool CanRead
            {
                get { return true; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return true; }
            }

            #region NotSupported

            public override long Length
            {
                get { throw new NotSupportedException(); }
            }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            #endregion NotSupported

            public override void Flush()
            {
                CheckDisposed();

                _writeLock.Wait();
                try
                {
                    FirstWrite();
                }
                finally
                {
                    _writeLock.Release();
                }

                // TODO: Wait for data to drain?
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                VerifyBuffer(buffer, offset, count, false);
                _readLock.Wait();
                try
                {
                    int totalRead = 0;
                    do
                    {
                        // Don't drain buffered data when signaling an abort.
                        CheckAborted();
                        if (_topBuffer.Count <= 0)
                        {
                            byte[] topBuffer;
                            while (!_bufferedData.TryDequeue(out topBuffer))
                            {
                                if (_disposed)
                                {
                                    CheckAborted();
                                    // Graceful close
                                    return totalRead;
                                }
                                WaitForDataAsync().Wait();
                            }
                            _topBuffer = new ArraySegment<byte>(topBuffer);
                        }
                        int actualCount = Math.Min(count, _topBuffer.Count);
                        Buffer.BlockCopy(_topBuffer.Array, _topBuffer.Offset, buffer, offset, actualCount);
                        _topBuffer = new ArraySegment<byte>(_topBuffer.Array,
                            _topBuffer.Offset + actualCount,
                            _topBuffer.Count - actualCount);
                        totalRead += actualCount;
                        offset += actualCount;
                        count -= actualCount;
                    } while (count > 0 && (_topBuffer.Count > 0 || _bufferedData.Count > 0));
                    // Keep reading while there is more data available and we have more space to put it in.
                    return totalRead;
                }
                finally
                {
                    _readLock.Release();
                }
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                VerifyBuffer(buffer, offset, count, false);
                CancellationTokenRegistration registration = cancellationToken.Register(Abort);
                await _readLock.WaitAsync(cancellationToken);
                try
                {
                    int totalRead = 0;
                    do
                    {
                        // Don't drained buffered data on abort.
                        CheckAborted();
                        if (_topBuffer.Count <= 0)
                        {
                            byte[] topBuffer;
                            while (!_bufferedData.TryDequeue(out topBuffer))
                            {
                                if (_disposed)
                                {
                                    CheckAborted();
                                    // Graceful close
                                    return totalRead;
                                }
                                await WaitForDataAsync();
                            }
                            _topBuffer = new ArraySegment<byte>(topBuffer);
                        }
                        int actualCount = Math.Min(count, _topBuffer.Count);
                        Buffer.BlockCopy(_topBuffer.Array, _topBuffer.Offset, buffer, offset, actualCount);
                        _topBuffer = new ArraySegment<byte>(_topBuffer.Array,
                            _topBuffer.Offset + actualCount,
                            _topBuffer.Count - actualCount);
                        totalRead += actualCount;
                        offset += actualCount;
                        count -= actualCount;
                    } while (count > 0 && (_topBuffer.Count > 0 || _bufferedData.Count > 0));
                    // Keep reading while there is more data available and we have more space to put it in.
                    return totalRead;
                }
                finally
                {
                    registration.Dispose();
                    _readLock.Release();
                }
            }

            // Called under write-lock.
            private void FirstWrite()
            {
                if (_firstWrite)
                {
                    _firstWrite = false;
                    _onFirstWrite();
                }
            }

            // Write with count 0 will still trigger OnFirstWrite
            public override void Write(byte[] buffer, int offset, int count)
            {
                VerifyBuffer(buffer, offset, count, true);
                CheckDisposed();

                _writeLock.Wait();
                try
                {
                    FirstWrite();
                    if (count == 0)
                    {
                        return;
                    }
                    // Copies are necessary because we don't know what the caller is going to do with the buffer afterwards.
                    var internalBuffer = new byte[count];
                    Buffer.BlockCopy(buffer, offset, internalBuffer, 0, count);
                    _bufferedData.Enqueue(internalBuffer);

                    SignalDataAvailable();
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback,
                object state)
            {
                Write(buffer, offset, count);
                var tcs = new TaskCompletionSource<object>(state);
                tcs.TrySetResult(null);
                IAsyncResult result = tcs.Task;
                if (callback != null)
                {
                    callback(result);
                }
                return result;
            }

            public override void EndWrite(IAsyncResult asyncResult)
            {
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                VerifyBuffer(buffer, offset, count, true);
                if (cancellationToken.IsCancellationRequested)
                {
                    return CanceledTask;
                }

                Write(buffer, offset, count);
                return Task.FromResult<object>(null);
            }

            private static void VerifyBuffer(byte[] buffer, int offset, int count, bool allowEmpty)
            {
                if (buffer == null)
                {
                    throw new ArgumentNullException("buffer");
                }
                if (offset < 0 || offset > buffer.Length)
                {
                    throw new ArgumentOutOfRangeException("offset", offset, string.Empty);
                }
                if (count < 0 || count > buffer.Length - offset
                    || (!allowEmpty && count == 0))
                {
                    throw new ArgumentOutOfRangeException("count", count, string.Empty);
                }
            }

            private void SignalDataAvailable()
            {
                // Dispatch, as TrySetResult will synchronously execute the waiters callback and block our Write.
                Task.Factory.StartNew(() => _readWaitingForData.TrySetResult(null));
            }

            private Task WaitForDataAsync()
            {
                _readWaitingForData = new TaskCompletionSource<object>();

                if (!_bufferedData.IsEmpty || _disposed)
                {
                    // Race, data could have arrived before we created the TCS.
                    _readWaitingForData.TrySetResult(null);
                }

                return _readWaitingForData.Task;
            }

            private void Abort()
            {
                _aborted = true;
                _abortException = new OperationCanceledException();
                Dispose();
            }

            private void CheckAborted()
            {
                if (_aborted)
                {
                    throw new IOException(string.Empty, _abortException);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    // Throw for further writes, but not reads.  Allow reads to drain the buffered data and then return 0 for further reads.
                    _disposed = true;
                    _readWaitingForData.TrySetResult(null);
                }

                base.Dispose(disposing);
            }

            private void CheckDisposed()
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(GetType().FullName);
                }
            }
        }
    }
}