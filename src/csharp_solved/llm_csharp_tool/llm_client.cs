// -----------------------------------------------------------------------------
// llm_client.cs
// -----------------------------------------------------------------------------
// C# 17 client library for the LingoFuse LLM services.
//
// This is the C# counterpart of llm_client.hpp / llm_client.cpp. It speaks
// the structured streaming protocol defined by llm_service.py / llm_proxy.py
// / llm_proxy_tool.py, and it uses the same JSON, framing, and callback
// semantics as the C++ client.
//
// Three backends, one protocol:
//   llm_service.py     (server_kind = "service")  local llama.cpp inference
//   llm_proxy.py       (server_kind = "proxy")    stateless HTTP forwarder
//   llm_proxy_tool.py  (server_kind = "proxy", tools=1)  LTB with tool exec
//
// This client works transparently against all three. All Call APIs behave
// identically EXCEPT SetSystemMessage(), which is unsupported on the two
// proxy siblings.
//
// Event dispatch model
// --------------------
// The LingoFuse runtime delivers notifications on a background worker
// thread. Three dispatch modes are available:
//
//   DispatchMode.MainThread  (DEFAULT)
//       The native callback copies the raw payload into an internal queue
//       and returns immediately. The user's main thread drains that queue
//       by calling PumpEvents(). Handlers run on the calling thread, one
//       at a time, in arrival order. This is the recommended mode for any
//       program that owns a main loop (a CLI, a GUI, an embedded host).
//
//   DispatchMode.Queued
//       Same as MainThread, except a dedicated dispatcher thread owned by
//       this Client drains the queue and invokes handlers. Use only when
//       the program has no main loop.
//
//   DispatchMode.Direct
//       Handlers are invoked directly on the LingoFuse notification thread.
//       Use only when you genuinely want notification-thread semantics.
//
// In all modes, ioLock is held during each handler invocation. Callers that
// produce output from a different thread should also acquire ioLock around
// their writes, so main-thread and handler output never interleave.
//
// Threading
// ---------
// Call API methods (Connect, Generate, CreateSession, ...) are synchronous
// and block the calling thread. Multiple Client instances may be used from
// different threads concurrently. A single Client instance is NOT designed
// for concurrent Call API invocations from multiple threads.
//
// JSON safety
// -----------
// All JSON I/O goes through LingoFuse.LfIo, which is the toolchain-wide
// JSON policy (System.Text.Json with UnsafeRelaxedJsonEscaping, plus the
// surrogate-pair rewrite for supplementary-plane characters). No structured
// JSON is ever built by hand-assembled strings.
//
// Dependencies
// ------------
//   LingoFuse binding  : Framework, AppHandle, DataHandle, LfIo, ...
//   System.Text.Json   : JsonNode-based request / response manipulation
// -----------------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

using LingoFuse;

namespace Llm
{
    // ========================================================================
    // Attachment records
    // ========================================================================

    /// <summary>
    /// One validated text attachment, ready to be encoded into a generate
    /// request's `attachments` array.
    /// </summary>
    public sealed class TextAttachment
    {
        public string Name { get; set; } = "";
        public string Mime { get; set; } = "";
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// One validated image attachment, ready to be encoded into a generate
    /// request's `attachments` array. `DataB64` is the base64 form of the raw
    /// image bytes; the server wraps it in a `data:&lt;mime&gt;;base64,...`
    /// URL before forwarding to the backend.
    /// </summary>
    public sealed class ImageAttachment
    {
        public string Name { get; set; } = "";
        public string Mime { get; set; } = "";
        public string DataB64 { get; set; } = "";
    }

    // ========================================================================
    // Dispatch mode
    // ========================================================================

    public enum DispatchMode
    {
        /// <summary>Handlers run on the thread that calls PumpEvents().</summary>
        MainThread,

        /// <summary>Handlers run on a dedicated dispatcher thread.</summary>
        Queued,

        /// <summary>Handlers run on the LingoFuse notification thread.</summary>
        Direct,
    }

    // ========================================================================
    // API name constants
    // ========================================================================

    public static class Api
    {
        public const string Stream = "llm_stream";
        public const string Generate = "generate";
        public const string CreateSession = "create_session";
        public const string CloseSession = "close_session";
        public const string CancelSession = "cancel_session";
        public const string ListSessions = "list_sessions";
        public const string SetSystemMessage = "set_system_message";
        public const string GetCapabilities = "get_api_capabilities";
        public const string Health = "health";
    }

    // ========================================================================
    // Attachment size limits (must match llm_common/attachments.py)
    // ========================================================================

    public static class AttachmentLimits
    {
        public const int MaxTextBytesPerFile = 256 * 1024;
        public const int MaxTextBytesTotal = 512 * 1024;
        public const int MaxImageB64PerFile = 8 * 1024 * 1024;
        public const int MaxImageB64Total = 16 * 1024 * 1024;
        public const int MaxNameLen = 256;
    }

    public static class AttachmentDefaults
    {
        public const string TextMime = "text/plain";
        public const string ImageMime = "image/png";
        public const string NamePrefix = "unnamed";
    }

    // ========================================================================
    // Client
    // ========================================================================

