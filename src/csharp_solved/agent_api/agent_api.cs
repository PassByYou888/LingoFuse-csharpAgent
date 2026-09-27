/**
 * @file agent_api.cs
 * @brief LingoFuse arithmetic tool provider (C# port of agent_api.cpp).
 *
 * This program is the C# counterpart of agent_api.cpp and
 * pascal_agent_api.lpr. It registers as an App on the C4 mesh and
 * exposes four Call APIs:
 *
 *   add  - Add two integers: a + b
 *   sub  - Subtract two integers: a - b
 *   mul  - Multiply two integers: a * b
 *   div  - Divide two integers (floating result): a / b
 *
 * On startup, after connecting to the beacon, it registers each of
 * those APIs as a tool through the beacon's register_agent Call API.
 *
 * All LingoFuse interaction goes through the managed binding (assembly
 * `LingoFuse`, namespace `LingoFuse`). The binding resolves and loads
 * the native library lazily on the first call, so — unlike the C++
 * port — this file has no explicit LF_LoadLibrary step. It also owns
 * the DataHandle lifetime and the callback plumbing, so this file
 * never calls an LF_* function directly.
 *
 * ASYNCHRONOUS LOGGING
 * --------------------
 * The C++ port forwards each log message to the beacon's agent_log API
 * from a detached std::thread, because the RPC must not run on the
 * LingoFuse notification thread. This port uses Task.Run for the same
 * reason: the log RPC runs on a .NET thread pool thread, never on the
 * callback thread.
 *
 * A _shuttingDown flag and a _pendingLogs counter make shutdown safe:
 * the main thread sets the flag, then waits briefly for in-flight log
 * tasks to complete before calling Framework.Shutdown.
 *
 * All comments and log messages are in English.
 */

using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using LingoFuse;

namespace AgentApi;

// ============================================================================
// Configuration
// ============================================================================

internal static class Config
{
    /// <summary>App name registered on the C4 mesh.</summary>
    public const string MyAppName = "my_calculator";

    /// <summary>Human-readable description of this App.</summary>
    public const string MyAppDesc =
        "Calculator service providing arithmetic tools";

    /// <summary>LingoFuse endpoint of the beacon.</summary>
    public const string IpcEndpoint = "ipc:agent";

    /// <summary>App name of the beacon service (agent_service).</summary>
    public const string BeaconApp = "agent_main_app";

    /// <summary>Call API name that registers a tool with the beacon.</summary>
    public const string RegisterApi = "register_agent";

    /// <summary>Call API name that forwards a log message to the beacon.</summary>
    public const string AgentLogApi = "agent_log";

    /// <summary>
    /// When true, each successful call and each error is logged to
    /// stderr and forwarded to the beacon. When false, the service is
    /// silent on the logging side but still returns results to callers.
    /// </summary>
    public const bool DebugLog = true;
}

// ============================================================================
// Status output
// ============================================================================

internal static class Status
{
    public static void Log(string message)
    {
        Console.Error.WriteLine(message);
    }
}

// ============================================================================
// Asynchronous logging
// ============================================================================
//
// A log message is forwarded to the beacon's agent_log Call API. The
// RPC must NOT run on the LingoFuse callback thread (doing so would
// deadlock), so a Task.Run performs it on a .NET thread pool thread.
//
// Shutdown coordination:
//   - _shuttingDown is set to true before the framework is torn down.
//   - _pendingLogs counts in-flight log tasks.
//   - The main thread calls WaitForPending() before shutting down, so
//     no log task can call into the native library after it has been
//     unloaded.
//
// The _shuttingDown flag is checked twice: once before Task.Run, and
// once inside the task after it starts. This narrows the window during
// which a logging RPC could race with shutdown, mirroring the C++ port.

internal static class AsyncLog
{
    private static volatile bool _shuttingDown;
    private static int _pendingLogs;

    /// <summary>
    /// Set the shutdown flag. Called once, by the main thread, before
    /// the framework is torn down.
    /// </summary>
    public static void BeginShutdown()
    {
        _shuttingDown = true;
    }

