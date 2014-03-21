/* Copyright (C) 2013-2014, Manuel Meitinger
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 2 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Resources;
using System.Text;
using System.Threading;

namespace Aufbauwerk.Net.Asterisk
{
    /// <summary>
    /// Represents a client for an Asterisk Manager Interface via HTTP.
    /// </summary>
    public class AsteriskClient : IDisposable
    {
        private class CookieWebClient : WebClient
        {
            private readonly CookieContainer cookies;

            internal CookieWebClient(CookieContainer cookies)
            {
                this.cookies = cookies;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                // set the cookies
                var result = (HttpWebRequest)base.GetWebRequest(address);
                result.CookieContainer = cookies;
                return result;
            }
        }

        private readonly HashSet<CookieWebClient> webClients = new HashSet<CookieWebClient>();
        private readonly CookieContainer cookies = new CookieContainer();
        private readonly Queue<WaitCallback> pendingOperations = new Queue<WaitCallback>();
        private bool disposed = false;
        private int pendingSyncOperations = 0;
        private readonly Uri baseUri;
        private int numberOfQueries = 3;

        protected class AsyncResult : IAsyncResult
        {
            private readonly object syncRoot = new object();
            private ManualResetEvent waitHandle;
            private bool cancelled;
            private Exception error;
            private string result;

            public AsyncResult(AsteriskClient client, AsteriskAction action)
            {
                // create the instance and mark it as synchronous
                if (client == null)
                    throw new ArgumentNullException("client");
                if (action == null)
                    throw ExceptionBuilder.NullArgument("action");
                CompletedSynchronously = true;
                Client = client;
                Address = new Uri(client.baseUri, action.ToString());
            }

            public IAsyncResult SetAsync(AsyncCallback callback, object state)
            {
                // check the current state and set the async state and variables
                lock (syncRoot)
                {
                    if (!CompletedSynchronously || IsCompleted)
                        throw new InvalidOperationException();
                    CompletedSynchronously = false;
                    Callback = callback;
                    AsyncState = state;
                    waitHandle = new ManualResetEvent(false);
                }

                // try to aquire a web client
                var webClient = Client.AcquireWebClient(BeginDownload);
                if (webClient != null)
                {
                    // schedule the download or release the client if that fails
                    try { ThreadPool.QueueUserWorkItem(BeginDownload, webClient); }
                    catch
                    {
                        Client.ReleaseWebClient(webClient);
                        throw;
                    }
                }
                return this;
            }

            public object AsyncState { get; private set; }

            public WaitHandle AsyncWaitHandle
            {
                get
                {
                    // only return a handle if the op is async and running
                    var handle = waitHandle;
                    if (handle == null)
                        throw new InvalidOperationException();
                    return handle;
                }
            }

            public bool CompletedSynchronously { get; private set; }

            public bool IsCompleted { get; private set; }

            public AsteriskClient Client { get; private set; }

            public Uri Address { get; private set; }

            public AsyncCallback Callback { get; private set; }

            private void BeginDownload(object state)
            {
                // set the completed handler and start the download
                var webClient = (CookieWebClient)state;
                webClient.DownloadStringCompleted += EndDownload;
                try { webClient.DownloadStringAsync(Address, null); }
                catch (Exception e)
                {
                    // remove the handler, release the client and fail
                    webClient.DownloadStringCompleted -= EndDownload;
                    Client.ReleaseWebClient(webClient);
                    EndAsync((ar, arg) => ar.error = arg, e);
                }
            }

            private void EndDownload(object sender, DownloadStringCompletedEventArgs e)
            {
                // remove the handler, release the client and complete the task
                var webClient = (CookieWebClient)sender;
                webClient.DownloadStringCompleted -= EndDownload;
                Client.ReleaseWebClient(webClient);
                EndAsync
                (
                    (ar, arg) =>
                    {
                        if (arg.Error != null)
                            ar.error = arg.Error;
                        else if (arg.Cancelled)
                            ar.cancelled = true;
                        else
                            ar.result = arg.Result;
                    },
                    e
                );
            }

            private void EndAsync<T>(Action<AsyncResult, T> applyResult, T result)
            {
                // store the result
                lock (syncRoot)
                {
                    // ensure that the op is async and not completed
                    if (CompletedSynchronously || IsCompleted)
                        throw new InvalidOperationException();

                    // set the variables
                    applyResult(this, result);
                    IsCompleted = true;

                    // signal the wait handle and the threads waiting for an end
                    waitHandle.Set();
                    Monitor.PulseAll(syncRoot);
                }

                // perform the callback if necessary
                if (Callback != null)
                    Callback(this);
            }

            public string Execute()
            {
                lock (syncRoot)
                {
                    // handle sync and async ops differently
                    if (CompletedSynchronously)
                    {
                        // perform the query synchronously if necessary
                        if (!IsCompleted)
                        {
                            var webClient = Client.AcquireWebClient(null);
                            try { result = webClient.DownloadString(Address); }
                            catch (OperationCanceledException) { cancelled = true; }
                            catch (Exception e) { error = e; }
                            Client.ReleaseWebClient(webClient);
                            IsCompleted = true;
                        }
                    }
                    else
                    {
                        // wait for completion
                        while (!IsCompleted)
                            Monitor.Wait(syncRoot);

                        // ensure that the function isn't called twice
                        if (waitHandle == null)
                            throw ExceptionBuilder.AsyncResultEndedTwice();

                        // close the handle
                        waitHandle.Close();
                        waitHandle = null;
                    }

                    // return the value or the error that occurred
                    if (error != null)
                        throw error;
                    if (cancelled)
                        throw new OperationCanceledException();
                    return result;
                }
            }
        }

        private class AsyncQueryResult : AsyncResult
        {
            private readonly string expectedResponse;

            public AsyncQueryResult(AsteriskClient client, AsteriskAction action)
                : base(client, action)
            {
                // set the expected result
                if (string.Equals(action.Name, "Ping", StringComparison.InvariantCultureIgnoreCase))
                    expectedResponse = "Pong";
                else if (string.Equals(action.Name, "Logoff", StringComparison.InvariantCultureIgnoreCase))
                    expectedResponse = "Goodbye";
                else
                    expectedResponse = "Success";
            }

            public new AsteriskResponse Execute()
            {
                return new AsteriskResponse(base.Execute(), expectedResponse);
            }
        }

        private class AsyncNonQueryResult : AsyncQueryResult
        {
            public AsyncNonQueryResult(AsteriskClient client, AsteriskAction action) : base(client, action) { }

            public new void Execute()
            {
                base.Execute();
            }
        }

        private class AsyncScalarResult : AsyncQueryResult
        {
            public AsyncScalarResult(AsteriskClient client, AsteriskAction action, string valueName) :
                base(client, action)
            {
                if (valueName == null)
                    throw ExceptionBuilder.NullArgument("valueName");
                if (valueName.Length == 0)
                    throw ExceptionBuilder.EmptyArgument("valueName");
                ValueName = valueName;
            }

            public string ValueName { get; private set; }

            public new string Execute()
            {
                return base.Execute().Get(ValueName);
            }
        }

        private class AsyncEnumerationResult : AsyncResult
        {
            public AsyncEnumerationResult(AsteriskClient client, AsteriskAction action, string completeEventName)
                : base(client, action)
            {
                if (completeEventName == null)
                    throw ExceptionBuilder.NullArgument("completeEventName");
                if (completeEventName.Length == 0)
                    throw ExceptionBuilder.EmptyArgument("completeEventName");
                CompleteEventName = completeEventName;
            }

            public string CompleteEventName { get; private set; }

            public new AsteriskEnumeration Execute()
            {
                return new AsteriskEnumeration(base.Execute(), CompleteEventName);
            }
        }

        /// <summary>
        /// Creates a new instance by building the base uri.
        /// </summary>
        /// <param name="host">The server name or IP.</param>
        /// <param name="port">The port on which the Asterisk micro webserver listens.</param>
        /// <param name="prefix">The prefix to the manager endpoints.</param>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="port"/> is less than -1 or greater than 65,535.</exception>
        /// <exception cref="System.UriFormatException">The URI constructed by the parameters is invalid.</exception>
        public AsteriskClient(string host, int port = 8088, string prefix = "asterisk")
            : this(new UriBuilder("http", host, port, prefix).Uri) { }

        /// <summary>
        /// Creates a new client instance.
        /// </summary>
        /// <param name="baseUri">The manager base address.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="baseUri"/> is <c>null</c>.</exception>
        public AsteriskClient(Uri baseUri)
        {
            if (baseUri == null)
                throw ExceptionBuilder.NullArgument("baseUri");
            this.baseUri = baseUri;
        }

        ~AsteriskClient()
        {
            Dispose(false);
        }

        /// <summary>
        /// Ensure that the object isn't disposed.
        /// </summary>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        protected void CheckDisposed()
        {
            if (disposed)
                throw ExceptionBuilder.AsteriskClientAlreadyDisposed(baseUri);
        }

        /// <summary>
        /// Disposes the client and cancels all pending queries.
        /// </summary>
        /// <param name="disposing"><c>true</c> if the client is explicitly disposed, <c>false</c> otherwise.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    lock (webClients)
                    {
                        foreach (var webClient in webClients)
                        {
                            try { webClient.CancelAsync(); }
                            catch { }
                        }
                    }
                }
                disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private CookieWebClient AcquireWebClient(WaitCallback asyncOperation)
        {
            // ensure that we're not disposed
            CheckDisposed();

            lock (webClients)
            {
                // handle the case where there are no more queries available
                if (webClients.Count >= numberOfQueries)
                {
                    // check if the call should block
                    if (asyncOperation == null)
                    {
                        // wait until there is something available
                        pendingSyncOperations++;
                        try
                        {
                            do { Monitor.Wait(webClients); }
                            while (webClients.Count >= numberOfQueries);
                        }
                        finally { pendingSyncOperations--; }
                    }
                    else
                    {
                        // queue the operation and return null
                        pendingOperations.Enqueue(asyncOperation);
                        return null;
                    }
                }

                // create the new client
                var webClient = new CookieWebClient(cookies);
                webClients.Add(webClient);
                return webClient;
            }
        }

        private void ReleaseWebClient(CookieWebClient client)
        {
            lock (webClients)
            {
                // return the client and call the assignment method
                if (!webClients.Remove(client))
                    throw new InvalidOperationException();
                AssignWebClientsWithinLock();
            }
        }

        private void AssignWebClientsWithinLock()
        {
            // ensure that there are vacancies
            var remainingAvailableClients = numberOfQueries - webClients.Count;
            if (remainingAvailableClients > 0)
            {
                // notify pending sync ops first
                if (pendingSyncOperations > 0)
                {
                    Monitor.PulseAll(webClients);
                    remainingAvailableClients -= pendingSyncOperations;
                }

                // start as many async ops as possible
                while (remainingAvailableClients-- > 0 && pendingOperations.Count > 0)
                {
                    // create and add the web client
                    var webClient = new CookieWebClient(cookies);
                    webClients.Add(webClient);
                    try
                    {
                        // dequeue the pending op and start it
                        var result = pendingOperations.Dequeue();
                        try { ThreadPool.QueueUserWorkItem(result, webClient); }
                        catch
                        {
                            // on error enqueue the op again
                            pendingOperations.Enqueue(result);
                            throw;
                        }
                    }
                    catch
                    {
                        // on error remove the client
                        webClients.Remove(webClient);
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Casts the given <see cref="System.IAsyncResult"/> into the requested type <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The type of operation.</typeparam>
        /// <param name="asyncResult">A reference to the outstanding asynchronous I/O request.</param>
        /// <returns>The corresponding instance of <typeparamref name="T"/>.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from a method returning <typeparamref name="T"/> on the current client.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        protected T GetAsync<T>(IAsyncResult asyncResult) where T : AsyncResult
        {
            if (asyncResult == null)
                throw ExceptionBuilder.NullArgument("asyncResult");
            if (asyncResult.GetType() != typeof(T))
                throw ExceptionBuilder.AsteriskClientIncompatibleIAsyncResult("asyncResult");
            var result = (T)asyncResult;
            if (result.Client != this)
                throw ExceptionBuilder.AsteriskClientNotOwnerOfIAsyncResult("asyncResult");
            CheckDisposed();
            return result;
        }

        /// <summary>
        /// Executes an operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <returns>The Asterisk Manager response.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.WebException">An error occurred while querying the server.</exception>
        public string Execute(AsteriskAction action)
        {
            return new AsyncResult(this, action).Execute();
        }

        /// <summary>
        /// Begins an asynchronous operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="callback">An optional asynchronous callback, to be called when the operation is complete.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous request from other requests.</param>
        /// <returns>An <see cref="System.IAsyncResult"/> that represents the asynchronous operation, which could still be pending.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        public IAsyncResult BeginExecute(AsteriskAction action, AsyncCallback callback, object state)
        {
            return new AsyncResult(this, action).SetAsync(callback, state);
        }

        /// <summary>
        /// Ends an asynchronous operation.
        /// </summary>
        /// <param name="asyncResult">A reference to the outstanding asynchronous request.</param>
        /// <returns>The Asterisk Manager response.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from a <see cref="BeginExecute"/> method on the current client.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.WebException">An error occurred while querying the server.</exception>
        public string EndExecute(IAsyncResult asyncResult)
        {
            return GetAsync<AsyncResult>(asyncResult).Execute();
        }

        /// <summary>
        /// Executes a non-query operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public void ExecuteNonQuery(AsteriskAction action)
        {
            new AsyncNonQueryResult(this, action).Execute();
        }

        /// <summary>
        /// Begins an asynchronous non-query operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="callback">An optional asynchronous callback, to be called when the operation is complete.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous request from other requests.</param>
        /// <returns>An <see cref="System.IAsyncResult"/> that represents the asynchronous operation, which could still be pending.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        public IAsyncResult BeginExecuteNonQuery(AsteriskAction action, AsyncCallback callback, object state)
        {
            return new AsyncNonQueryResult(this, action).SetAsync(callback, state);
        }

        /// <summary>
        /// Ends an asynchronous non-query operation.
        /// </summary>
        /// <param name="asyncResult">A reference to the outstanding asynchronous request.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from a <see cref="BeginExecuteNonQuery"/> method on the current client.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public void EndExecuteNonQuery(IAsyncResult asyncResult)
        {
            GetAsync<AsyncNonQueryResult>(asyncResult).Execute();
        }

        /// <summary>
        /// Executes a scalar operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <returns>The scalar value.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public string ExecuteScalar(AsteriskAction action, string valueName)
        {
            return new AsyncScalarResult(this, action, valueName).Execute();
        }

        /// <summary>
        /// Begins an asynchronous scalar operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="valueName">The name of the value to return.</param>
        /// <param name="callback">An optional asynchronous callback, to be called when the operation is complete.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous request from other requests.</param>
        /// <returns>An <see cref="System.IAsyncResult"/> that represents the asynchronous operation, which could still be pending.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> or <paramref name="valueName"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="valueName"/> is empty.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        public IAsyncResult BeginExecuteScalar(AsteriskAction action, string valueName, AsyncCallback callback, object state)
        {
            return new AsyncScalarResult(this, action, valueName).SetAsync(callback, state);
        }

        /// <summary>
        /// Ends an asynchronous scalar operation.
        /// </summary>
        /// <param name="asyncResult">A reference to the outstanding asynchronous request.</param>
        /// <returns>The scalar value.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from a <see cref="BeginExecuteScalar"/> method on the current client.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public string EndExecuteScalar(IAsyncResult asyncResult)
        {
            return GetAsync<AsyncScalarResult>(asyncResult).Execute();
        }

        /// <summary>
        /// Executes a query operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <returns>The response name-value pairs.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public AsteriskResponse ExecuteQuery(AsteriskAction action)
        {
            return new AsyncQueryResult(this, action).Execute();
        }

        /// <summary>
        /// Begins an asynchronous query operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="callback">An optional asynchronous callback, to be called when the operation is complete.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous request from other requests.</param>
        /// <returns>An <see cref="System.IAsyncResult"/> that represents the asynchronous operation, which could still be pending.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        public IAsyncResult BeginExecuteQuery(AsteriskAction action, AsyncCallback callback, object state)
        {
            return new AsyncQueryResult(this, action).SetAsync(callback, state);
        }

        /// <summary>
        /// Ends an asynchronous query operation.
        /// </summary>
        /// <param name="asyncResult">A reference to the outstanding asynchronous request.</param>
        /// <returns>The response name-value pairs.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from a <see cref="BeginExecuteQuery"/> method on the current client.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public AsteriskResponse EndExecuteQuery(IAsyncResult asyncResult)
        {
            return GetAsync<AsyncQueryResult>(asyncResult).Execute();
        }

        /// <summary>
        /// Executes an enumeration operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <returns>An <see cref="Aufbauwerk.Net.Asterisk.AsteriskEnumeration"/> instance.</returns>
        /// <remarks>This method assumes that the complete event name is the action name followed by <c>Complete</c>.</remarks>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public AsteriskEnumeration ExecuteEnumeration(AsteriskAction action)
        {
            return ExecuteEnumeration(action, action.Name + "Complete");
        }

        /// <summary>
        /// Executes an enumeration operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="completeEventName">The name of the complete name.</param>
        /// <returns>An <see cref="Aufbauwerk.Net.Asterisk.AsteriskEnumeration"/> instance.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> or <paramref name="completeEventName"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="completeEventName"/> is empty.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public AsteriskEnumeration ExecuteEnumeration(AsteriskAction action, string completeEventName)
        {
            return new AsyncEnumerationResult(this, action, completeEventName).Execute();
        }

        /// <summary>
        /// Begins an asynchronous enumeration operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="callback">An optional asynchronous callback, to be called when the operation is complete.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous request from other requests.</param>
        /// <returns>An <see cref="System.IAsyncResult"/> that represents the asynchronous operation, which could still be pending.</returns>
        /// <remarks>This method assumes that the complete event name is the action name followed by <c>Complete</c>.</remarks>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        public IAsyncResult BeginExecuteEnumeration(AsteriskAction action, AsyncCallback callback, object state)
        {
            return BeginExecuteEnumeration(action, action.Name + "Complete", callback, state);
        }

        /// <summary>
        /// Begins an asynchronous enumeration operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="completeEventName">The name of the complete name.</param>
        /// <param name="callback">An optional asynchronous callback, to be called when the operation is complete.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous request from other requests.</param>
        /// <returns>An <see cref="System.IAsyncResult"/> that represents the asynchronous operation, which could still be pending.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> or <paramref name="completeEventName"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="completeEventName"/> is empty.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        public IAsyncResult BeginExecuteEnumeration(AsteriskAction action, string completeEventName, AsyncCallback callback, object state)
        {
            return new AsyncEnumerationResult(this, action, completeEventName).SetAsync(callback, state);
        }

        /// <summary>
        /// Ends an asynchronous enumeration operation.
        /// </summary>
        /// <param name="asyncResult">A reference to the outstanding asynchronous request.</param>
        /// <returns>An <see cref="Aufbauwerk.Net.Asterisk.AsteriskEnumeration"/> instance.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from a <see cref="BeginExecuteEnumeration"/> method on the current client.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public AsteriskEnumeration EndExecuteEnumeration(IAsyncResult asyncResult)
        {
            return GetAsync<AsyncEnumerationResult>(asyncResult).Execute();
        }

        /// <summary>
        /// Gets or sets the number of simultaneous server queries.
        /// </summary>
        /// <exception cref="System.ArgumentOutOfRangeException">The value is less than 1.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        public int SimultaneousQueries
        {
            get
            {
                CheckDisposed();
                return numberOfQueries;
            }
            set
            {
                // check the input and if there's something to do
                if (value < 1)
                    throw ExceptionBuilder.AsteriskClientQueriesOutOfRange("value");
                CheckDisposed();
                if (numberOfQueries != value)
                {
                    lock (webClients)
                    {
                        if (numberOfQueries != value)
                        {
                            // apply the new limit and handle pending ops
                            numberOfQueries = value;
                            AssignWebClientsWithinLock();
                        }
                    }
                }
            }
        }
    }

    internal static class ExceptionBuilder
    {
        private static readonly ResourceManager Res = new ResourceManager(typeof(AsteriskClient));

        internal static ArgumentNullException NullArgument(string paramName)
        {
            return new ArgumentNullException(paramName, string.Format(Res.GetString("NullArgument"), paramName));
        }

        internal static ArgumentException EmptyArgument(string paramName)
        {
            return new ArgumentException(paramName, string.Format(Res.GetString("EmptyArgument"), paramName));
        }

        internal static AsteriskException ResultSetMultipleEncountered()
        {
            return new AsteriskException(Res.GetString("ResultSetMultipleEncountered"));
        }

        internal static AsteriskException ResultSetKeyNotFound(string name)
        {
            return new AsteriskException(string.Format(Res.GetString("ResultSetKeyNotFound"), name));
        }

        internal static AsteriskException ResultSetKeyNotUnique(string name)
        {
            return new AsteriskException(string.Format(Res.GetString("ResultSetKeyNotUnique"), name));
        }

        internal static AsteriskException ResponseUnexpected(string response, string expectedResponse, string message)
        {
            return new AsteriskException(string.Format(Res.GetString("ResponseUnexpected"), response, expectedResponse, message));

        }

        internal static AsteriskException EnumerationResponseMissing()
        {
            return new AsteriskException(Res.GetString("EnumerationResponseMissing"));
        }

        internal static AsteriskException EnumerationCompleteEventMissing()
        {
            return new AsteriskException(Res.GetString("EnumerationCompleteEventMissing"));
        }

        internal static InvalidOperationException AsyncResultEndedTwice()
        {
            return new InvalidOperationException(Res.GetString("AsyncResultEndedTwice"));
        }

        internal static ObjectDisposedException AsteriskClientAlreadyDisposed(Uri managerUri)
        {
            return new ObjectDisposedException(string.Format(Res.GetString("AsteriskClientFormatString"), managerUri), Res.GetString("AsteriskClientAlreadyDisposed"));
        }

        internal static ArgumentException AsteriskClientIncompatibleIAsyncResult(string paramName)
        {
            return new ArgumentException(paramName, string.Format(Res.GetString("AsteriskClientIncompatibleIAsyncResult"), paramName));
        }

        internal static ArgumentException AsteriskClientNotOwnerOfIAsyncResult(string paramName)
        {
            return new ArgumentException(paramName, string.Format(Res.GetString("AsteriskClientNotOwnerOfIAsyncResult"), paramName));
        }

        internal static ArgumentOutOfRangeException AsteriskClientQueriesOutOfRange(string paramName)
        {
            return new ArgumentOutOfRangeException(paramName, Res.GetString("AsteriskClientQueriesOutOfRange"));
        }
    }

    /// <summary>
    /// Represents an Asterisk Manager action request.
    /// </summary>
    public sealed class AsteriskAction : System.Collections.IEnumerable
    {
        private readonly StringBuilder queryBuilder = new StringBuilder("rawman?action=");
        private readonly NameValueCollection parameters = new NameValueCollection(StringComparer.InvariantCultureIgnoreCase);
        private string cachedQuery;

        private class ReadOnlyNameValueCollection : NameValueCollection
        {
            internal ReadOnlyNameValueCollection(NameValueCollection col)
                : base(col)
            {
                IsReadOnly = true;
            }
        }

        /// <summary>
        /// Creates a new action query definition.
        /// </summary>
        /// <param name="name">The name of the action.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="name"/> is empty.</exception>
        public AsteriskAction(string name)
        {
            // add the action param
            if (name == null)
                throw ExceptionBuilder.NullArgument("name");
            if (name.Length == 0)
                throw ExceptionBuilder.EmptyArgument("name");
            Name = name;
            queryBuilder.Append(Uri.EscapeUriString(name));
            cachedQuery = null;
        }

        /// <summary>
        /// Adds another parameter to the action.
        /// </summary>
        /// <param name="paramName">The parameter name.</param>
        /// <param name="paramValue">The value of the parameter.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="paramName"/> or <paramref name="paramValue"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="paramName"/> is empty.</exception>
        public void Add(string paramName, string paramValue)
        {
            // check, escape and add the param
            if (paramName == null)
                throw ExceptionBuilder.NullArgument("paramName");
            if (paramName.Length == 0)
                throw ExceptionBuilder.EmptyArgument("paramName");
            if (paramValue == null)
                throw ExceptionBuilder.NullArgument("paramValue");
            parameters.Add(paramName, paramValue);
            queryBuilder.Append('&');
            queryBuilder.Append(Uri.EscapeUriString(paramName));
            queryBuilder.Append('=');
            queryBuilder.Append(Uri.EscapeUriString(paramValue));
            cachedQuery = null;
        }

        /// <summary>
        /// Gets the name of this action.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Gets a read-only copy of the parameters.
        /// </summary>
        public NameValueCollection Parameters { get { return new ReadOnlyNameValueCollection(parameters); } }

        public override string ToString()
        {
            if (cachedQuery == null)
                cachedQuery = queryBuilder.ToString();
            return cachedQuery;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return parameters.GetEnumerator();
        }
    }

    /// <summary>
    /// A <see cref="System.Collections.Specialized.NameValueCollection"/> that behaves more like <see cref="System.Collections.IDictionary"/> and is read-only.
    /// </summary>
    public class AsteriskResultSet : NameValueCollection
    {
        private static readonly string[] LineSeparator = new string[] { "\r\n" };
        private static readonly char[] PartSeparator = new char[] { ':' };

        /// <summary>
        /// Creates a result set.
        /// </summary>
        /// <param name="input">The server response.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="input"/> is <c>null</c>.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response is invalid.</exception>
        public AsteriskResultSet(string input)
            : base(StringComparer.InvariantCultureIgnoreCase)
        {
            // check the input
            if (input == null)
                throw new ArgumentNullException("input");
            if (input.Contains("\n\r\n\r"))
                throw ExceptionBuilder.ResultSetMultipleEncountered();

            // split the lines
            var lines = input.Split(LineSeparator, StringSplitOptions.RemoveEmptyEntries);

            // add each name-value pair
            for (int i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split(PartSeparator, 2);
                if (parts.Length == 2)
                    Add(parts[0].Trim(), parts[1].Trim());
                else
                    Add(null, parts[0].Trim());
            }

            // don't allow further modifications
            IsReadOnly = true;
        }

        public override string Get(string name)
        {
            // ensure that there is one and only one value
            var values = base.GetValues(name);
            if (values == null || values.Length == 0)
                throw ExceptionBuilder.ResultSetKeyNotFound(name);
            if (values.Length > 1)
                throw ExceptionBuilder.ResultSetKeyNotUnique(name);
            return values[0];
        }

        /// <summary>
        /// Checks if the result set contains at least one value with the given key.
        /// </summary>
        /// <param name="key">The name of the key.</param>
        /// <returns><c>true</c> if the key exists, <c>false</c> otherwise.</returns>
        public bool Contains(string key)
        {
            var values = base.GetValues(key);
            return values != null && values.Length > 1;
        }
    }

    /// <summary>
    /// A result set with additional response metadata.
    /// </summary>
    public class AsteriskResponse : AsteriskResultSet
    {
        /// <summary>
        /// Creates a response result set.
        /// </summary>
        /// <param name="input">The server response.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="input"/> is <c>null</c>.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response is invalid.</exception>
        public AsteriskResponse(string input)
            : base(input)
        {
            // set the status and message
            Status = Get("Response");
            var messages = GetValues("Message");
            Message = messages == null || messages.Length == 0 ? null : string.Join(Environment.NewLine, messages);
        }

        /// <summary>
        /// Creates a new response result set and ensures that the status is as expected.
        /// </summary>
        /// <param name="input">The server response.</param>
        /// <param name="expectedResponseStatus">The expected status code.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="input"/> or <paramref name="expectedResponseStatus"/> is <c>null</c>.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response is invalid.</exception>
        public AsteriskResponse(string input, string expectedResponseStatus)
            : this(input)
        {
            // check the response status
            if (expectedResponseStatus == null)
                throw new ArgumentNullException("expectedResponseStatus");
            if (!string.Equals(Status, expectedResponseStatus, StringComparison.InvariantCultureIgnoreCase))
                throw ExceptionBuilder.ResponseUnexpected(Status, expectedResponseStatus, Message);
        }

        /// <summary>
        /// Gets the value of the response field, usually <c>Success</c> or <c>Error</c>.
        /// </summary>
        public string Status { get; private set; }

        /// <summary>
        /// Gets the optional status message.
        /// </summary>
        public string Message { get; private set; }
    }

    /// <summary>
    /// Represents an Asterisk Manager event.
    /// </summary>
    public class AsteriskEvent : AsteriskResultSet
    {
        /// <summary>
        /// Creates a new event description.
        /// </summary>
        /// <param name="input">The server response.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="input"/> is <c>null</c>.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response is invalid.</exception>
        public AsteriskEvent(string input)
            : base(input)
        {
            // get the event name
            EventName = Get("Event");
        }

        /// <summary>
        /// Gets the name of the current event.
        /// </summary>
        public string EventName { get; private set; }
    }

    /// <summary>
    /// A collection of events and metadata.
    /// </summary>
    public class AsteriskEnumeration : IEnumerable<AsteriskEvent>
    {
        private static readonly string[] ResultSetSeparator = new string[] { "\r\n\r\n" };

        private readonly AsteriskEvent[] events;

        /// <summary>
        /// Creates a new enumeration from a manager response.
        /// </summary>
        /// <param name="input">The server response.</param>
        /// <param name="expectedCompleteEventName">The name of the event that ends the enumeration.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="input"/> or <paramref name="expectedCompleteEventName"/> is <c>null</c>.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response is invalid.</exception>
        public AsteriskEnumeration(string input, string expectedCompleteEventName)
        {
            // check the input
            if (input == null)
                throw new ArgumentNullException("input");
            if (expectedCompleteEventName == null)
                throw new ArgumentNullException("expectedCompleteEventName");

            // split the events
            var items = input.Split(ResultSetSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length == 0)
                throw ExceptionBuilder.EnumerationResponseMissing();

            // get (and check) the response result set
            Response = new AsteriskResponse(items[0], "Success");

            // get the complete event
            if (items.Length == 1)
                throw ExceptionBuilder.EnumerationCompleteEventMissing();
            CompleteEvent = new AsteriskEvent(items[items.Length - 1]);
            if (!string.Equals(CompleteEvent.EventName, expectedCompleteEventName, StringComparison.InvariantCultureIgnoreCase))
                throw ExceptionBuilder.EnumerationCompleteEventMissing();

            // get the rest
            events = new AsteriskEvent[items.Length - 2];
            for (int i = 0; i < events.Length; i++)
                events[i] = new AsteriskEvent(items[i + 1]);
        }

        /// <summary>
        /// Gets the response that was sent before any event.
        /// </summary>
        public AsteriskResponse Response { get; private set; }

        /// <summary>
        /// Gets the event that was sent after the enumeration was complete.
        /// </summary>
        public AsteriskEvent CompleteEvent { get; private set; }

        /// <summary>
        /// Gets the event at a certain position within the enumeration.
        /// </summary>
        /// <param name="index">The offset within the enumeration.</param>
        /// <returns>The event description.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="index"/> is less than zero or equal to or greater than <see cref="Count"/>.</exception>
        public AsteriskEvent this[int index] { get { return events[index]; } }

        /// <summary>
        /// Gets the number of events that were returned by the Asterisk Manager, excluding <see cref="CompleteEvent"/>.
        /// </summary>
        public int Count { get { return events.Length; } }

        public IEnumerator<AsteriskEvent> GetEnumerator() { return ((IEnumerable<AsteriskEvent>)events).GetEnumerator(); }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }

    /// <summary>
    /// Represents an Asterisk Manager error.
    /// </summary>
    [Serializable]
    public class AsteriskException : Exception
    {
        public AsteriskException(string message) : base(message) { }
        public AsteriskException(string message, Exception inner) : base(message, inner) { }
        protected AsteriskException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
