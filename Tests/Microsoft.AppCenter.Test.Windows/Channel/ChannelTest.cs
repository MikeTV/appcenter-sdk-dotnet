// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AppCenter.Channel;
using Microsoft.AppCenter.Ingestion;
using Microsoft.AppCenter.Ingestion.Http;
using Microsoft.AppCenter.Ingestion.Models;
using Microsoft.AppCenter.Ingestion.Models.Serialization;
using Microsoft.AppCenter.Storage;
using Microsoft.AppCenter.Test.Storage;
using Microsoft.AppCenter.Utils.Synchronization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Microsoft.AppCenter.Test.Channel
{
    using Channel = Microsoft.AppCenter.Channel.Channel;

    [TestClass]
    public class ChannelTest
    {
        private AggregateException _unobservedTaskException;

        private Mock<IIngestion> _mockIngestion;

        private Channel _channel;

        private IStorage _storage;

        private const string ChannelName = "test";

        private const int MaxLogsPerBatch = 3;

        private const int MaxParallelBatches = 3;

        // Event semaphores for invokation verification
        private const int SendingLogSemaphoreIdx = 0;

        private const int SentLogSemaphoreIdx = 1;

        private const int FailedToSendLogSemaphoreIdx = 2;

        private const int EnqueuingLogSemaphoreIdx = 3;

        private const int FilteringLogSemaphoreIdx = 4;

        private List<SemaphoreSlim> _eventSemaphores;

        public ChannelTest()
        {
            LogSerializer.AddLogType(TestLog.JsonIdentifier, typeof(TestLog));
        }

        [TestInitialize]
        public void InitializeChannelTest()
        {
            _unobservedTaskException = null;
            _mockIngestion = new Mock<IIngestion>();
            SetupIngestionCallSucceed();
            SetChannelWithTimeSpan(TimeSpan.FromSeconds(1));
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            AppCenterLog.Level = LogLevel.Verbose;
        }

        [TestCleanup]
        public void CleanupAppCenterTest()
        {
            EnsureAllTasksAreFinishedInChannel();

            // The UnobservedTaskException will only happen if a Task gets collected by the GC with an exception unobserved
            GC.Collect();
            GC.WaitForPendingFinalizers();
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

            if (_unobservedTaskException != null)
            {
                throw _unobservedTaskException;
            }
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            _unobservedTaskException = e.Exception;
        }

        /// <summary>
        /// Verify that channel is enabled by default
        /// </summary>
        [TestMethod]
        public void ChannelEnabledByDefault()
        {
            Assert.IsTrue(_channel.IsEnabled);
        }

        /// <summary>
        /// Verify that disabling channel has the expected effect
        /// </summary>
        [TestMethod]
        public void DisableChannel()
        {
            _channel.SetEnabled(false);

            Assert.IsFalse(_channel.IsEnabled);
        }

        /// <summary>
        /// Verify that enabling the channel has the expected effect
        /// </summary>
        [TestMethod]
        public void EnableChannel()
        {
            _channel.SetEnabled(false);
            _channel.SetEnabled(true);

            Assert.IsTrue(_channel.IsEnabled);
        }

        /// <summary>
        /// Verify that enqueuing a log passes the same log reference to enqueue event argument
        /// </summary>
        [TestMethod]
        public async Task EnqueuedLogsAreSame()
        {
            var log = new TestLog();
            var sem = new SemaphoreSlim(0);
            _channel.EnqueuingLog += (sender, args) =>
            {
                Assert.AreSame(log, args.Log);
                sem.Release();
            };

            await _channel.EnqueueAsync(log);
            Assert.IsTrue(sem.Wait(0));
        }

        /// <summary>
        /// Verify that when a batch reaches its capacity, it is sent immediately
        /// </summary>
        [TestMethod]
        public async Task EnqueueMaxLogs()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            SetChannelWithTimeSpan(TimeSpan.FromHours(1));
            for (var i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }
            VerifySendingLog(MaxLogsPerBatch);
        }

        /// <summary>
        /// Verify that when channel is disabled, sent event is not triggered
        /// </summary>
        [TestMethod]
        public async Task EnqueueWhileDisabled()
        {
            _channel.SetEnabled(false);
            var log = new TestLog();
            await _channel.EnqueueAsync(log);
            VerifyFailedToSendLog(1);
            VerifyEnqueuingLog(0);
        }

        [TestMethod]
        public async Task ChannelInvokesFilteringLogEvent()
        {
            for (var i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }
            VerifyFilteringLog(MaxLogsPerBatch);
        }

        /// <summary>
        /// Validate filtering out a log
        /// </summary>
        [TestMethod]
        public async Task FilterLogShouldNotSend()
        {
            _channel.FilteringLog += (sender, args) => args.FilterRequested = true;
            for (int i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }
            VerifyFilteringLog(MaxLogsPerBatch);
            VerifySendingLog(0);
            VerifySentLog(0);
        }

        /// <summary>
        /// Validate filters can cancel each other
        /// </summary>
        [TestMethod]
        public async Task FilterLogThenCancelFilterLogInAnotherHandlerShouldSend()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            _channel.FilteringLog += (sender, args) => args.FilterRequested = true;
            _channel.FilteringLog += (sender, args) => args.FilterRequested = false;
            for (int i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }
            VerifyFilteringLog(MaxLogsPerBatch);
            VerifySendingLog(MaxLogsPerBatch);
            VerifySentLog(MaxLogsPerBatch);
        }

        [TestMethod]
        public async Task ChannelInvokesSendingLogEvent()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            for (var i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }
            VerifySendingLog(MaxLogsPerBatch);
        }

        [TestMethod]
        public async Task ChannelInvokesSentLogEvent()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            for (var i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }
            VerifySentLog(MaxLogsPerBatch);
        }

        [TestMethod]
        public void VerifyStateChannelAfterEnablingNetworkRequests()
        {
            // Disable channel.
            _channel.SetEnabled(false);

            // Allow network requests and check that state of channel wasn't changed.
            _channel.SetNetworkRequestAllowed(true);
            Assert.IsFalse(_channel.IsEnabled);

            // Enable channel.
            _channel.SetEnabled(true);

            // Disallow network requests and check that state of channel wasn't changed.
            _channel.SetNetworkRequestAllowed(false);
            Assert.IsTrue(_channel.IsEnabled);
        }

        [TestMethod]
        public async Task ChannelInvokesSentLogEventWhenNetworkRequestsDallowed()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(false);
            for (var i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }
            VerifySentLog(0);
            VerifySendingLog(0);
        }

        [TestMethod]
        public async Task ChannelInvokesFailedToSendLogEvent()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            SetupIngestionCallFail(new Exception());
            for (var i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }
            VerifyFailedToSendLog(MaxLogsPerBatch);
        }

        /// <summary>
        /// Verify that after throw HttpRequestException logs will be recoverable. 
        /// </summary>
        [TestMethod]
        public async Task ChannelInvokesFailedWithHttpRequestExceptionToSendLogEvent()
        {
            // Prepare data.
            Mock<HttpClient> mockHttpClient = new Mock<HttpClient>();
            mockHttpClient.Setup(setup => setup.SendAsync(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>())).Throws(new HttpRequestException());
            IIngestion mockIngestion = new NetworkStateIngestion(new RetryableIngestion(new IngestionHttp(new HttpNetworkAdapter(mockHttpClient.Object))), new NetworkStateAdapter());
            SetChannelWithTimeSpanAndIngestion(TimeSpan.FromSeconds(1), mockIngestion);
            for (var i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }

            // Verify that SendAsync was called 3 times.
            mockHttpClient.Verify(setup => setup.SendAsync(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()), Times.AtMost(MaxLogsPerBatch));

            // Verify that FailedToSendLog wasn't called.
            Assert.AreNotEqual(MaxLogsPerBatch, EventWithSemaphoreOccurred(_eventSemaphores[FailedToSendLogSemaphoreIdx]),
               $"Failed on verify {nameof(Channel.FailedToSendLog)} event call times");
        }

        /// <summary>
        /// Validate that links are same on an error and a log
        /// </summary>
        [TestMethod]
        public void FailedToSendLogEventArgsAreSame()
        {
            var ex = new Exception();
            var log = new TestLog();
            var failedEventLogArgs = new FailedToSendLogEventArgs(log, ex);
            Assert.AreSame(log, failedEventLogArgs.Log);
            Assert.AreSame(ex, failedEventLogArgs.Exception);
        }

        /// <summary>
        /// Validate that channel will send log after enabling
        /// </summary>
        [TestMethod]
        public async Task ChannelInvokesSendingLogEventAfterEnabling()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            await _channel.ShutdownAsync();
            for (int i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }
            _channel.SetEnabled(true);
            VerifySendingLog(MaxLogsPerBatch);
        }

        /// <summary>
        /// Verify that logs are sent only when network requests are allowed.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task ChannelInvokesSendingLogEventAfterEnablingWhenNetworkRequestDisallowed()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(false);
            await _channel.ShutdownAsync();
            for (int i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }
            _channel.SetEnabled(true);
            VerifySendingLog(0);

            // Enable network requests and verify that logs were sent.
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            _channel.SetNetworkRequestAllowed(true);
            VerifySendingLog(MaxLogsPerBatch);

            // Disable network request and verify that logs were not sent.
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(false);
            _channel.SetNetworkRequestAllowed(false);
            for (int i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }
            VerifySendingLog(0);

            // Enable network requests again and verify that logs were sent.
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            _channel.SetNetworkRequestAllowed(true);
            VerifySendingLog(MaxLogsPerBatch);
        }

        /// <summary>
        /// Validate that FailedToSendLog calls when channel is disabled
        /// </summary>
        [TestMethod]
        public async Task ChannelInvokesFailedToSendLogEventAfterDisabling()
        {
            _channel.SetEnabled(false);
            for (int i = 0; i < MaxLogsPerBatch; ++i)
            {
                await _channel.EnqueueAsync(new TestLog());
            }

            VerifySendingLog(MaxLogsPerBatch);
            VerifyFailedToSendLog(MaxLogsPerBatch);
        }

        /// <summary>
        /// Validate that all logs removed
        /// </summary>
        [TestMethod]
        public async Task TaskClearLogs()
        {
            await _channel.ShutdownAsync();
            await _channel.EnqueueAsync(new TestLog());

            await _channel.ClearAsync();
            _channel.SetEnabled(true);

            VerifySendingLog(0);
        }

        /// <summary>
        /// Validate that channel's mutex is disposed
        /// </summary>
        [TestMethod]
        public void DisposeChannelTest()
        {
            EnsureAllTasksAreFinishedInChannel();
            _channel.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(() => _channel.SetEnabled(true));
        }

        /// <summary>
        /// Validate that StorageException is processing without exception
        /// </summary>
        [TestMethod]
        public async Task ThrowStorageExceptionInDeleteLogsTime()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            var log = new TestLog();
            var storageException = new StorageException();

            // Make sure that the storage exception is "observed" to
            // avoid the exception propagating to the point where the
            // test fails.
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                if (e.Exception.InnerException == storageException)
                {
                    e.SetObserved();
                }
            };
            var storage = new Mock<IStorage>();
            storage.Setup(s => s.DeleteLogs(It.IsAny<string>(), It.IsAny<string>())).Throws(storageException);
            storage.Setup(s => s.GetLogsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<List<Log>>()))
                .Callback((string channelName, int limit, List<Log> logs) => logs.Add(log))
                .Returns(() => Task.FromResult("test-batch-id"));

            var appSecret = Guid.NewGuid().ToString();
            _channel = new Channel(ChannelName, 1, TimeSpan.FromSeconds(1), 1, appSecret, _mockIngestion.Object, storage.Object);
            SetupEventCallbacks();

            // Shutdown channel and store some log
            await _channel.ShutdownAsync();
            await _channel.EnqueueAsync(log);

            _channel.SetEnabled(true);

           VerifySentLog(1);

            // Not throw any exception
        }

        [TestMethod]
        public async Task MultiBatchTest()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            SetChannelWithTimeSpan(TimeSpan.Zero);
            
            var firstCalls = new[] { new ServiceCall(), new ServiceCall(), new ServiceCall() };
            var lastCall = new ServiceCall();
            var setup = _mockIngestion
                .SetupSequence(ingestion => ingestion.Call(
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<IList<Log>>()));
            foreach (var call in firstCalls)
            {
                setup.Returns(call);
            }
            setup.Returns(lastCall);
            
            // Send in separate batches
            await _channel.EnqueueAsync(new TestLog());
            VerifySendingLog(1);
            await _channel.EnqueueAsync(new TestLog());
            VerifySendingLog(1);
            await _channel.EnqueueAsync(new TestLog());
            VerifySendingLog(1);

            // An extra call is on hold as reaching maximum capacity of concurrent http calls.
            await _channel.EnqueueAsync(new TestLog());
            VerifySendingLog(0);

            // Complete all
            foreach (var call in firstCalls)
            {
                call.SetResult("test");
            }
            VerifySentLog(3);

            // Check sending all logs unlocks the last call.
            VerifySendingLog(1);
            lastCall.SetResult("test");
            VerifySentLog(1);
        }

        /// <summary>
        /// Verify recoverable http error
        /// </summary>
        [TestMethod]
        public async Task RecoverableHttpError()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            SetChannelWithTimeSpan(TimeSpan.Zero);

            // Enqueue some log and do not complete it
            var call = new ServiceCall();
            _mockIngestion
                .Setup(ingestion => ingestion.Call(
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<IList<Log>>()))
                .Returns((string appSecret, Guid installId, IList<Log> logs) => call);
            await _channel.EnqueueAsync(new TestLog());
            VerifySendingLog(1);
            
            // Next one will fail
            SetupIngestionCallFail(new RecoverableIngestionException());
            await _channel.EnqueueAsync(new TestLog());
            VerifySendingLog(1);

            // Wait up to 20 seconds for suspend to finish
            VerifyChannelDisable(TimeSpan.FromSeconds(10));
            _mockIngestion.Verify(ingestion => ingestion.Close(), Times.Never);
            VerifyFailedToSendLog(0);
            Assert.IsFalse(call.IsCompleted);
            Assert.IsFalse(call.IsCanceled);
        }

        /// <summary>
        /// Verify non-recoverable http error
        /// </summary>
        [TestMethod]
        public async Task NonRecoverableHttpError()
        {
            _mockIngestion.Setup(ingestion => ingestion.IsEnabled).Returns(true);
            SetChannelWithTimeSpan(TimeSpan.Zero);

            // Enqueue some log and do not complete it
            var call = new ServiceCall();
            _mockIngestion
                .Setup(ingestion => ingestion.Call(
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<IList<Log>>()))
                .Returns((string appSecret, Guid installId, IList<Log> logs) => call);
            await _channel.EnqueueAsync(new TestLog());
            VerifySendingLog(1);
            
            // Next one will fail
            SetupIngestionCallFail(new NonRecoverableIngestionException());
            await _channel.EnqueueAsync(new TestLog());
            VerifySendingLog(1);

            // Wait up to 20 seconds for suspend to finish
            VerifyChannelDisable(TimeSpan.FromSeconds(10));
            _mockIngestion.Verify(ingestion => ingestion.Close(), Times.Never);
            VerifyFailedToSendLog(2);

            // The first call is canceled
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => call.ToTask());
        }

        /// <summary>
        /// Verify that channel will not crash with StatefulMutexException when calling setEnabled(false) method during sending logs.
        /// </summary>
        [DataTestMethod]
        [DataRow(1)]
        [DataRow(4)]
        public async Task DisableChannelDuringSendingLogs(int countLogs)
        {
            try
            {
                // Prepare data.
                MockStorage storage = new MockStorage();
                var appSecret = Guid.NewGuid().ToString();
                Channel channel = new Channel(ChannelName, MaxLogsPerBatch, new TimeSpan(), MaxParallelBatches,
                    appSecret, _mockIngestion.Object, storage);
                SetupEventCallbacks();

                // Run methods in parallel
                for (int i = 0; i < countLogs; i++)
                {
                    channel.EnqueueAsync(new TestLog()).RunNotAsync();
                }
                channel.SetEnabled(false);

                // Wait when tasks will be finalized.
                await Task.Delay(1000);
            } 
            catch (StatefulMutexException)
            {
                // Crash test if was generated StatefulMutexException error.
                Assert.Fail();
            }
        }

        [TestMethod]
        public async Task HandleStorageExceptionWhenSendingLogsWithNullEvents()
        {
            try
            {
                var log = new TestLog();
                var storageException = new StorageException();

                // Make sure that the storage exception is "observed" to
                // avoid the exception propagating to the point where the
                // test fails.
                TaskScheduler.UnobservedTaskException += (sender, e) =>
                {
                    if (e.Exception.InnerException == storageException)
                    {
                        e.SetObserved();
                    }
                };
                var storage = new Mock<IStorage>();
                storage.Setup(s => s.DeleteLogs(It.IsAny<string>())).Throws(storageException);
                storage.Setup(s => s.GetLogsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<List<Log>>()))
                    .Callback((string channelName, int limit, List<Log> logs) => logs.Add(log))
                    .Returns(() => Task.FromResult("test-batch-id"));

                // Prepare data.
                var appSecret = Guid.NewGuid().ToString();
                Channel channel = new Channel(ChannelName, MaxLogsPerBatch, new TimeSpan(), MaxParallelBatches,
                    appSecret, _mockIngestion.Object, storage.Object);

                // Disable and enable channel.
                channel.SetEnabled(false);
                channel.SetEnabled(true);
            }
            catch (StorageException e)
            {
                // Crash test if was generated StorageException error.
                Assert.Fail();
            }
        }

        [TestMethod]
        public async Task HandleStorageExceptionWhenSendingLogsWithEvents()
        {
            try
            {
                var log = new TestLog();
                var storageException = new StorageException();

                // Make sure that the storage exception is "observed" to
                // avoid the exception propagating to the point where the
                // test fails.
                TaskScheduler.UnobservedTaskException += (sender, e) =>
                {
                    if (e.Exception.InnerException == storageException)
                    {
                        e.SetObserved();
                    }
                };
                var storage = new Mock<IStorage>();
                storage.Setup(s => s.DeleteLogs(It.IsAny<string>())).Throws(storageException);
                storage.Setup(s => s.GetLogsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<List<Log>>()))
                    .Callback((string channelName, int limit, List<Log> logs) => logs.Add(log))
                    .Returns(() => Task.FromResult("test-batch-id"));

                // Prepare data.
                var appSecret = Guid.NewGuid().ToString();
                Channel channel = new Channel(ChannelName, MaxLogsPerBatch, new TimeSpan(), MaxParallelBatches,
                    appSecret, _mockIngestion.Object, storage.Object);

                // Disable and enable channel.
                channel.FailedToSendLog += (sender, args) => { };
                channel.SetEnabled(false);
                channel.SetEnabled(true);
            }
            catch (StorageException e)
            {
                // Crash test if was generated StorageException error.
                Assert.Fail();
            }
        }

        [TestMethod]
        public async Task HandleStorageExceptionWhenClearLogs()
        {
            try
            {
                var log = new TestLog();
                var storageException = new StorageException();

                // Make sure that the storage exception is "observed" to
                // avoid the exception propagating to the point where the
                // test fails.
                TaskScheduler.UnobservedTaskException += (sender, e) =>
                {
                    if (e.Exception.InnerException == storageException)
                    {
                        e.SetObserved();
                    }
                };
                var storage = new Mock<IStorage>();
                storage.Setup(s => s.DeleteLogs(It.IsAny<string>())).Throws(storageException);
                storage.Setup(s => s.GetLogsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<List<Log>>()))
                    .Callback((string channelName, int limit, List<Log> logs) => logs.Add(log))
                    .Returns(() => Task.FromResult("test-batch-id"));

                // Prepare data.
                var appSecret = Guid.NewGuid().ToString();
                Channel channel = new Channel(ChannelName, MaxLogsPerBatch, new TimeSpan(), MaxParallelBatches,
                    appSecret, _mockIngestion.Object, storage.Object);
                SetupEventCallbacks();
                await channel.ClearAsync();
            }
            catch (StorageException e)
            {
                // Crash test if was generated StorageException error.
                Assert.Fail();
            }
        }

        private void SetChannelWithTimeSpan(TimeSpan timeSpan)
        {
            SetChannelWithTimeSpanAndIngestion(timeSpan, _mockIngestion.Object);
        }

        private void SetChannelWithTimeSpanAndIngestion(TimeSpan timeSpan, IIngestion ingestion)
        {
            _storage = new MockStorage();
            var appSecret = Guid.NewGuid().ToString();
            _channel = new Channel(ChannelName, MaxLogsPerBatch, timeSpan, MaxParallelBatches,
                appSecret, ingestion, _storage);
            SetupEventCallbacks();
        }

        private void EnsureAllTasksAreFinishedInChannel()
        {
            try
            {
                // We need to wait for a continuation task in Channel constructor 
                // to be executed so we wait until we acquire a lock to let this happen
                bool dummy = _channel.IsEnabled;
            }
            catch (ObjectDisposedException) { }
        }

        private void SetupIngestionCallFail(Exception exception)
        {
            _mockIngestion
                .Setup(ingestion => ingestion.Call(
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<IList<Log>>()))
                .Returns((string appSecret, Guid installId, IList<Log> logs) =>
                {
                    var call = new ServiceCall(appSecret, installId, logs);
                    call.SetException(exception);
                    return call;
                });
        }

        private void SetupIngestionCallSucceed(string result = "")
        {
            _mockIngestion
                .Setup(ingestion => ingestion.Call(
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<IList<Log>>()))
                .Returns((string appSecret, Guid installId, IList<Log> logs) =>
                {
                    var call = new ServiceCall(appSecret, installId, logs);
                    call.SetResult(result);
                    return call;
                });
        }

        private void SetupEventCallbacks()
        {
            _eventSemaphores = new List<SemaphoreSlim>
            {
                new SemaphoreSlim(0), new SemaphoreSlim(0), new SemaphoreSlim(0), new SemaphoreSlim(0), new SemaphoreSlim(0)
            };
            _channel.SendingLog += (sender, args) => { _eventSemaphores[SendingLogSemaphoreIdx].Release(); };
            _channel.SentLog += (sender, args) => { _eventSemaphores[SentLogSemaphoreIdx].Release(); };
            _channel.FailedToSendLog += (sender, args) => { _eventSemaphores[FailedToSendLogSemaphoreIdx].Release(); };
            _channel.EnqueuingLog += (sender, args) => { _eventSemaphores[EnqueuingLogSemaphoreIdx].Release(); };
            _channel.FilteringLog += (sender, args) => { _eventSemaphores[FilteringLogSemaphoreIdx].Release(); };
        }

        private void VerifySendingLog(int expectedTimes) =>
            Assert.AreEqual(expectedTimes, EventWithSemaphoreOccurred(_eventSemaphores[SendingLogSemaphoreIdx]),
                $"Failed on verify {nameof(Channel.SendingLog)} event call times");

        private void VerifySentLog(int expectedTimes) =>
            Assert.AreEqual(expectedTimes, EventWithSemaphoreOccurred(_eventSemaphores[SentLogSemaphoreIdx]),
                $"Failed on verify {nameof(Channel.SentLog)} event call times");

        private void VerifyFailedToSendLog(int expectedTimes) =>
            Assert.AreEqual(expectedTimes, EventWithSemaphoreOccurred(_eventSemaphores[FailedToSendLogSemaphoreIdx]),
                $"Failed on verify {nameof(Channel.FailedToSendLog)} event call times");

        private void VerifyEnqueuingLog(int expectedTimes) =>
            Assert.AreEqual(expectedTimes, EventWithSemaphoreOccurred(_eventSemaphores[EnqueuingLogSemaphoreIdx]),
                $"Failed on verify {nameof(Channel.EnqueuingLog)} event call times");

        private void VerifyFilteringLog(int expectedTimes) =>
            Assert.AreEqual(expectedTimes, EventWithSemaphoreOccurred(_eventSemaphores[FilteringLogSemaphoreIdx]),
                $"Failed on verify {nameof(Channel.FilteringLog)} event call times");

        private void VerifyChannelDisable(TimeSpan timeout)
        {
            var task = Task.Run(async () =>
            {
                while (_channel.IsEnabled)
                {
                    await Task.Delay(10);
                }
            });
            Assert.IsTrue(task.Wait(timeout));
        }

        private static int EventWithSemaphoreOccurred(SemaphoreSlim semaphore)
        {
            for (var i = 0;; ++i)
            {
                if (!semaphore.Wait(i == 0 ? 1000 : 100))
                {
                    return i;
                }
            }
        }
    }
}
