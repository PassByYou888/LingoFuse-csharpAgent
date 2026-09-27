/**
 * @file agent_service.cs
 * @brief LingoFuse agent beacon / main service (C# port of agent_service.cpp).
 *
 * This program is the C# counterpart of agent_service.cpp and
 * pascal_agent_service.lpr. It is a LingoFuse App that registers on the
 * C4 mesh and exposes three Call APIs:
 *
 *   agent_log       - Forward a log message to the console.
 *   agent_main      - Return the list of available tools as JSON.
 *   register_agent  - Add or replace a dynamic tool definition.
 *
 * The service listens on both ipc:agent and 0.0.0.0:9897, so local and
 * remote clients can reach it. It also prepares a local IPC client, so
 * the App is discoverable on the mesh.
 *
 * All LingoFuse interaction goes through the managed binding (assembly
 * `LingoFuse`, namespace `LingoFuse`). The binding resolves and loads
 * the native library lazily on the first call, so — unlike the C++
 * port — this file has no explicit LF_LoadLibrary step. It also owns
 * the DataHandle lifetime and the callback plumbing, so this file never
 * calls an LF_* function directly.
 *
 * WIRE-FORMAT NOTE
 * ----------------
 * agent_service.cpp serialises every JSON document with
 * nlohmann::json's dump(-1, ' ', false), which is:
 *     - compact,
 *     - ensure_ascii = false.
 *
 * LfIo.WriteJson uses the same policy via System.Text.Json (compact,
 * UnsafeRelaxedJsonEscaping, plus a surrogate-pair rewrite). Every
 * payload produced by this file is therefore byte-compatible with the
 * C++ port and with the other language bindings.
 *
 * All comments and log messages are in English.
 */

using System;
using System.Collections.Generic;
using System.Text.Json;

using LingoFuse;

namespace AgentService;

// ============================================================================
// Configuration
// ============================================================================

internal static class Config
{
    public const string AppName = "agent_main_app";
    public const string AppDesc = "agent main application";
    public const string IpcEndpoint = "ipc:agent";
    public const string TcpListenAddr = "0.0.0.0:9897";
    public const string TcpPublicAddr = "127.0.0.1:9897";
}

// ============================================================================
// Status output
// ============================================================================
//
// All diagnostic output goes to stderr so that it never interferes with
// any stdout-based consumer of this program. This mirrors the C++ port,
// whose status() helper writes to stderr.

internal static class Status
{
    public static void Log(string message)
    {
        Console.Error.WriteLine(message);
    }
}

// ============================================================================
// Registered tool storage
// ============================================================================
//
// The beacon keeps a flat list of tool definitions. Each entry is an
// independent copy of the JSON object received from a register_agent
// call.
//
// ToolRegistry replaces the C++ port's static std::mutex + std::vector
// pair. Its lock is required because register_agent runs on a native
// worker thread and may be invoked concurrently with agent_main, which
// reads the list from a different worker thread.

internal sealed class ToolEntry
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string TargetApp { get; init; } = "";
    public string TargetApi { get; init; } = "";

    /// <summary>
    /// The tool's JSON-Schema "parameters" object, captured from the
    /// register_agent request. Stored as a detached JsonElement so that
    /// it remains valid after the request's JsonDocument is disposed.
    /// </summary>
    public JsonElement Parameters { get; init; }
}

internal static class ToolRegistry
{
    private static readonly object Gate = new();
    private static readonly List<ToolEntry> Agents = new();

    /// <summary>
    /// Return a shallow copy of the registered tool list, safe to
    /// iterate outside the lock.
    /// </summary>
    public static List<ToolEntry> Snapshot()
    {
        lock (Gate)
        {
            return new List<ToolEntry>(Agents);
        }
    }

    /// <summary>
    /// Add a new entry, or replace an existing entry with the same
    /// name. Returns true when an existing entry was replaced.
    /// </summary>
    public static bool AddOrReplace(ToolEntry entry)
    {
        lock (Gate)
        {
            for (int i = 0; i < Agents.Count; i++)
            {
                if (string.Equals(
                        Agents[i].Name, entry.Name,
                        StringComparison.Ordinal))
                {
                    Agents[i] = entry;
                    return true;
                }
            }
            Agents.Add(entry);
            return false;
        }
    }
}

// ============================================================================
// Call API: agent_log
// ============================================================================
//
// Reads a JSON request, extracts the "message" field, prints it to the
// console, and returns {"status":"ok"}. If the payload is not JSON, or
// is JSON that is not an object with a "message" field, the raw text is
// used as the log message. This mirrors the C++ port exactly.

