﻿using System;
using System.Threading;
using System.Net;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

using Oxide.Core.Plugins;

namespace Oxide.Core.Libraries
{
    /// <summary>
    /// The WebRequests library
    /// </summary>
    public class WebRequests : Library
    {
        /// <summary>
        /// Specifies the HTTP request timeout in seconds
        /// </summary>
        public static float Timeout = 30f;
        
        /// <summary>
        /// Represents a single WebRequest instance
        /// </summary>
        public class WebRequest
        {
            /// <summary>
            /// Gets the callback delegate
            /// </summary>
            public Action<int, string> Callback { get; private set; }

            /// <summary>
            /// Overrides the default request timeout
            /// </summary>
            public float Timeout { get; set; }

            /// <summary>
            /// Gets the web request method
            /// </summary>
            public string Method { get; set; }

            /// <summary>
            /// Gets the destination URL
            /// </summary>
            public string URL { get; private set; }
            
            /// <summary>
            /// Gets or sets the request body
            /// </summary>
            public string Body { get; set; }

            /// <summary>
            /// Gets the response code
            /// </summary>
            public int ResponseCode { get; protected set; }

            /// <summary>
            /// Gets the response text
            /// </summary>
            public string ResponseText { get; protected set; }

            /// <summary>
            /// Gets the plugin to which this web request belongs, if any
            /// </summary>
            public Plugin Owner { get; private set; }

            /// <summary>
            /// Gets the web request headers
            /// </summary>
            public Dictionary<string, string> RequestHeaders { get; set; }
            
            private HttpWebRequest request = null;

            /// <summary>
            /// Initializes a new instance of the WebRequest class
            /// </summary>
            /// <param name="url"></param>
            /// <param name="callback"></param>
            /// <param name="owner"></param>
            public WebRequest(string url, Action<int, string> callback, Plugin owner)
            {
                URL = url;
                Callback = callback;
                Owner = owner;
                if (owner != null) owner.OnRemovedFromManager += owner_OnRemovedFromManager;
            }
            
            /// <summary>
            /// Used by the worker thread to start the request
            /// </summary>
            public void Start()
            {   
                try
                {
                    // Create the request
                    request = (HttpWebRequest)System.Net.WebRequest.Create(URL);
                    request.Method = Method;
                    request.Credentials = CredentialCache.DefaultCredentials;
                    request.Proxy = null;
                    request.KeepAlive = false;
                    request.Timeout = (int)Math.Round((Timeout == 0f ? WebRequests.Timeout : Timeout) * 1000f);
                    request.ServicePoint.MaxIdleTime = request.Timeout;

                    if (RequestHeaders != null) request.SetRawHeaders(RequestHeaders);

                    // Optional request body for post requests
                    var data = new byte[0];
                    if (Body != null)
                    {
                        request.ContentType = "application/x-www-form-urlencoded";
                        request.ContentLength = data.Length;
                        data = Encoding.UTF8.GetBytes(Body);
                    }

                    // Perform DNS lookup and connect (blocking)
                    if (data.Length > 0)
                    {
                        request.BeginGetRequestStream(result =>
                        {
                            // Write request body
                            using (var stream = request.EndGetRequestStream(result))
                                stream.Write(data, 0, data.Length);
                            WaitForResponse();
                        }, null);
                    }
                    else
                    {
                        WaitForResponse();
                    }
                }
                catch (Exception ex)
                {
                    ResponseText = ex.Message;
                    Interface.Oxide.LogException(string.Format("Web request produced exception (Url: {0})", URL), ex);
                    if (request != null) request.Abort();
                }
            }

            private void WaitForResponse()
            {
                request.BeginGetResponse(result => {
                    try
                    {
                        using (var response = (HttpWebResponse)request.EndGetResponse(result))
                        {
                            using (var stream = response.GetResponseStream())
                                using (var reader = new StreamReader(stream))
                                    ResponseText = reader.ReadToEnd();
                            ResponseCode = (int)response.StatusCode;
                        }
                        OnComplete();
                    }
                    catch (Exception ex)
                    {
                        ResponseText = ex.Message;
                        Interface.Oxide.LogException(string.Format("Web request produced exception (Url: {0})", URL), ex);
                    }
                }, null);
            }

            private void OnComplete()
            {
                Interface.Oxide.NextTick(() =>
                {
                    try
                    {
                        Callback(ResponseCode, ResponseText);
                    }
                    catch (Exception ex)
                    {
                        Interface.Oxide.LogException("Exception raised in web request callback", ex);
                    }
                });
            }
            
            /// <summary>
            /// Called when the owner plugin was unloaded
            /// </summary>
            /// <param name="sender"></param>
            /// <param name="manager"></param>
            private void owner_OnRemovedFromManager(Plugin sender, PluginManager manager)
            {
                OnComplete();
            }
        }
        