    /// <summary>
    /// High-level, event-driven client for the LingoFuse LLM services.
    /// </summary>
    public sealed class Client : IDisposable
    {
        // --------------------------------------------------------------------
        // Events
        // --------------------------------------------------------------------
        //
        // Handlers are invoked with (session_id, payload). In MainThread and
        // Queued modes they run on the caller's thread / the dispatcher
        // thread respectively, one at a time, in arrival order. In Direct
        // mode they run on the LingoFuse notification thread.
        //
        // Handlers MUST NOT call any Call API on the same Client (that would
        // deadlock, because the Call API blocks waiting for a finish event,
        // and the finish event is delivered through the same code path).

        public event Action<string, string>? OnChunk;   // (session_id, text)
        public event Action<string, string>? OnThink;   // (session_id, text)
        public event Action<string, string>? OnFinish;  // (session_id, reason)
        public event Action<string, string>? OnError;   // (session_id, message)
        public event Action<string, string>? OnClosed;  // (session_id, reason)

        // --------------------------------------------------------------------
        // Construction
        // --------------------------------------------------------------------

        public Client(string serverApp, string endpoint, int timeoutMs = 10000)
        {
            _serverApp = serverApp;
            _endpoint = endpoint;
            _timeoutMs = timeoutMs;
        }

        public void Dispose()
        {
            try { Disconnect(); } catch { /* best-effort */ }
            try { StopDispatcher(); } catch { /* best-effort */ }
            GC.SuppressFinalize(this);
        }

        // --------------------------------------------------------------------
        // State accessors
        // --------------------------------------------------------------------

        public bool Connected => _connected;
        public string ClientName => _clientName;
        public string ServerKind => _serverKind;
        public string CurrentSessionId => _currentSessionId;
        public DispatchMode DispatchMode => _dispatchMode;
        public string CapabilitiesJson => _capabilitiesRaw;
        public bool HasCapabilityInfo => _hasCapabilityInfo;

        public void SetCurrentSessionId(string id) => _currentSessionId = id;

        // --------------------------------------------------------------------
        // Dispatch mode
        // --------------------------------------------------------------------

        /// <summary>
        /// Set the dispatch mode. Must be called before Connect(). Changing
        /// the mode while connected is not supported; the previous mode is
        /// retained.
        /// </summary>
        public void SetDispatchMode(DispatchMode mode)
        {
            if (_connected) return;
            _dispatchMode = mode;

            if (mode == DispatchMode.Queued) StartDispatcher();
            else StopDispatcher();
        }

        /// <summary>
        /// Lock used to serialize handler output. In the default MainThread
        /// mode, handlers run on the same thread that called PumpEvents, so
        /// no external locking is needed. In Queued and Direct modes,
        /// callers that produce output from a different thread should
        /// acquire this lock around their writes.
        /// </summary>
        public object IoLock => _ioLock;

        // --------------------------------------------------------------------
        // Event pump
        // --------------------------------------------------------------------

        /// <summary>
        /// Drain and dispatch all pending events on the calling thread.
        ///
        /// MainThread mode: pushes raw payloads into an internal queue that
        /// this method drains, parses, and dispatches. Queued / Direct modes:
        /// no-op (the dispatcher or the native thread handles dispatch).
        ///
        /// If <paramref name="timeoutMs"/> is &gt; 0 and the queue is empty,
        /// the call blocks up to that many milliseconds waiting for the
        /// first event. After the first event arrives, the rest are drained
        /// without waiting.
        /// </summary>
        /// <returns>Number of events dispatched; 0 means nothing to do.</returns>
        public int PumpEvents(int timeoutMs = 0)
        {
            int count = 0;

            if (timeoutMs > 0)
            {
                if (!_queue.TryTake(out var first, timeoutMs))
                {
                    return 0;
                }
                lock (_ioLock) DispatchPayload(first);
                count++;
            }

            while (_queue.TryTake(out var payload, 0))
            {
                lock (_ioLock) DispatchPayload(payload);
                count++;
            }

            return count;
        }

        // --------------------------------------------------------------------
        // Lifecycle
        // --------------------------------------------------------------------