    /// <summary>
    /// Forward a log message to the beacon. Best-effort: any failure is
    /// swallowed, matching the C++ port.
    /// </summary>
    public static void Send(string msg)
    {
        if (_shuttingDown)
        {
            return;
        }

        Interlocked.Increment(ref _pendingLogs);
        Task.Run(() =>
        {
            try
            {
                if (_shuttingDown)
                {
                    return;
                }

                using var data = new DataHandle(Config.AgentLogApi);
                LfIo.WriteJson(data, new { message = msg });

                using var result = Framework.Call(
                    Config.BeaconApp, data, 3000);
                // The result handle is disposed by `using` regardless
                // of whether the call succeeded. A zero-size handle
                // (timeout or unreachable target) is still a valid
                // handle and must be disposed.
            }
            catch
            {
                // Best-effort logging; ignore any failure.
            }
            finally
            {
                Interlocked.Decrement(ref _pendingLogs);
            }
        });
    }

    /// <summary>
    /// Wait until all in-flight log tasks have completed, or until the
    /// timeout expires. Called by the main thread just before shutdown.
    /// </summary>
    public static void WaitForPending(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pendingLogs) > 0
               && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }
    }
}

// ============================================================================
// Arithmetic tool callbacks
// ============================================================================
//
// Request contract:   {"a": <int>, "b": <int>}
// Success response:   {"result": <number>}
// Error response:     {"error": "<message>"}
//
// All four handlers share the same parsing logic. The div handler has an
// additional zero-divisor check.

internal static class Arithmetic
{
    /// <summary>
    /// Parse the {"a": int, "b": int} request body. Returns false on
    /// any failure and fills `err` with a human-readable description
    /// that matches the C++ port's wording.
    /// </summary>
    private static bool TryParseAB(
        DataHandle input,
        out int a,
        out int b,
        out string err)
    {
        a = 0;
        b = 0;
        err = "";

        // The C++ port reads the whole payload with readPayload(), which
        // uses LF_GetSize + LF_ReadStringBytes. LfIo.ReadString applies
        // the same NUL-framed, fault-tolerant read.
        string payload = LfIo.ReadString(input);
        if (string.IsNullOrEmpty(payload))
        {
            err = "Empty input";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            err = "Invalid JSON";
            return false;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("a", out JsonElement aElem)
                || !root.TryGetProperty("b", out JsonElement bElem))
            {
                err = "Missing \"a\" or \"b\"";
                return false;
            }

            if (aElem.ValueKind != JsonValueKind.Number
                || bElem.ValueKind != JsonValueKind.Number
                || !aElem.TryGetInt32(out a)
                || !bElem.TryGetInt32(out b))
            {
                err = "Invalid type for \"a\" or \"b\"";
                return false;
            }
        }

        return true;
    }

    private static void WriteError(DataHandle output, string err)
    {
        LfIo.WriteJson(output, new { error = err });
    }

    // ---- add --------------------------------------------------------

    public static void DoAdd(DataHandle input, DataHandle output)
    {
        if (!TryParseAB(input, out int a, out int b, out string err))
        {
            WriteError(output, err);
            if (Config.DebugLog)
            {
                Status.Log($"[add] Error: {err}");
                AsyncLog.Send($"[add] Error: {err}");
            }
            return;
        }

        int sum = a + b;
        LfIo.WriteJson(output, new { result = sum });

        if (Config.DebugLog)
        {
            Status.Log($"[add] {a} + {b} = {sum}");
            AsyncLog.Send($"[add] {a} + {b} = {sum}");
        }
    }

    // ---- sub --------------------------------------------------------

    public static void DoSub(DataHandle input, DataHandle output)
    {
        if (!TryParseAB(input, out int a, out int b, out string err))
        {
            WriteError(output, err);
            if (Config.DebugLog)
            {
                Status.Log($"[sub] Error: {err}");
                AsyncLog.Send($"[sub] Error: {err}");
            }
            return;
        }

        int diff = a - b;
        LfIo.WriteJson(output, new { result = diff });

        if (Config.DebugLog)
        {
            Status.Log($"[sub] {a} - {b} = {diff}");
            AsyncLog.Send($"[sub] {a} - {b} = {diff}");
        }
    }

    // ---- mul --------------------------------------------------------

    public static void DoMul(DataHandle input, DataHandle output)
    {
        if (!TryParseAB(input, out int a, out int b, out string err))
        {
            WriteError(output, err);
            if (Config.DebugLog)
            {
                Status.Log($"[mul] Error: {err}");
                AsyncLog.Send($"[mul] Error: {err}");
            }
            return;
        }

        int prod = a * b;
        LfIo.WriteJson(output, new { result = prod });

        if (Config.DebugLog)
        {
            Status.Log($"[mul] {a} * {b} = {prod}");
            AsyncLog.Send($"[mul] {a} * {b} = {prod}");
        }
    }

    // ---- div --------------------------------------------------------

    public static void DoDiv(DataHandle input, DataHandle output)
    {
        if (!TryParseAB(input, out int a, out int b, out string err))
        {
            WriteError(output, err);
            if (Config.DebugLog)
            {
                Status.Log($"[div] Error: {err}");
                AsyncLog.Send($"[div] Error: {err}");
            }
            return;
        }

        if (b == 0)
        {
            const string err2 = "Division by zero";
            WriteError(output, err2);
            if (Config.DebugLog)
            {
                Status.Log($"[div] Error: {err2}");
                AsyncLog.Send($"[div] Error: {err2}");
            }
            return;
        }

        double quot = (double)a / b;
        LfIo.WriteJson(output, new { result = quot });

        if (Config.DebugLog)
        {
            // The C++ port logs this with %.2f. To produce the same
            // text on any locale, format with the invariant culture.
            string formatted = quot.ToString(
                "F2", CultureInfo.InvariantCulture);
            Status.Log($"[div] {a} / {b} = {formatted}");
            AsyncLog.Send($"[div] {a} / {b} = {formatted}");
        }
    }
}