internal static class AgentLogApi
{
    public static void Handle(DataHandle input, DataHandle output)
    {
        // Read the raw NUL-framed payload. LfIo.ReadString applies the
        // same fault-tolerant read the C++ port relies on: stop at the
        // first NUL, or consume the whole remaining buffer if no NUL is
        // present.
        string payload = LfIo.ReadString(input);

        string msg = payload;
        if (!string.IsNullOrEmpty(payload))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(payload);
                JsonElement root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("message", out JsonElement m)
                    && m.ValueKind == JsonValueKind.String)
                {
                    msg = m.GetString() ?? "";
                }
                // Otherwise: not an object, or no "message" field —
                // fall back to the raw payload.
            }
            catch (JsonException)
            {
                // Not JSON at all. Use the raw payload.
                msg = payload;
            }
        }

        if (!string.IsNullOrEmpty(msg))
        {
            Status.Log($"[agent_log] {msg}");
        }

        LfIo.WriteJson(output, new { status = "ok" });
    }
}

// ============================================================================
// Call API: agent_main
// ============================================================================
//
// Returns {"tools":[...]} containing the built-in agent_log entry plus
// every dynamically registered tool whose target API is currently
// reachable on the mesh.
//
// The reachability probe (LingoFuseStatus.CheckApi) uses the same
// cache-based lookup as the C++ port's LF_CheckApi: it reads a local
// cache updated by network broadcasts, so it can lag by a few seconds
// after a new registration. This matches the documented semantics of
// the underlying native function.

internal static class AgentMainApi
{
    public static void Handle(DataHandle input, DataHandle output)
    {
        // The C++ port ignores the input payload; we do the same.
        _ = input;

        var tools = new List<object>();

        // ---- Built-in tool: agent_log -------------------------------
        tools.Add(new
        {
            name = "agent_log",
            description = "Send log messages to the backend",
            target_app = Config.AppName,
            target_api = "agent_log",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    message = new
                    {
                        type = "string",
                        description = "Log message content"
                    }
                },
                required = new[] { "message" }
            }
        });

        int toolCount = 1;

        // ---- Dynamically registered tools ---------------------------
        foreach (ToolEntry e in ToolRegistry.Snapshot())
        {
            if (!LingoFuseStatus.CheckApi(e.TargetApp, e.TargetApi))
            {
                Status.Log(
                    $"[agent_main] Skipped dynamic tool \"{e.Name}\": " +
                    $"API {e.TargetApp}.{e.TargetApi} not available");
                continue;
            }

            tools.Add(new
            {
                name = e.Name,
                description = e.Description,
                target_app = e.TargetApp,
                target_api = e.TargetApi,
                parameters = e.Parameters
            });

            Status.Log(
                $"[agent_main] Included dynamic tool: {e.Name} " +
                $"(API {e.TargetApp}.{e.TargetApi} available)");
            toolCount++;
        }

        LfIo.WriteJson(output, new { tools });
        Status.Log($"[agent_main] Tool list sent ({toolCount} tools)");
    }
}

// ============================================================================
// Call API: register_agent
// ============================================================================
//
// Validates the request fields and adds or replaces a tool entry.
// Required fields: name, description, target_app, target_api,
// parameters. A missing field, or a payload that is not a JSON object,
// produces {"status":"error","message":...} — matching the C++ port.

internal static class RegisterAgentApi
{
    private static readonly string[] RequiredFields =
    {
        "name", "description", "target_app", "target_api", "parameters"
    };

    public static void Handle(DataHandle input, DataHandle output)
    {
        string payload = LfIo.ReadString(input);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            Status.Log("[register_agent] Error: Invalid JSON payload");
            WriteError(output, "Invalid JSON payload");
            return;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                Status.Log(
                    "[register_agent] Error: Request is not a JSON object");
                WriteError(output, "Request is not a JSON object");
                return;
            }

            // ---- Required field check -------------------------------
            foreach (string field in RequiredFields)
            {
                if (!root.TryGetProperty(field, out _))
                {
                    string err = $"Missing \"{field}\" field";
                    Status.Log($"[register_agent] Error: {err}");
                    WriteError(output, err);
                    return;
                }
            }

            // ---- Build the tool entry -------------------------------
            //
            // The C++ port reads the four scalar fields with
            // req.value(key, "") which returns an empty string for a
            // missing or wrong-typed value. ReadStringOrEmpty mirrors
            // that exact behaviour.
            //
            // JsonElement.Clone() detaches the "parameters" element
            // from the JsonDocument that owns its backing store, so the
            // stored ToolEntry remains valid after `doc` is disposed at
            // the end of this using block.
            ToolEntry entry = new()
            {
                Name = ReadStringOrEmpty(root, "name"),
                Description = ReadStringOrEmpty(root, "description"),
                TargetApp = ReadStringOrEmpty(root, "target_app"),
                TargetApi = ReadStringOrEmpty(root, "target_api"),
                Parameters = root.GetProperty("parameters").Clone()
            };