        public bool Connect(out string error)
        {
            error = "";
            if (_connected) return true;

            if (_prepared || _app != null)
            {
                CleanupPartialConnect(false);
            }

            try
            {
                Framework.ResetPrepare();
                Framework.SetOption("Wait_Connection_ReadyOk", "True");
                Framework.SetOption("Overlap_Connection", "True");

                if (Framework.PrepareClient(_endpoint, null) == -1)
                {
                    error = "LF_PrepareClient returned -1 for endpoint " + _endpoint;
                    CleanupPartialConnect(false);
                    return false;
                }

                if (Framework.PrepareDone() != 1)
                {
                    error = "LF_PrepareDone failed";
                    CleanupPartialConnect(false);
                    return false;
                }

                _prepared = true;

                _clientName = Framework.GenerateAppName();
                if (string.IsNullOrEmpty(_clientName))
                {
                    error = "GenerateAppName returned an empty string";
                    CleanupPartialConnect(true);
                    return false;
                }

                _app = new AppHandle(_clientName, "C# LLM Client");

                if (!_app.RegisterNotify(Api.Stream, "LLM stream callback",
                                         OnNotifyCallback))
                {
                    error = "failed to register notify callback for " + Api.Stream;
                    CleanupPartialConnect(true);
                    return false;
                }

                int bound = _app.Bind();
                if (bound == 0)
                {
                    error = "LF_BindApp returned 0 (no free client available)";
                    CleanupPartialConnect(true);
                    return false;
                }

                _connected = true;

                // Best-effort capability probe. A server older than the
                // capability API returns a non-zero code and the cache stays
                // empty; callers then treat capability queries as
                // "unsupported" and behave conservatively.
                if (!GetApiCapabilities(out _, out var capsErr))
                {
                    Console.Error.WriteLine(
                        "[llm_client] capability probe failed: " + capsErr);
                }

                Console.Error.WriteLine(
                    $"[llm_client] connected: client={_clientName}, " +
                    $"kind={_serverKind}");
                return true;
            }
            catch (Exception ex)
            {
                error = "unexpected exception in Connect: " + ex.Message;
                CleanupPartialConnect(_prepared);
                return false;
            }
        }

        public void Disconnect()
        {
            if (!_prepared && !_connected && _app == null) return;
            Console.Error.WriteLine("[llm_client] disconnecting");
            CleanupPartialConnect(true);
        }

        // --------------------------------------------------------------------
        // Session management
        // --------------------------------------------------------------------

        public bool CreateSession(out string sessionId, out string error)
            => CreateSession("", out sessionId, out error);

        public bool CreateSession(string systemMessage,
                                  out string sessionId,
                                  out string error)
        {
            sessionId = "";
            error = "";

            if (!_connected)
            {
                error = "not connected to LingoFuse service";
                return false;
            }

            try
            {
                var req = new JsonObject { ["client_name"] = _clientName };
                if (!string.IsNullOrEmpty(systemMessage))
                {
                    req["system_message"] = systemMessage;
                }

                if (!CallApi(Api.CreateSession, req.ToJsonString(),
                             out var respJson, out error))
                {
                    return false;
                }

                if (!TryParseResponse(respJson, out var resp, out error)) return false;

                int code = GetInt(resp, "code", -1);
                if (code != 0)
                {
                    error = GetString(resp, "error") ?? "create_session failed";
                    return false;
                }

                string newId = GetString(resp, "session_id") ?? "";
                if (string.IsNullOrEmpty(newId))
                {
                    error = "server did not return session_id";
                    return false;
                }

                sessionId = newId;
                _currentSessionId = newId;
                Console.Error.WriteLine(
                    $"[llm_client] session created: {newId} " +
                    $"(system_message={systemMessage.Length} chars)");
                return true;
            }
            catch (Exception ex)
            {
                error = "CreateSession: " + ex.Message;
                return false;
            }
        }

        public bool CloseSession(string sessionId, bool cancelRunning, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(sessionId))
            {
                error = "CloseSession: empty session_id";
                return false;
            }

            try
            {
                var req = new JsonObject
                {
                    ["session_id"] = sessionId,
                    ["cancel_running"] = cancelRunning,
                };

                if (!CallApi(Api.CloseSession, req.ToJsonString(),
                             out var respJson, out error))
                {
                    return false;
                }

                if (!TryParseResponse(respJson, out var resp, out error)) return false;

                int code = GetInt(resp, "code", -1);
                if (code != 0)
                {
                    error = GetString(resp, "error") ?? "close_session failed";
                    return false;
                }

                if (_currentSessionId == sessionId) _currentSessionId = "";
                Console.Error.WriteLine($"[llm_client] session closed: {sessionId}");
                return true;
            }
            catch (Exception ex)
            {
                error = "CloseSession: " + ex.Message;
                return false;
            }
        }

        public bool CancelSession(string sessionId, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(sessionId))
            {
                error = "CancelSession: empty session_id";
                return false;
            }

            try
            {
                var req = new JsonObject { ["session_id"] = sessionId };

                if (!CallApi(Api.CancelSession, req.ToJsonString(),
                             out var respJson, out error))
                {
                    return false;
                }

                if (!TryParseResponse(respJson, out var resp, out error)) return false;

                int code = GetInt(resp, "code", -1);
                if (code != 0)
                {
                    error = GetString(resp, "error") ?? "cancel_session failed";
                    return false;
                }

                Console.Error.WriteLine(
                    $"[llm_client] cancel requested for session: {sessionId}");
                return true;
            }
            catch (Exception ex)
            {
                error = "CancelSession: " + ex.Message;
                return false;
            }
        }

        public bool ListSessions(out string sessionsJson, out string error)
        {
            sessionsJson = "";
            error = "";

            try
            {
                var req = new JsonObject { ["client_name"] = _clientName };

                if (!CallApi(Api.ListSessions, req.ToJsonString(),
                             out var respJson, out error))
                {
                    return false;
                }

                sessionsJson = respJson;
                return true;
            }
            catch (Exception ex)
            {
                error = "ListSessions: " + ex.Message;
                return false;
            }
        }

        // --------------------------------------------------------------------
        // Generate (unified entry points)
        // --------------------------------------------------------------------