// ============================================================================
// Tool registration with the beacon
// ============================================================================
//
// Each tool definition is a JSON object with the shape the beacon's
// register_agent Call API expects:
//
//     {
//         "name":        "...",
//         "description": "...",
//         "target_app":  "...",
//         "target_api":  "...",
//         "parameters":  { JSON Schema }
//     }
//
// The beacon replies with {"status":"ok"} on success. Any other
// response, or any transport failure, is reported and treated as a
// registration failure. This function never throws.

internal static class ToolRegistration
{
    /// <summary>
    /// Build a JSON tool definition for one arithmetic API.
    /// </summary>
    public static object BuildToolDef(
        string name,
        string description,
        string paramADesc,
        string paramBDesc)
    {
        return new
        {
            name = name,
            description = description,
            target_app = Config.MyAppName,
            target_api = name,
            parameters = new
            {
                type = "object",
                properties = new
                {
                    a = new { type = "integer", description = paramADesc },
                    b = new { type = "integer", description = paramBDesc }
                },
                required = new[] { "a", "b" }
            }
        };
    }

    /// <summary>
    /// Send one tool definition to the beacon's register_agent API.
    /// Returns true only when the beacon replies with {"status":"ok"}.
    /// All failures are logged. This function never throws.
    /// </summary>
    public static bool RegisterTool(object toolDef)
    {
        try
        {
            using var data = new DataHandle(Config.RegisterApi);
            LfIo.WriteJson(data, toolDef);

            using var result = Framework.Call(
                Config.BeaconApp, data, 5000);

            // Framework.Call never returns null; a timeout or
            // unreachable target yields a zero-size handle. That is
            // the failure signal here, matching the C++ port's
            // `if (!result)` check.
            if (result.Size == 0)
            {
                Status.Log("[Register] Error: empty response");
                return false;
            }

            string respText = LfIo.ReadString(result);
            if (string.IsNullOrEmpty(respText))
            {
                Status.Log("[Register] Error: empty response");
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(respText);
                JsonElement root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("status", out JsonElement s)
                    && s.ValueKind == JsonValueKind.String
                    && s.GetString() == "ok")
                {
                    return true;
                }

                string msg = "unknown";
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("message", out JsonElement m)
                    && m.ValueKind == JsonValueKind.String)
                {
                    msg = m.GetString() ?? "unknown";
                }
                Status.Log($"[Register] Failed: {msg}");
            }
            catch (JsonException)
            {
                Status.Log("[Register] Failed: invalid response");
            }
        }
        catch (Exception ex)
        {
            Status.Log($"[Register] Error: {ex.Message}");
        }
        return false;
    }
}

// ============================================================================
// Main
// ============================================================================