            bool replaced = ToolRegistry.AddOrReplace(entry);

            Status.Log(
                $"[register_agent] " +
                $"{(replaced ? "Updated existing" : "Added new")} tool: " +
                $"{entry.Name} \"{entry.Description}\"");
        }

        LfIo.WriteJson(output, new { status = "ok" });
    }

    /// <summary>
    /// Return the string value of a JSON property, or the empty string
    /// when the property is missing or is not a JSON string. Matches
    /// nlohmann::json::value(key, std::string()) from the C++ port.
    /// </summary>
    private static string ReadStringOrEmpty(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement v)
            && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString() ?? "";
        }
        return "";
    }

    private static void WriteError(DataHandle output, string err)
    {
        LfIo.WriteJson(output, new { status = "error", message = err });
    }
}

// ============================================================================
// Main
// ============================================================================

internal static class Program
{
    private static int Main()
    {
        Status.Log("[MAIN] LingoFuse agent service starting");

        // The managed binding resolves and loads the native library on
        // the first P/Invoke call, so there is no equivalent of the C++
        // port's explicit LF_LoadLibrary() step here.

        AppHandle? app = null;
        try
        {
            // ---- Create the App and register the three Call APIs -----
            app = new AppHandle(Config.AppName, Config.AppDesc);
            Status.Log($"[MAIN] Application \"{Config.AppName}\" created");

            bool ok = true;
            ok &= app.RegisterCall(
                "agent_log",
                "Logging tool",
                AgentLogApi.Handle);
            ok &= app.RegisterCall(
                "agent_main",
                "Tool list entry",
                AgentMainApi.Handle);
            ok &= app.RegisterCall(
                "register_agent",
                "Dynamic tool registration",
                RegisterAgentApi.Handle);

            if (!ok)
            {
                Status.Log(
                    "[MAIN] FATAL: Failed to register one or more APIs");
                return 1;
            }
            Status.Log(
                "[MAIN] Registered APIs: " +
                "agent_log, agent_main, register_agent");

            // ---- Configure the mesh and prepare the service ----------
            //
            // Wait_Ready=False: deployment mode. The service comes
            // online as soon as the mesh finishes its internal
            // preparation; the caller does not block waiting for peer
            // discovery. This mirrors the C++ port.
            Framework.SetOption("Wait_Ready", "False");
            Status.Log("[MAIN] Set Wait_Ready=False (deployment mode)");

            Framework.ResetPrepare();

            Status.Log(
                $"[MAIN] Preparing IPC service on {Config.IpcEndpoint}");
            Framework.PrepareService(
                Config.IpcEndpoint, Config.IpcEndpoint);

            Status.Log(
                $"[MAIN] Preparing TCP service on {Config.TcpListenAddr} " +
                $"(public {Config.TcpPublicAddr})");
            Framework.PrepareService(
                Config.TcpListenAddr, Config.TcpPublicAddr);

            Status.Log(
                $"[MAIN] Connecting local IPC client to " +
                $"{Config.IpcEndpoint}");
            Framework.PrepareClient(Config.IpcEndpoint, app);

            Status.Log("[MAIN] Starting framework...");
            int ready = Framework.PrepareDone();

            // PrepareDone returns 1 the first time it is called in this
            // process and 0 on any subsequent call. This process has
            // not initialised the framework before, so a 0 here would
            // indicate a genuine startup failure — but we still check
            // CheckMainThread() as a defensive second signal, mirroring
            // the pattern used by the C++ port.
            if (ready != 1 && !LingoFuseStatus.CheckMainThread())
            {
                Status.Log("[MAIN] FATAL: LF_PrepareDone failed");
                return 1;
            }
            Status.Log("[MAIN] Framework started successfully");

            // ---- Main loop: read lines from stdin until "exit" ------
            Status.Log(
                "[MAIN] Service is running. Type \"exit\" to quit.");

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
            //   1. Clear network event callbacks.
            //   2. Exit the simulated main thread.
            //   3. Dispose the App (detach, stop its sequenced threads).
            //   4. Shut down the framework and release the global pool.
            //
            // Each step is best-effort and independent: a failure in
            // one step must not prevent the remaining steps from
            // running.
            Status.Log("[MAIN] Shutting down...");

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