        public bool Generate(string content, string prompt,
                             ref string sessionId, out string error)
        {
            return SendGenerateCombined(content, prompt,
                Array.Empty<TextAttachment>(),
                Array.Empty<ImageAttachment>(),
                "", ref sessionId, out error);
        }

        public bool GenerateWithAttachments(string content, string prompt,
                                            IReadOnlyList<TextAttachment> texts,
                                            IReadOnlyList<ImageAttachment> images,
                                            ref string sessionId, out string error)
        {
            return SendGenerateCombined(content, prompt, texts, images,
                "", ref sessionId, out error);
        }

        public bool GenerateWithTextFile(string content, string prompt,
                                         string filePath,
                                         ref string sessionId, out string error)
        {
            if (!BuildTextAttachmentFromFile(filePath, out var att, out error))
            {
                return false;
            }
            return GenerateWithAttachments(content, prompt,
                new[] { att }, Array.Empty<ImageAttachment>(),
                ref sessionId, out error);
        }

        public bool GenerateWithImageFile(string content, string prompt,
                                          string filePath,
                                          ref string sessionId, out string error)
        {
            if (!BuildImageAttachmentFromFile(filePath, out var att, out error))
            {
                return false;
            }
            return GenerateWithAttachments(content, prompt,
                Array.Empty<TextAttachment>(), new[] { att },
                ref sessionId, out error);
        }

        public bool GenerateCurrent(string content, string prompt, out string error)
        {
            string sid = _currentSessionId;
            return Generate(content, prompt, ref sid, out error);
        }

        // --------------------------------------------------------------------
        // Structured Output
        // --------------------------------------------------------------------

        public bool GenerateStructured(string content, string prompt,
                                       string responseFormatJson,
                                       ref string sessionId, out string error)
        {
            if (string.IsNullOrEmpty(responseFormatJson))
            {
                error = "GenerateStructured: empty response_format";
                return false;
            }
            return SendGenerateCombined(content, prompt,
                Array.Empty<TextAttachment>(),
                Array.Empty<ImageAttachment>(),
                responseFormatJson, ref sessionId, out error);
        }

        public bool GenerateWithJsonSchema(string content, string prompt,
                                           string schemaName, string schemaJson,
                                           bool strict,
                                           ref string sessionId, out string error)
        {
            string envelope = BuildSchemaResponseFormat(schemaName, schemaJson,
                strict, out error);
            if (string.IsNullOrEmpty(envelope)) return false;

            return SendGenerateCombined(content, prompt,
                Array.Empty<TextAttachment>(),
                Array.Empty<ImageAttachment>(),
                envelope, ref sessionId, out error);
        }

        public bool GenerateWithImageFileAndSchema(string content, string prompt,
                                                   string filePath,
                                                   string schemaName, string schemaJson,
                                                   bool strict,
                                                   ref string sessionId, out string error)
        {
            string envelope = BuildSchemaResponseFormat(schemaName, schemaJson,
                strict, out error);
            if (string.IsNullOrEmpty(envelope)) return false;

            if (!BuildImageAttachmentFromFile(filePath, out var att, out error))
            {
                return false;
            }
            return SendGenerateCombined(content, prompt,
                Array.Empty<TextAttachment>(), new[] { att },
                envelope, ref sessionId, out error);
        }

        public bool GenerateWithAttachmentsAndSchema(string content, string prompt,
                                                     IReadOnlyList<TextAttachment> texts,
                                                     IReadOnlyList<ImageAttachment> images,
                                                     string schemaName, string schemaJson,
                                                     bool strict,
                                                     ref string sessionId, out string error)
        {
            string envelope = BuildSchemaResponseFormat(schemaName, schemaJson,
                strict, out error);
            if (string.IsNullOrEmpty(envelope)) return false;

            return SendGenerateCombined(content, prompt, texts, images,
                envelope, ref sessionId, out error);
        }

        // --------------------------------------------------------------------
        // Server-wide settings
        // --------------------------------------------------------------------

        public bool SetSystemMessage(string message, out string error)
        {
            error = "";

            if (_hasCapabilityInfo && !LlmSupported(Api.SetSystemMessage))
            {
                string kind = string.IsNullOrEmpty(_serverKind)
                    ? "unknown" : _serverKind;
                error = "set_system_message is not supported by this server " +
                        "(kind=" + kind + "). The system message is fixed at " +
                        "session creation time. Pass it to CreateSession, or " +
                        "close the current session and create a new one with " +
                        "the desired system message.";
                return false;
            }

            try
            {
                var req = new JsonObject { ["content"] = message };

                if (!CallApi(Api.SetSystemMessage, req.ToJsonString(),
                             out var respJson, out error))
                {
                    return false;
                }

                if (!TryParseResponse(respJson, out var resp, out error)) return false;

                int code = GetInt(resp, "code", -1);
                if (code != 0)
                {
                    error = GetString(resp, "error") ?? "set_system_message failed";
                    return false;
                }

                Console.Error.WriteLine("[llm_client] global system message updated");
                return true;
            }
            catch (Exception ex)
            {
                error = "SetSystemMessage: " + ex.Message;
                return false;
            }
        }