        private readonly Queue<WebRequest> queue = new Queue<WebRequest>();
        private readonly object syncroot = new object();
        private readonly Thread workerthread;
        private readonly AutoResetEvent workevent = new AutoResetEvent(false);
        private bool shutdown;

        /// <summary>
        /// Initializes a new instance of the WebRequests library
        /// </summary>
        public WebRequests()
        {
            // Initialize SSL
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
            ServicePointManager.DefaultConnectionLimit = 200;

            // Start worker thread
            workerthread = new Thread(Worker);
            workerthread.Start();
        }

        /// <summary>
        /// Shuts down the worker thread
        /// </summary>
        public void Shutdown()
        {
            shutdown = true;
            workevent.Set();
            Thread.Sleep(250);
            workerthread.Abort();
        }

        /// <summary>
        /// The worker thread method
        /// </summary>
        private void Worker()
        {
            while (!shutdown)
            {
                if (queue.Count < 1)
                {
                    workevent.Reset();
                    workevent.WaitOne();
                }
                WebRequest request = null;
                lock (syncroot) request = queue.Dequeue();
                request.Start();
            }
        }

        /// <summary>
        /// Enqueues a get request
        /// </summary>
        /// <param name="url"></param>
        /// <param name="callback"></param>
        /// <param name="owner"></param>
        /// <param name="headers"></param>
        /// <param name="timeout"></param>
        [LibraryFunction("EnqueueGet")]
        public void EnqueueGet(string url, Action<int, string> callback, Plugin owner, Dictionary<string, string> headers = null, float timeout = 0f)
        {
            var request = new WebRequest(url, callback, owner) { Method = "GET", RequestHeaders = headers, Timeout = timeout };
            lock (syncroot) queue.Enqueue(request);
            workevent.Set();
        }

        /// <summary>
        /// Enqueues a post request
        /// </summary>
        /// <param name="url"></param>
        /// <param name="body"></param>
        /// <param name="callback"></param>
        /// <param name="owner"></param>
        /// <param name="headers"></param>
        /// <param name="timeout"></param>
        [LibraryFunction("EnqueuePost")]
        public void EnqueuePost(string url, string body, Action<int, string> callback, Plugin owner, Dictionary<string, string> headers = null, float timeout = 0f)
        {
            var request = new WebRequest(url, callback, owner) { Method = "POST", RequestHeaders = headers, Timeout = timeout };
            if (timeout > 0f) request.Timeout = timeout;
            request.Body = body;
            lock (syncroot) queue.Enqueue(request);
            workevent.Set();
        }

        /// <summary>
        /// Returns the current queue length
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetQueueLength")]
        public int GetQueueLength()
        {
            return queue.Count;
        }
    }

    // HttpWebRequest extensions to add raw header support
    public static class HttpWebRequestExtensions
    {
        /// <summary>
        /// Headers that require modification via a property
        /// </summary>
        private static readonly string[] RestrictedHeaders = {
            "Accept",
            "Connection",
            "Content-Length",
            "Content-Type",
            "Date",
            "Expect",
            "Host",
            "If-Modified-Since",
            "Keep-Alive",
            "Proxy-Connection",
            "Range",
            "Referer",
            "Transfer-Encoding",
            "User-Agent"
        };

        /// <summary>
        /// Dictionary of all of the header properties
        /// </summary>
        private static readonly Dictionary<string, PropertyInfo> HeaderProperties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initialize the HeaderProperties dictionary
        /// </summary>
        static HttpWebRequestExtensions()
        {
            var type = typeof(HttpWebRequest);
            foreach (var header in RestrictedHeaders)
                HeaderProperties[header] = type.GetProperty(header.Replace("-", ""));
        }

        /// <summary>
        /// Sets raw HTTP request headers
        /// </summary>
        /// <param name="request">Request object</param>
        /// <param name="headers">Dictionary of headers to set</param>
        public static void SetRawHeaders(this WebRequest request, Dictionary<string, string> headers)
        {
            foreach (var keyValPair in headers)
                request.SetRawHeader(keyValPair.Key, keyValPair.Value);
        }

        /// <summary>
        /// Sets a raw HTTP request header
        /// </summary>
        /// <param name="request">Request object</param>
        /// <param name="name">Name of the header</param>
        /// <param name="value">Value of the header</param>
        public static void SetRawHeader(this WebRequest request, string name, string value)
        {
            if (HeaderProperties.ContainsKey(name))
            {
                var property = HeaderProperties[name];
                if (property.PropertyType == typeof(DateTime))
                    property.SetValue(request, DateTime.Parse(value), null);
                else if (property.PropertyType == typeof(bool))
                    property.SetValue(request, Boolean.Parse(value), null);
                else if (property.PropertyType == typeof(long))
                    property.SetValue(request, Int64.Parse(value), null);
                else
                    property.SetValue(request, value, null);
            }
            else
            {
                request.Headers[name] = value;
            }
        }
    }
}
