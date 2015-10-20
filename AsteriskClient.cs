/* Copyright (C) 2013-2015, Manuel Meitinger
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

            public CookieWebClient(CookieContainer cookies)
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

        internal class AsyncResult : IAsyncResult
        {
            private readonly object syncRoot = new object();
            private readonly WebClient webClient;
            private readonly ManualResetEvent waitHandle;
            private bool cancelled;
            private Exception error;
            private string result;

            public AsyncResult(AsteriskClient client, AsteriskAction action, AsyncCallback callback, object state)
            {
                // create the instance and mark it as synchronous
                if (client == null)
                    throw ExceptionBuilder.NullArgument("client");
                if (action == null)
                    throw ExceptionBuilder.NullArgument("action");
                Client = client;
                Action = action;
                Callback = callback;
                AsyncState = state;
                webClient = new CookieWebClient(client.Cookies);
                waitHandle = new ManualResetEvent(false);
                try { ThreadPool.QueueUserWorkItem(BeginDownload); }
                catch
                {
                    webClient.Dispose();
                    waitHandle.Close();
                    throw;
                }
            }

            public object AsyncState { get; private set; }

            public WaitHandle AsyncWaitHandle { get { return waitHandle; } }

            public bool CompletedSynchronously { get; private set; }

            public bool IsCompleted { get; private set; }

            internal AsteriskClient Client { get; private set; }

            internal AsteriskAction Action { get; private set; }

            internal AsyncCallback Callback { get; private set; }

            private void BeginDownload(object unused)
            {
                // set the completed handler and start the download
                webClient.DownloadStringCompleted += EndDownload;
                try { webClient.DownloadStringAsync(new Uri(Client.BaseUri, Action.ToString()), null); }
                catch (Exception e)
                {
                    // remove the handler and fail
                    webClient.DownloadStringCompleted -= EndDownload;
                    Complete(e, false, null);
                }
            }

            private void EndDownload(object sender, DownloadStringCompletedEventArgs e)
            {
                // remove the handler and complete the task
                webClient.DownloadStringCompleted -= EndDownload;
                Complete(e.Error, e.Cancelled, e.Error == null && !e.Cancelled ? e.Result : null);
            }

            private void Complete(Exception error, bool cancelled, string result)
            {
                // store the result
                lock (syncRoot)
                {
                    // ensure that the op is not completed before
                    if (IsCompleted)
                        throw new InvalidOperationException();
                    IsCompleted = true;

                    // set the variables
                    this.error = error;
                    this.cancelled = cancelled;
                    this.result = result;

                    // signal the wait handle and the threads waiting for an end
                    waitHandle.Set();
                    Monitor.PulseAll(syncRoot);
                }

                // perform the callback if necessary
                if (Callback != null)
                    Callback(this);
            }

            public void Cancel()
            {
                webClient.CancelAsync();
            }

            public string GetResult()
            {
                lock (syncRoot)
                {
                    // wait for completion
                    while (!IsCompleted)
                        Monitor.Wait(syncRoot);

                    // close the handle and client
                    webClient.Dispose();
                    waitHandle.Close();
                }

                // return the value or the error that occurred
                if (error != null)
                    throw error;
                if (cancelled)
                    throw new OperationCanceledException();
                return result;
            }
        }

        /// <summary>
        /// Represents a parser for the raw AMI result.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        protected abstract class Parser<T>
        {
            /// <summary>
            /// Creates a new parser instance.
            /// </summary>
            /// <param name="action">The action whose results should be parsed.</param>
            /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
            public Parser(AsteriskAction action)
            {
                if (action == null)
                    throw ExceptionBuilder.NullArgument("action");
                Action = action;
            }

            internal AsteriskAction Action { get; private set; }

            /// <summary>
            /// Parses the result of an AMI action.
            /// </summary>
            /// <param name="s">The raw AMI result as <see cref="string"/>.</param>
            /// <returns>The properly typed result.</returns>
            /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
            public abstract T Parse(string s);

            internal string ExpectedResponse
            {
                get
                {
                    return
                        string.Equals(Action.Name, "Ping", StringComparison.OrdinalIgnoreCase) ? "Pong" :
                        string.Equals(Action.Name, "Logoff", StringComparison.OrdinalIgnoreCase) ? "Goodbye" :
                        "Success";
                }
            }
        }

        private class QueryParser : Parser<AsteriskResponse>
        {
            public QueryParser(AsteriskAction action) : base(action) { }

            public override AsteriskResponse Parse(string s)
            {
                return new AsteriskResponse(s, ExpectedResponse);
            }
        }

        private class NonQueryParser : Parser<bool>
        {
            public NonQueryParser(AsteriskAction action) : base(action) { }

            public override bool Parse(string s)
            {
                return new AsteriskResponse(s, ExpectedResponse) != null;
            }
        }

        private class ScalarParser : Parser<string>
        {
            public ScalarParser(AsteriskAction action, string valueName)
                : base(action)
            {
                if (valueName == null)
                    throw ExceptionBuilder.NullArgument("valueName");
                if (valueName.Length == 0)
                    throw ExceptionBuilder.EmptyArgument("valueName");
                ValueName = valueName;
            }

            public string ValueName { get; private set; }

            public override string Parse(string s)
            {
                return new AsteriskResponse(s, ExpectedResponse).Get(ValueName);
            }
        }

        private class EnumerationParser : Parser<AsteriskEnumeration>
        {
            public EnumerationParser(AsteriskAction action, string completeEventName)
                : base(action)
            {
                if (completeEventName == null)
                    throw ExceptionBuilder.NullArgument("completeEventName");
                if (completeEventName.Length == 0)
                    throw ExceptionBuilder.EmptyArgument("completeEventName");
                CompleteEventName = completeEventName;
            }

            public string CompleteEventName { get; private set; }

            public override AsteriskEnumeration Parse(string s)
            {
                return new AsteriskEnumeration(s, CompleteEventName);
            }
        }

        private readonly Dictionary<AsyncResult, object> asyncResults = new Dictionary<AsyncResult, object>();
        private bool disposed = false;

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
            BaseUri = baseUri;
            Cookies = new CookieContainer();
        }

        /// <summary>
        /// Calls <see cref="Dispose(bool)"/> with <c>disposing</c> set to <c>false</c>.
        /// </summary>
        ~AsteriskClient()
        {
            Dispose(false);
        }

        internal Uri BaseUri { get; private set; }

        internal CookieContainer Cookies { get; private set; }

        /// <summary>
        /// Ensure that the object isn't disposed.
        /// </summary>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        protected void CheckDisposed()
        {
            if (disposed)
                throw ExceptionBuilder.AsteriskClientAlreadyDisposed(BaseUri);
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
                    lock (asyncResults)
                        foreach (var asyncResult in asyncResults.Keys)
                            asyncResult.Cancel();
                }
                disposed = true;
            }
        }

        /// <summary>
        /// Disposes the <see cref="Aufbauwerk.Net.Asterisk.AsteriskClient"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Executes an AMI action.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="parser">A <see cref="Parser{T}"/> that converts the raw AMI result into type <typeparamref name="T"/>.</param>
        /// <returns>The parsed result.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="parser"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.Net.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        protected virtual T Execute<T>(Parser<T> parser)
        {
            // ensure the client is not disposed and execute the action
            if (parser == null)
                ExceptionBuilder.NullArgument("parser");
            CheckDisposed();
            using (var client = new CookieWebClient(Cookies))
                return parser.Parse(client.DownloadString(new Uri(BaseUri, parser.Action.ToString())));
        }

        /// <summary>
        /// Begins an asynchronous operation.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="parser">A <see cref="Parser{T}"/> that converts the raw AMI result into type <typeparamref name="T"/>.</param>
        /// <param name="callback">An optional asynchronous callback, to be called when the operation is complete.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous request from other requests.</param>
        /// <returns>An <see cref="System.IAsyncResult"/> that represents the asynchronous operation, which could still be pending.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="parser"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        protected virtual IAsyncResult BeginExecute<T>(Parser<T> parser, AsyncCallback callback, object state)
        {
            // ensure the client is not disposed and create the async result
            if (parser == null)
                ExceptionBuilder.NullArgument("parser");
            CheckDisposed();
            lock (asyncResults)
            {
                var asyncResult = new AsyncResult(this, parser.Action, callback, state);
                asyncResults.Add(asyncResult, parser);
                return asyncResult;
            }
        }

        /// <summary>
        /// Ends an asynchronous operation.
        /// </summary>
        /// <typeparam name="T">The parser of the operation.</typeparam>
        /// <typeparam name="U">The result type of the operation.</typeparam>
        /// <param name="asyncResult">A reference to the outstanding asynchronous AMI request.</param>
        /// <returns>The result of parser <typeparamref name="T"/>.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from a method with the same parser type <typeparamref name="T"/> on the current client.</exception>
        /// <exception cref="System.InvalidOperationException">The asynchronous operation was already ended.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.Net.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        protected virtual U EndExecute<T, U>(IAsyncResult asyncResult) where T : Parser<U>
        {
            // check the args and state
            if (asyncResult == null)
                throw ExceptionBuilder.NullArgument("asyncResult");
            if (asyncResult.GetType() != typeof(AsyncResult))
                throw ExceptionBuilder.AsteriskClientNotOwnerOfIAsyncResult("asyncResult");
            CheckDisposed();

            // get the parser
            object parser;
            lock (asyncResults)
            {
                if (!asyncResults.TryGetValue((AsyncResult)asyncResult, out parser))
                {
                    if (((AsyncResult)asyncResult).Client == this)
                        throw ExceptionBuilder.AsteriskClientAlreadyEndedIAsyncResult();
                    throw ExceptionBuilder.AsteriskClientNotOwnerOfIAsyncResult("asyncResult");
                }
                if (parser.GetType() != typeof(T))
                    throw ExceptionBuilder.AsteriskClientIncompatibleIAsyncResult("asyncResult");
                asyncResults.Remove((AsyncResult)asyncResult);
            }

            // get the result
            return ((T)parser).Parse(((AsyncResult)asyncResult).GetResult());
        }

        /// <summary>
        /// Cancels an asynchronous operation. 
        /// </summary>
        /// <param name="asyncResult">A reference to the outstanding asynchronous AMI request.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from this instance.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        protected virtual void CancelExecute(IAsyncResult asyncResult)
        {
            // check the args and state
            if (asyncResult == null)
                throw ExceptionBuilder.NullArgument("asyncResult");
            if (asyncResult.GetType() != typeof(AsyncResult))
                throw ExceptionBuilder.AsteriskClientNotOwnerOfIAsyncResult("asyncResult");
            CheckDisposed();

            // make sure the operation exists
            lock (asyncResults)
                if (!asyncResults.ContainsKey((AsyncResult)asyncResult))
                    throw ExceptionBuilder.AsteriskClientNotOwnerOfIAsyncResult("asyncResult");

            // cancel it
            ((AsyncResult)asyncResult).Cancel();
        }

        /// <summary>
        /// Executes a non-query operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.Net.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public void ExecuteNonQuery(AsteriskAction action)
        {
            Execute(new NonQueryParser(action));
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
            return BeginExecute(new NonQueryParser(action), callback, state);
        }

        /// <summary>
        /// Ends an asynchronous non-query operation.
        /// </summary>
        /// <param name="asyncResult">A reference to the outstanding asynchronous request.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from a <see cref="BeginExecuteNonQuery"/> method on the current client.</exception>
        /// <exception cref="System.InvalidOperationException">The asynchronous operation has already been ended.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.Net.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public void EndExecuteNonQuery(IAsyncResult asyncResult)
        {
            EndExecute<NonQueryParser, bool>(asyncResult);
        }

        /// <summary>
        /// Executes a scalar operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="valueName">The name of the value to return.</param>
        /// <returns>The scalar value.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.Net.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public string ExecuteScalar(AsteriskAction action, string valueName)
        {
            return Execute(new ScalarParser(action, valueName));
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
            return BeginExecute(new ScalarParser(action, valueName), callback, state);
        }

        /// <summary>
        /// Ends an asynchronous scalar operation.
        /// </summary>
        /// <param name="asyncResult">A reference to the outstanding asynchronous request.</param>
        /// <returns>The scalar value.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from a <see cref="BeginExecuteScalar"/> method on the current client.</exception>
        /// <exception cref="System.InvalidOperationException">The asynchronous operation has already been ended.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.Net.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public string EndExecuteScalar(IAsyncResult asyncResult)
        {
            return EndExecute<ScalarParser, string>(asyncResult);
        }

        /// <summary>
        /// Executes a query operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <returns>The response name-value pairs.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.Net.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public AsteriskResponse ExecuteQuery(AsteriskAction action)
        {
            return Execute(new QueryParser(action));
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
            return BeginExecute(new QueryParser(action), callback, state);
        }

        /// <summary>
        /// Ends an asynchronous query operation.
        /// </summary>
        /// <param name="asyncResult">A reference to the outstanding asynchronous request.</param>
        /// <returns>The response name-value pairs.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from a <see cref="BeginExecuteQuery"/> method on the current client.</exception>
        /// <exception cref="System.InvalidOperationException">The asynchronous operation has already been ended.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.Net.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public AsteriskResponse EndExecuteQuery(IAsyncResult asyncResult)
        {
            return EndExecute<QueryParser, AsteriskResponse>(asyncResult);
        }

        /// <summary>
        /// Executes an enumeration operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <returns>An <see cref="Aufbauwerk.Net.Asterisk.AsteriskEnumeration"/> instance.</returns>
        /// <remarks>This method assumes that the complete event name is the action name followed by <c>Complete</c>.</remarks>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.Net.WebException">An error occurred while querying the server.</exception>
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
        /// <exception cref="System.Net.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public AsteriskEnumeration ExecuteEnumeration(AsteriskAction action, string completeEventName)
        {
            return Execute(new EnumerationParser(action, completeEventName));
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
            return BeginExecute(new EnumerationParser(action, completeEventName), callback, state);
        }

        /// <summary>
        /// Ends an asynchronous enumeration operation.
        /// </summary>
        /// <param name="asyncResult">A reference to the outstanding asynchronous request.</param>
        /// <returns>An <see cref="Aufbauwerk.Net.Asterisk.AsteriskEnumeration"/> instance.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from calling <see cref="BeginExecuteEnumeration(Aufbauwerk.Net.Asterisk.AsteriskAction,string,System.AsyncCallback,object)"/> or <see cref="BeginExecuteEnumeration(Aufbauwerk.Net.Asterisk.AsteriskAction,string,System.AsyncCallback,object)"/> on the current client.</exception>
        /// <exception cref="System.InvalidOperationException">The asynchronous operation has already been ended.</exception>
        /// <exception cref="System.ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="System.Net.WebException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public AsteriskEnumeration EndExecuteEnumeration(IAsyncResult asyncResult)
        {
            return EndExecute<EnumerationParser, AsteriskEnumeration>(asyncResult);
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

        internal static InvalidOperationException AsteriskClientAlreadyEndedIAsyncResult()
        {
            return new InvalidOperationException(Res.GetString("AsteriskClientAlreadyEndedIAsyncResult"));
        }
    }

    /// <summary>
    /// Represents an Asterisk Manager action request.
    /// </summary>
    public sealed class AsteriskAction : System.Collections.IEnumerable
    {
        /// <summary>
        /// Gets the AMI action that was used to start an asynchronous AMI request.
        /// </summary>
        /// <param name="asyncResult">A reference to the outstanding asynchronous AMI request.</param>
        /// <returns>The <see cref="Aufbauwerk.Net.Asterisk.AsteriskAction"/> that was used to start the operation.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="asyncResult"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="asyncResult"/> did not originate from a <see cref="Aufbauwerk.Net.Asterisk.AsteriskClient"/>.</exception>
        public static AsteriskAction FromIAsyncResult(IAsyncResult asyncResult)
        {
            if (asyncResult == null)
                throw ExceptionBuilder.NullArgument("asyncResult");
            if (asyncResult.GetType() != typeof(AsteriskClient.AsyncResult))
                throw ExceptionBuilder.AsteriskClientNotOwnerOfIAsyncResult("asyncResult");
            return ((AsteriskClient.AsyncResult)asyncResult).Action;
        }

        private readonly StringBuilder queryBuilder = new StringBuilder("rawman?action=");
        private readonly NameValueCollection parameters = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
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

        /// <summary>
        /// Returns the entire <c>rawman</c> action URL.
        /// </summary>
        /// <returns>A relative URL.</returns>
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
            : base(StringComparer.OrdinalIgnoreCase)
        {
            // check the input
            if (input == null)
                throw ExceptionBuilder.NullArgument("input");
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

        /// <summary>
        /// Gets the value associated with the given key.
        /// </summary>
        /// <param name="name">The key of the entry that contains the value.</param>
        /// <returns>A <see cref="string"/> that contains the value.</returns>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">There are either no or multiple associated values.</exception>
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
                throw ExceptionBuilder.NullArgument("expectedResponseStatus");
            if (!string.Equals(Status, expectedResponseStatus, StringComparison.OrdinalIgnoreCase))
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
                throw ExceptionBuilder.NullArgument("input");
            if (expectedCompleteEventName == null)
                throw ExceptionBuilder.NullArgument("expectedCompleteEventName");

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
            if (!string.Equals(CompleteEvent.EventName, expectedCompleteEventName, StringComparison.OrdinalIgnoreCase))
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

        /// <summary>
        /// Returns an enumerator that iterates through the retrieved events.
        /// </summary>
        /// <returns>An enumerator that can be used to iterate through the returned events.</returns>
        public IEnumerator<AsteriskEvent> GetEnumerator() { return ((IEnumerable<AsteriskEvent>)events).GetEnumerator(); }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }

    /// <summary>
    /// Represents an Asterisk Manager error.
    /// </summary>
    [Serializable]
    public class AsteriskException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Aufbauwerk.Net.Asterisk.AsteriskException"/> class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public AsteriskException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Aufbauwerk.Net.Asterisk.AsteriskException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="inner">The exception that is the cause of the current exception, or a <c>null</c> reference if no inner exception is specified.</param>
        public AsteriskException(string message, Exception inner) : base(message, inner) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Aufbauwerk.Net.Asterisk.AsteriskException"/> class with serialized data.
        /// </summary>
        /// <param name="info">The <see cref="System.Runtime.Serialization.SerializationInfo"/> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context"></param>
        protected AsteriskException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