        public bool Health(out string healthJson, out string error)
        {
            healthJson = "";
            error = "";

            try
            {
                if (!CallApi(Api.Health, "{}", out var respJson, out error))
                {
                    return false;
                }

                if (!TryParseResponse(respJson, out var resp, out error)) return false;

                int code = GetInt(resp, "code", -1);
                if (code != 0)
                {
                    error = GetString(resp, "error") ?? "health failed";
                    return false;
                }

                healthJson = respJson;
                return true;
            }
            catch (Exception ex)
            {
                error = "Health: " + ex.Message;
                return false;
            }
        }

        // --------------------------------------------------------------------
        // Capability discovery
        // --------------------------------------------------------------------

        public bool GetApiCapabilities(out string capabilitiesJson, out string error)
        {
            capabilitiesJson = "";
            error = "";

            ResetCapabilityState();

            try
            {
                if (!CallApi(Api.GetCapabilities, "{}", out var respJson, out error))
                {
                    return false;
                }

                if (!TryParseResponse(respJson, out var resp, out error)) return false;

                int code = GetInt(resp, "code", -1);
                if (code != 0)
                {
                    error = GetString(resp, "error") ?? "get_api_capabilities failed";
                    return false;
                }

                if (resp["capabilities"] is not JsonObject caps)
                {
                    error = "get_api_capabilities response missing 'capabilities' object";
                    return false;
                }

                _capabilities = (JsonObject)caps.DeepClone();
                _serverKind = GetString(resp, "server_kind") ?? "";
                _capabilitiesRaw = respJson;
                _hasCapabilityInfo = true;

                capabilitiesJson = respJson;
                return true;
            }
            catch (Exception ex)
            {
                error = "GetApiCapabilities: " + ex.Message;
                return false;
            }
        }

        public bool LlmSupported(string apiName)
        {
            if (!_hasCapabilityInfo) return false;
            if (_capabilities == null) return false;
            if (!_capabilities.TryGetPropertyValue(apiName, out var v) || v == null)
            {
                return false;
            }
            try { return v.GetValue<int>() == 1; }
            catch { return false; }
        }

        public bool IsToolBridge()
            => LlmSupported("tools") && LlmSupported("tool_calls");

        public bool HasVision() => LlmSupported("vision");
        public bool HasAttachments() => LlmSupported("attachments");

        // --------------------------------------------------------------------
        // Last-exception accessor (diagnostic)
        // --------------------------------------------------------------------

        public string TakeLastException()
        {
            lock (_exceptionLock)
            {
                string s = _lastException;
                _lastException = "";
                return s;
            }
        }

        // ====================================================================
        // Internal: dispatcher
        // ====================================================================

        private void StartDispatcher()
        {
            if (_dispatcherThread != null) return;

            _dispatcherCts = new CancellationTokenSource();
            var token = _dispatcherCts.Token;
            _dispatcherThread = new Thread(() => DispatcherLoop(token))
            {
                IsBackground = true,
                Name = "llm-csharp-dispatcher",
            };
            _dispatcherThread.Start();
        }

        private void StopDispatcher()
        {
            if (_dispatcherThread == null) return;
            try { _dispatcherCts?.Cancel(); } catch { }
            try { _dispatcherThread.Join(2000); } catch { }
            _dispatcherThread = null;
            try { _dispatcherCts?.Dispose(); } catch { }
            _dispatcherCts = null;
        }