internal static class Program
{
    private static int Main()
    {
        Status.Log("[MAIN] LingoFuse calculator tool provider starting");

        AppHandle? app = null;
        try
        {
            // ---- Create the App and register the four Call APIs -----
            app = new AppHandle(Config.MyAppName, Config.MyAppDesc);

            bool ok = true;
            ok &= app.RegisterCall(
                "add", "Add two integers", Arithmetic.DoAdd);
            ok &= app.RegisterCall(
                "sub", "Subtract two integers", Arithmetic.DoSub);
            ok &= app.RegisterCall(
                "mul", "Multiply two integers", Arithmetic.DoMul);
            ok &= app.RegisterCall(
                "div",
                "Divide two integers (floating result)",
                Arithmetic.DoDiv);

            if (!ok)
            {
                Status.Log(
                    "[MAIN] FATAL: Failed to register one or more APIs");
                return 1;
            }
            Status.Log("[MAIN] Registered APIs: add, sub, mul, div");

            // ---- Connect to the beacon ------------------------------
            //
            // WaitConnect=True blocks PrepareDone until the client has
            // completed its handshake with the beacon. This mirrors the
            // C++ port, which sets the same option.
            //
            // Only PrepareClient is called: this App is a worker that
            // attaches to an existing beacon, not a service host.
            Framework.SetOption("WaitConnect", "True");
            Framework.ResetPrepare();
            Framework.PrepareClient(Config.IpcEndpoint, app);

            if (Framework.PrepareDone() != 1)
            {
                Status.Log(
                    $"[MAIN] FATAL: Failed to connect to beacon at " +
                    $"{Config.IpcEndpoint}");
                return 1;
            }
            Status.Log(
                "[MAIN] Connected to beacon; registering tools...");

            // ---- Register each arithmetic API with the beacon ------
            RegisterAndReport(
                "add",
                ToolRegistration.BuildToolDef(
                    "add", "Add two integers: a + b",
                    "First operand", "Second operand"));

            RegisterAndReport(
                "sub",
                ToolRegistration.BuildToolDef(
                    "sub", "Subtract two integers: a - b",
                    "First operand", "Second operand"));

            RegisterAndReport(
                "mul",
                ToolRegistration.BuildToolDef(
                    "mul", "Multiply two integers: a * b",
                    "First operand", "Second operand"));

            RegisterAndReport(
                "div",
                ToolRegistration.BuildToolDef(
                    "div",
                    "Divide two integers (floating result): a / b",
                    "Dividend", "Divisor (must be non-zero)"));

            Status.Log(
                "[MAIN] All tools registered. Type \"exit\" to quit.");

            // ---- Main loop: read lines from stdin until "exit" -----
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                if (line == "exit")
                {
                    break;
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Status.Log($"[MAIN] FATAL: {ex.Message}");
            return 1;
        }
        finally
        {
            // ---- Cleanup --------------------------------------------
            // Order matches the documented contract:
            //   1. Set the shutdown flag, so any in-flight log task
            //      bails out before the library is torn down.
            //   2. Wait briefly for in-flight log tasks to complete.
            //   3. Clear network event callbacks.
            //   4. Exit the simulated main thread.
            //   5. Dispose the App (detach, stop its sequenced
            //      threads).
            //   6. Shut down the framework and release the global pool.
            //
            // Each step is best-effort and independent.
            Status.Log("[MAIN] Shutting down...");

            AsyncLog.BeginShutdown();
            AsyncLog.WaitForPending(TimeSpan.FromSeconds(5));

            TryRun(NetworkEvents.Clear);
            TryRun(Framework.ExitMainThread);
            if (app is not null)
            {
                TryRun(app.Dispose);
            }
            TryRun(Framework.Shutdown);

            Status.Log("[MAIN] Cleanup complete.");
        }
    }

    /// <summary>
    /// Call the beacon's register_agent API once and print the outcome
    /// with the same [OK]/[FAIL] wording used by the C++ port.
    /// </summary>
    private static void RegisterAndReport(string toolName, object toolDef)
    {
        if (ToolRegistration.RegisterTool(toolDef))
        {
            Status.Log($"[OK] Registered tool: {toolName}");
        }
        else
        {
            Status.Log($"[FAIL] Failed to register {toolName}");
        }
    }

    /// <summary>
    /// Invoke a cleanup action, swallowing any exception. Used only in
    /// the shutdown path, where every step is best-effort.
    /// </summary>
    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Swallow: shutdown must not abort on a failed step.
        }
    }
}