        private void DispatcherLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!_queue.TryTake(out var payload, 100)) continue;
                if (token.IsCancellationRequested) break;
                lock (_ioLock) DispatchPayload(payload);
            }
        }

        // ====================================================================
        // Internal: notification callback
        // ====================================================================

        private void OnNotifyCallback(DataHandle input)
        {
            if (input == null) return;

            string payload;
            try
            {
                payload = LfIo.ReadString(input);
            }
            catch (Exception ex)
            {
                lock (_exceptionLock)
                {
                    _lastException = "notify read: " + ex.Message;
                }
                return;
            }

            if (string.IsNullOrEmpty(payload)) return;

            if (_dispatchMode == DispatchMode.Direct)
            {
                lock (_ioLock) DispatchPayload(payload);
            }
            else
            {
                // MainThread and Queued both enqueue. In MainThread mode the
                // user's main thread drains via PumpEvents(). In Queued mode
                // the dispatcher thread drains.
                try { _queue.Add(payload); }
                catch (InvalidOperationException) { /* shutting down */ }
            }
        }

        private void DispatchPayload(string payload)
        {
            try
            {
                if (JsonNode.Parse(payload) is not JsonObject j) return;
                if (!j.TryGetPropertyValue("type", out var typeNode)) return;

                string msgType = typeNode?.GetValue<string>() ?? "";
                string sessionId = GetString(j, "session_id") ?? "";

                switch (msgType)
                {
                    case "chunk":
                        {
                            string text = GetString(j, "text") ?? "";
                            if (!string.IsNullOrEmpty(text)) OnChunk?.Invoke(sessionId, text);
                            break;
                        }
                    case "think":
                        {
                            string text = GetString(j, "text") ?? "";
                            if (!string.IsNullOrEmpty(text)) OnThink?.Invoke(sessionId, text);
                            break;
                        }
                    case "finish":
                        {
                            string reason = GetString(j, "reason") ?? "";
                            OnFinish?.Invoke(sessionId, reason);
                            break;
                        }
                    case "error":
                        {
                            string msg = GetString(j, "message") ?? "";
                            OnError?.Invoke(sessionId, msg);
                            break;
                        }
                    case "closed":
                        {
                            string reason = GetString(j, "reason") ?? "";
                            OnClosed?.Invoke(sessionId, reason);
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                lock (_exceptionLock)
                {
                    _lastException = "dispatch: " + ex.Message;
                }
            }
        }

        // ====================================================================
        // Internal: low-level Call API
        // ====================================================================

        private bool CallApi(string apiName, string requestJson,
                             out string responseJson, out string error)
        {
            responseJson = "";
            error = "";

            if (!_connected)
            {
                error = "not connected to LingoFuse service";
                return false;
            }

            try
            {
                using var param = new DataHandle(apiName);
                LfIo.WriteString(param, requestJson);

                DataHandle? raw = null;
                try
                {
                    raw = Framework.Call(_serverApp, param, (ulong)_timeoutMs);
                }
                catch (Exception ex)
                {
                    error = $"LF_Call({apiName}) threw: {ex.Message}";
                    return false;
                }

                if (raw == null || !raw.IsValid)
                {
                    raw?.Dispose();
                    error = "LF_Call returned a null handle for API " + apiName;
                    return false;
                }

                using (raw)
                {
                    if (raw.Size == 0)
                    {
                        error = "empty response from API " + apiName +
                                " (timeout or target unreachable)";
                        return false;
                    }

                    raw.Position = 0;
                    responseJson = LfIo.ReadString(raw);
                }

                if (string.IsNullOrEmpty(responseJson))
                {
                    error = "empty response payload from API " + apiName;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"CallApi({apiName}): {ex.Message}";
                return false;
            }
        }

        // ====================================================================
        // Internal: generate pipeline
        // ====================================================================

        private bool SendGenerateCombined(string content, string prompt,
                                          IReadOnlyList<TextAttachment> texts,
                                          IReadOnlyList<ImageAttachment> images,
                                          string responseFormatJson,
                                          ref string sessionId,
                                          out string error)
        {
            error = "";

            if (!_connected)
            {
                error = "not connected to LingoFuse service";
                return false;
            }

            try
            {
                var req = new JsonObject
                {
                    ["content"] = content,
                    ["prompt"] = prompt,
                };

                if (!string.IsNullOrEmpty(sessionId))
                    req["session_id"] = sessionId;
                else if (!string.IsNullOrEmpty(_currentSessionId))
                    req["session_id"] = _currentSessionId;
                else
                    req["client_name"] = _clientName;

                if (texts.Count > 0 || images.Count > 0)
                {
                    if (!PopulateAttachments(texts, images,
                                             out var attachmentsArray, out error))
                    {
                        return false;
                    }
                    req["attachments"] = attachmentsArray;
                }

                if (!string.IsNullOrEmpty(responseFormatJson))
                {
                    var rf = JsonNode.Parse(responseFormatJson);
                    req["options"] = new JsonObject { ["response_format"] = rf };
                }

                if (!CallApi(Api.Generate, req.ToJsonString(),
                             out var respJson, out error))
                {
                    return false;
                }

                if (!TryParseResponse(respJson, out var resp, out error)) return false;

                int code = GetInt(resp, "code", -1);
                if (code != 0)
                {
                    error = GetString(resp, "error") ?? "generate failed";
                    return false;
                }

                string newId = GetString(resp, "session_id") ?? "";
                if (string.IsNullOrEmpty(newId))
                {
                    error = "server did not return session_id";
                    return false;
                }

                sessionId = newId;
                _currentSessionId = newId;

                Console.Error.WriteLine(
                    $"[llm_client] generate queued: session={newId}, " +
                    $"texts={texts.Count}, images={images.Count}, " +
                    $"response_format={(string.IsNullOrEmpty(responseFormatJson) ? "no" : "yes")}");
                return true;
            }
            catch (Exception ex)
            {
                error = "SendGenerateCombined: " + ex.Message;
                return false;
            }
        }

        private static bool PopulateAttachments(
            IReadOnlyList<TextAttachment> texts,
            IReadOnlyList<ImageAttachment> images,
            out JsonArray attachmentsArray,
            out string error)
        {
            error = "";
            attachmentsArray = new JsonArray();

            // Validate totals first; fail before touching the wire.
            long totalText = 0;
            for (int i = 0; i < texts.Count; i++)
            {
                var t = texts[i];
                int n = Encoding.UTF8.GetByteCount(t.Text);
                if (n > AttachmentLimits.MaxTextBytesPerFile)
                {
                    error = $"text attachment '{t.Name}' is {n} bytes, " +
                            $"exceeding per-file limit " +
                            $"{AttachmentLimits.MaxTextBytesPerFile}";
                    return false;
                }
                totalText += n;
            }
            if (totalText > AttachmentLimits.MaxTextBytesTotal)
            {
                error = $"cumulative text size {totalText} exceeds total " +
                        $"limit {AttachmentLimits.MaxTextBytesTotal}";
                return false;
            }

            long totalB64 = 0;
            for (int i = 0; i < images.Count; i++)
            {
                var im = images[i];
                int n = im.DataB64.Length;
                if (n == 0)
                {
                    error = $"image attachment '{im.Name}' has empty data_b64";
                    return false;
                }
                if (n > AttachmentLimits.MaxImageB64PerFile)
                {
                    error = $"image attachment '{im.Name}' is {n} base64 chars, " +
                            $"exceeding per-file limit " +
                            $"{AttachmentLimits.MaxImageB64PerFile}";
                    return false;
                }
                totalB64 += n;
            }
            if (totalB64 > AttachmentLimits.MaxImageB64Total)
            {
                error = $"cumulative image size {totalB64} exceeds total " +
                        $"limit {AttachmentLimits.MaxImageB64Total}";
                return false;
            }

            for (int i = 0; i < texts.Count; i++)
            {
                var t = texts[i];
                attachmentsArray.Add(new JsonObject
                {
                    ["kind"] = "text",
                    ["name"] = string.IsNullOrEmpty(t.Name)
                        ? AttachmentDefaults.NamePrefix : t.Name,
                    ["mime"] = string.IsNullOrEmpty(t.Mime)
                        ? AttachmentDefaults.TextMime : t.Mime,
                    ["text"] = t.Text,
                });
            }

            for (int i = 0; i < images.Count; i++)
            {
                var im = images[i];
                attachmentsArray.Add(new JsonObject
                {
                    ["kind"] = "image",
                    ["name"] = string.IsNullOrEmpty(im.Name)
                        ? AttachmentDefaults.NamePrefix : im.Name,
                    ["mime"] = string.IsNullOrEmpty(im.Mime)
                        ? AttachmentDefaults.ImageMime : im.Mime,
                    ["data_b64"] = im.DataB64,
                });
            }

            return true;
        }

        private static string BuildSchemaResponseFormat(
            string schemaName, string schemaJson, bool strict, out string error)
        {
            error = "";

            if (string.IsNullOrEmpty(schemaName))
            {
                error = "BuildSchemaResponseFormat: empty schema name";
                return "";
            }
            if (string.IsNullOrEmpty(schemaJson))
            {
                error = "BuildSchemaResponseFormat: empty schema JSON";
                return "";
            }

            try
            {
                var schemaBody = JsonNode.Parse(schemaJson);
                var envelope = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = schemaName,
                        ["strict"] = strict,
                        ["schema"] = schemaBody,
                    },
                };
                return envelope.ToJsonString();
            }
            catch (Exception ex)
            {
                error = "BuildSchemaResponseFormat: " + ex.Message;
                return "";
            }
        }

        // ====================================================================
        // Internal: connect / cleanup
        // ====================================================================

        private void CleanupPartialConnect(bool exitMainThread)
        {
            if (_app != null)
            {
                try { _app.Dispose(); } catch { }
                _app = null;
            }

            if (exitMainThread && _prepared)
            {
                try { Framework.ExitMainThread(); } catch { }
            }

            ResetCapabilityState();

            _prepared = false;
            _connected = false;
            _clientName = "";
            _currentSessionId = "";
        }

        private void ResetCapabilityState()
        {
            _capabilities = null;
            _capabilitiesRaw = "";
            _serverKind = "";
            _hasCapabilityInfo = false;
        }

        // ====================================================================
        // Internal: small helpers
        // ====================================================================

        private static bool TryParseResponse(string json,
                                             out JsonObject resp,
                                             out string error)
        {
            error = "";
            resp = null!;
            try
            {
                if (JsonNode.Parse(json) is not JsonObject obj)
                {
                    error = "response is not a JSON object";
                    return false;
                }
                resp = obj;
                return true;
            }
            catch (Exception ex)
            {
                error = "failed to parse response JSON: " + ex.Message;
                return false;
            }
        }

        private static string? GetString(JsonObject obj, string key)
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node == null)
            {
                return null;
            }
            try { return node.GetValue<string>(); }
            catch { return null; }
        }

        private static int GetInt(JsonObject obj, string key, int fallback)
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node == null)
            {
                return fallback;
            }
            try { return node.GetValue<int>(); }
            catch { return fallback; }
        }

        // ====================================================================
        // Static helpers (public)
        // ====================================================================

        /// <summary>
        /// Guess an image MIME type from a file extension. Falls back to
        /// image/png for unrecognized extensions; the server has the
        /// authoritative whitelist and will reject an unsupported MIME.
        /// </summary>
        public static string GuessImageMimeByExtension(string filePath)
        {
            int dot = filePath.LastIndexOf('.');
            if (dot < 0) return AttachmentDefaults.ImageMime;
            string ext = filePath.Substring(dot).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => AttachmentDefaults.ImageMime,
            };
        }

        /// <summary>
        /// Read a binary file fully into a byte array. Fails with a clear
        /// error message when the file does not exist or cannot be read.
        /// </summary>
        public static bool ReadBinaryFile(string path, out byte[] data,
                                          out string error)
        {
            data = Array.Empty<byte>();
            error = "";
            try
            {
                data = File.ReadAllBytes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                error = "cannot open file: " + path;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                error = "directory not found: " + path;
                return false;
            }
            catch (Exception ex)
            {
                error = "cannot read file " + path + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Build one ImageAttachment from a file. Enforces the per-file
        /// base64 size limit and normalizes the attachment name.
        /// </summary>
        public static bool BuildImageAttachmentFromFile(
            string filePath, out ImageAttachment attachment, out string error)
        {
            attachment = new ImageAttachment();
            error = "";

            if (string.IsNullOrEmpty(filePath))
            {
                error = "empty file path";
                return false;
            }

            if (!ReadBinaryFile(filePath, out var raw, out error)) return false;

            if (raw.Length == 0)
            {
                error = "image file is empty (0 bytes): " + filePath;
                return false;
            }

            long b64Estimate = ((raw.Length + 2L) / 3L) * 4L;
            if (b64Estimate > AttachmentLimits.MaxImageB64PerFile)
            {
                error = $"image file would encode to about {b64Estimate} " +
                        $"base64 chars, exceeding per-file limit " +
                        $"{AttachmentLimits.MaxImageB64PerFile}";
                return false;
            }

            string name = Path.GetFileName(filePath);
            if (name.Length > AttachmentLimits.MaxNameLen)
            {
                name = name.Substring(0, AttachmentLimits.MaxNameLen);
            }

            attachment.Name = name;
            attachment.Mime = GuessImageMimeByExtension(filePath);
            attachment.DataB64 = Convert.ToBase64String(raw);
            return true;
        }

        /// <summary>
        /// Build one TextAttachment from a file. Decodes the raw bytes as
        /// UTF-8 first, then GBK, then Latin-1. Enforces the per-file byte
        /// limit.
        /// </summary>
        public static bool BuildTextAttachmentFromFile(
            string filePath, out TextAttachment attachment, out string error)
        {
            attachment = new TextAttachment();
            error = "";

            if (string.IsNullOrEmpty(filePath))
            {
                error = "empty file path";
                return false;
            }

            if (!ReadBinaryFile(filePath, out var raw, out error)) return false;

            if (raw.Length > AttachmentLimits.MaxTextBytesPerFile)
            {
                error = $"text file is {raw.Length} bytes, exceeding " +
                        $"per-file limit {AttachmentLimits.MaxTextBytesPerFile}";
                return false;
            }

            string name = Path.GetFileName(filePath);
            if (name.Length > AttachmentLimits.MaxNameLen)
            {
                name = name.Substring(0, AttachmentLimits.MaxNameLen);
            }

            attachment.Name = name;
            attachment.Mime = AttachmentDefaults.TextMime;
            attachment.Text = DecodeTextBytes(raw);
            return true;
        }

        /// <summary>
        /// Decode a byte array to text using the UTF-8 -&gt; GBK -&gt;
        /// Latin-1 fallback chain. Invalid UTF-8 does NOT produce an
        /// exception: the chain silently retries the next candidate.
        /// </summary>
        private static string DecodeTextBytes(byte[] bytes)
        {
            if (bytes.Length == 0) return "";

            if (IsValidUtf8(bytes))
            {
                return Encoding.UTF8.GetString(bytes);
            }

            try
            {
                // GBK (CP936) is the Windows default for Chinese locales.
                return Encoding.GetEncoding("GBK").GetString(bytes);
            }
            catch { /* fall through */ }

            // Latin-1 accepts any byte sequence, so this never fails.
            return Encoding.Latin1.GetString(bytes);
        }

        private static bool IsValidUtf8(byte[] b)
        {
            int i = 0, n = b.Length;
            while (i < n)
            {
                byte c = b[i];
                int extra;
                if (c < 0x80) { i++; continue; }
                else if ((c & 0xE0) == 0xC0) extra = 1;
                else if ((c & 0xF0) == 0xE0) extra = 2;
                else if ((c & 0xF8) == 0xF0) extra = 3;
                else return false;

                if (i + extra >= n) return false;
                for (int k = 1; k <= extra; k++)
                {
                    if ((b[i + k] & 0xC0) != 0x80) return false;
                }
                i += extra + 1;
            }
            return true;
        }

        // ====================================================================
        // Fields
        // ====================================================================

        private AppHandle? _app;
        private readonly string _serverApp;
        private readonly string _endpoint;
        private readonly int _timeoutMs;

        private bool _prepared;
        private volatile bool _connected;

        private string _clientName = "";
        private string _currentSessionId = "";

        private JsonObject? _capabilities;
        private string _capabilitiesRaw = "";
        private string _serverKind = "";
        private volatile bool _hasCapabilityInfo;

        private readonly object _exceptionLock = new();
        private string _lastException = "";

        private DispatchMode _dispatchMode = DispatchMode.MainThread;

        private readonly BlockingCollection<string> _queue = new();
        private Thread? _dispatcherThread;
        private CancellationTokenSource? _dispatcherCts;

        private readonly object _ioLock = new();
    }
}