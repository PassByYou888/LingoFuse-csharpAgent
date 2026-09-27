// -----------------------------------------------------------------------------
// llm_csharp_tool.cs
// -----------------------------------------------------------------------------
// Interactive / one-shot command-line client for the LingoFuse LLM services.
//
// This is the C# counterpart of llm_cpp_tool.cpp. It uses the Llm.Client
// library (llm_client.cs) and speaks the same protocol as llm_service.py /
// llm_proxy.py / llm_proxy_tool.py.
//
// Architecture
// ------------
//   - The main thread owns the console.
//   - A dedicated input thread performs a blocking Console.ReadLine() and
//     places each completed line into a thread-safe queue.
//   - The main thread runs a small event loop:
//         1. Drain pending LLM events via client.PumpEvents(0).
//         2. React to state changes (e.g. a turn finished).
//         3. Print the prompt if it is due.
//         4. Process one queued input line, if any.
//         5. Sleep briefly and repeat.
//   This lets the model's streaming output and the operator's typing
//   coexist without any synchronization between them beyond a mutex-
//   protected queue.
//
// Attachments and Structured Output
// ---------------------------------
//   Attachments are one-shot: after they are delivered with a turn, the
//   pending lists are cleared automatically.
//   Structured Output is sticky: once a JSON Schema is loaded, it stays
//   active on every subsequent turn until disabled with /schema off.
//
// All output is English.
// -----------------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using Llm;

namespace LlmCSharpTool
{
    // ========================================================================
    // Constants
    // ========================================================================

    internal static class Consts
    {
        public const string DefaultEndpoint = "ipc:llm_service";
        public const string DefaultServerApp = "LLM_Service";
        public const int DefaultTimeoutMs = 30000;
        public const int MainLoopPollMs = 15;
        public const string DefaultSchemaName = "response_schema";

        // ANSI dim / reset for the thinking stream. Disabled when NO_COLOR
        // is set in the environment.
        public const string StyleThink = "\x1b[2m";
        public const string StyleReset = "\x1b[0m";

        // Built-in detector template. Loaded with "/schema template".
        public const string DetectorTemplate = @"{
  ""type"": ""object"",
  ""properties"": {
    ""detections"": {
      ""type"": ""array"",
      ""description"": ""List of detected objects with confidence scores"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""label"": {
            ""type"": ""string"",
            ""description"": ""Object class name, e.g. person, car, dog""
          },
          ""bbox"": {
            ""type"": ""array"",
            ""description"": ""Normalized bbox [x_min, y_min, x_max, y_max], values in 0~1"",
            ""items"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1 },
            ""minItems"": 4,
            ""maxItems"": 4
          },
          ""confidence"": {
            ""type"": ""number"",
            ""description"": ""Detection confidence, 0.0 to 1.0"",
            ""minimum"": 0,
            ""maximum"": 1
          }
        },
        ""required"": [""label"", ""bbox"", ""confidence""]
      }
    }
  },
  ""required"": [""detections""]
}";
    }

    // ========================================================================
    // Pending schema state
    // ========================================================================

    internal sealed class PendingSchema
    {
        public bool Active = false;
        public string Name = Consts.DefaultSchemaName;
        public string Body = "";
        public bool Strict = true;
    }

    // ========================================================================
    // Console setup
    // ========================================================================

    internal static class ConsoleSetup
    {
        public static void Apply()
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            try { Console.InputEncoding = new UTF8Encoding(false); } catch { }
        }

        public static bool UseColor()
        {
            string? noColor = Environment.GetEnvironmentVariable("NO_COLOR");
            return string.IsNullOrEmpty(noColor);
        }
    }

    // ========================================================================
    // Input reader thread
    // ========================================================================

    internal sealed class InputReader
    {
        private readonly BlockingCollection<string> _lines = new();
        private Thread? _thread;
        private volatile bool _running;
        private volatile bool _eof;

        public void Start()
        {
            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "llm-csharp-tool-input",
            };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            // The input thread is a background thread; it will be reaped
            // by the runtime at process exit even if it is still blocked
            // in Console.ReadLine().
        }

        public bool TryPop(out string line)
        {
            if (_lines.TryTake(out var s, 0))
            {
                line = s;
                return true;
            }
            line = "";
            return false;
        }

        public bool EofReached => _eof;

        private void Loop()
        {
            try
            {
                while (_running)
                {
                    string? l = Console.ReadLine();
                    if (l == null)
                    {
                        _eof = true;
                        break;
                    }
                    _lines.Add(l);
                }
            }
            catch
            {
                _eof = true;
            }
        }
    }

    // ========================================================================
    // Command-line options
    // ========================================================================

    internal sealed class Options
    {
        public string Endpoint = Consts.DefaultEndpoint;
        public string ServerApp = Consts.DefaultServerApp;
        public int TimeoutMs = Consts.DefaultTimeoutMs;

        public string Content = "";
        public bool ContentSet = false;
        public string Prompt = "";
        public string SessionId = "";
        public string SystemMessage = "";

        public readonly List<string> TextFiles = new();
        public readonly List<string> ImageFiles = new();

        public string SchemaFile = "";
        public string SchemaName = Consts.DefaultSchemaName;
        public bool Strict = true;

        public bool Keep = false;
        public bool Thinking = false;
        public bool Debug = false;

        public bool Help = false;
    }

    // ========================================================================
    // Argument parsing
    // ========================================================================
    //
    // NOTE ON CS1628
    // --------------
    // A local function, lambda, anonymous method, or query expression is NOT
    // allowed to capture a ref / out / in parameter of its enclosing method.
    // The previous version of this class used a local helper `Need(i, name)`
    // that wrote to the `out string error` parameter, which triggered
    //     error CS1628: Cannot use ref, out, or in parameter 'error'
    // inside an anonymous method, lambda expression, query expression, or
    // local function.
    //
    // The fix splits argument handling into two passes:
    //
    //   Pass 1 handles every flag that takes NO value (--help, --keep, ...).
    //          These are dispatched by an early switch with `continue`.
    //
    //   Pass 2 handles every argument that DOES take a value. At this point
    //          the option is guaranteed to be one of a small, closed set,
    //          so a single inline bounds check is enough to validate that a
    //          value follows.
    //
    // This is behaviorally identical to the original `Need()` approach, but
    // no local function or lambda captures the `out` parameter, so the
    // compiler accepts it.

    internal static class ArgParser
    {
        public static bool Parse(string[] argv, Options opts, out string error)
        {
            error = "";

            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];

                // ---------------------------------------------------------
                // Pass 1: flags that take NO value.
                // ---------------------------------------------------------
                switch (a)
                {
                    case "--help":
                    case "-h":
                        opts.Help = true;
                        continue;

                    case "--no-strict":
                        opts.Strict = false;
                        continue;

                    case "--keep":
                        opts.Keep = true;
                        continue;

                    case "--thinking":
                        opts.Thinking = true;
                        continue;

                    case "--debug":
                        opts.Debug = true;
                        continue;
                }

                // ---------------------------------------------------------
                // Pass 2: every recognized argument below requires a value.
                // ---------------------------------------------------------
                if (i + 1 >= argv.Length)
                {
                    error = "missing value for " + a;
                    return false;
                }
                string val = argv[++i];

                switch (a)
                {
                    case "--endpoint":
                        opts.Endpoint = val;
                        break;

                    case "--server-app":
                        opts.ServerApp = val;
                        break;

                    case "--timeout":
                        if (!int.TryParse(val, out opts.TimeoutMs))
                        {
                            error = "invalid value for --timeout";
                            return false;
                        }
                        break;

                    case "--content":
                        opts.Content = val;
                        opts.ContentSet = true;
                        break;

                    case "--prompt":
                        opts.Prompt = val;
                        break;

                    case "--text":
                        opts.TextFiles.Add(val);
                        break;

                    case "--image":
                        opts.ImageFiles.Add(val);
                        break;

                    case "--session-id":
                        opts.SessionId = val;
                        break;

                    case "--system-message":
                        opts.SystemMessage = val;
                        break;

                    case "--schema":
                        opts.SchemaFile = val;
                        break;

                    case "--schema-name":
                        opts.SchemaName = val;
                        break;

                    default:
                        error = "unknown argument: " + a;
                        return false;
                }
            }

            return true;
        }

        public static void PrintUsage(string prog)
        {
            Console.WriteLine(
$@"Usage: {prog} [options]

Interactive mode is entered when none of --content, --text, --image,
or --schema is given. Otherwise a single turn is issued and the
program exits.

Connection:
  --endpoint <addr>         LingoFuse endpoint (default: {Consts.DefaultEndpoint})
  --server-app <name>       Server App name (default: {Consts.DefaultServerApp})
  --timeout <ms>            Call timeout in milliseconds (default: {Consts.DefaultTimeoutMs})

One-shot mode:
  --content <text>          Content to send
  --prompt <text>           Extra prompt appended after content
  --text <file>             Attach a text file (repeatable)
  --image <file>            Attach an image file (repeatable)
  --session-id <id>         Reuse an existing session
  --system-message <text>   System message for a new session
  --keep                    Keep the session alive after the turn
  --thinking                Enable thinking (server-dependent)

Structured Output (works with llm_proxy / llm_proxy_tool):
  --schema <file>           Load JSON Schema from file
  --schema-name <name>      Schema name (default: {Consts.DefaultSchemaName})
  --no-strict               Disable strict mode (default: ON)

Diagnostics:
  --debug                   Print debug information to stderr
  --help, -h                Show this help and exit

Interactive commands:
  /new                      Create a new session
  /use <session_id>         Switch the current session
  /sessions                 List sessions for this client
  /close [id]               Close a session (default: current)
  /cancel                   Cancel the current generation
  /sys <message>            Update the global system message
  /health                   Query the server health
  /capabilities             Show the server capability matrix
  /capabilities refresh     Force a fresh capability fetch
  /thinking on|off          Toggle thinking mode
  /text <file>              Attach a text file to the next turn
  /image <file>             Attach an image file to the next turn
  /attach                   List currently attached files
  /clear                    Clear all attached files
  /schema <file>            Load JSON Schema from file
  /schema template          Load the built-in detector template
  /schema off               Disable Structured Output
  /schema                   Show the current schema
  /schema-name <name>       Set the schema name
  /strict on|off            Toggle strict mode
  /help                     Show this help
  /quit, /exit              Quit

Anything else is sent to the current session as user input.
Pending attachments are sent with that turn and then cleared.
The active schema (if any) is applied to every subsequent turn
until it is disabled with /schema off.

Environment:
  NO_COLOR=1                Disable ANSI styles");
        }
    }

    // ========================================================================
    // Turn state
    // ========================================================================

    internal sealed class TurnState
    {
        public volatile bool Finished;
        public volatile string Reason = "";
    }

    // ========================================================================
    // Unified dispatch for the four generate combinations
    // ========================================================================
    //
    //   schema + attachments  ->  GenerateWithAttachmentsAndSchema
    //   schema only           ->  GenerateWithJsonSchema
    //   attachments only      ->  GenerateWithAttachments
    //   neither               ->  Generate
    //
    // The caller is responsible for clearing pending attachments after a
    // successful send. The schema is not cleared here: it is sticky.

    internal static class TurnDispatcher
    {
        public static bool SendTurnCombined(
            Client client,
            string text,
            IReadOnlyList<TextAttachment> texts,
            IReadOnlyList<ImageAttachment> images,
            PendingSchema schema,
            ref string sessionId,
            out string error)
        {
            bool hasAttachments = texts.Count > 0 || images.Count > 0;
            bool hasSchema = schema.Active && !string.IsNullOrEmpty(schema.Body);

            if (hasSchema && hasAttachments)
            {
                return client.GenerateWithAttachmentsAndSchema(
                    text, "", texts, images,
                    schema.Name, schema.Body, schema.Strict,
                    ref sessionId, out error);
            }
            if (hasSchema)
            {
                return client.GenerateWithJsonSchema(
                    text, "", schema.Name, schema.Body, schema.Strict,
                    ref sessionId, out error);
            }
            if (hasAttachments)
            {
                return client.GenerateWithAttachments(
                    text, "", texts, images,
                    ref sessionId, out error);
            }
            return client.Generate(text, "", ref sessionId, out error);
        }
    }

    // ========================================================================
    // Session list printer
    // ========================================================================

    internal static class SessionsPrinter
    {
        public static void Print(string sessionsJson, string currentSessionId)
        {
            try
            {
                if (JsonNode.Parse(sessionsJson) is not JsonObject j ||
                    j["sessions"] is not JsonArray arr)
                {
                    Console.WriteLine("[Client] No sessions reported.");
                    return;
                }
                if (arr.Count == 0)
                {
                    Console.WriteLine("[Client] No sessions for this client.");
                    return;
                }
                Console.WriteLine($"[Client] {arr.Count} session(s):");
                foreach (var s in arr)
                {
                    if (s is not JsonObject so) continue;
                    string sid = so["session_id"]?.GetValue<string>() ?? "?";
                    string status = so["status"]?.GetValue<string>() ?? "?";
                    string count = so["message_count"]?.GetValue<int>().ToString() ?? "?";
                    string marker = sid == currentSessionId ? " *" : "  ";
                    Console.WriteLine($"{marker} {sid}  status={status}  messages={count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Client] Failed to parse sessions: " + ex.Message);
            }
        }
    }

    // ========================================================================
    // Capability printer
    // ========================================================================

    internal static class CapabilitiesPrinter
    {
        public static void Print(Client c)
        {
            if (!c.HasCapabilityInfo)
            {
                Console.WriteLine("[Client] No capability information available.");
                return;
            }
            Console.WriteLine("[Client] Server kind: " +
                (string.IsNullOrEmpty(c.ServerKind) ? "unknown" : c.ServerKind));

            try
            {
                if (JsonNode.Parse(c.CapabilitiesJson) is not JsonObject j ||
                    j["capabilities"] is not JsonObject caps)
                {
                    Console.WriteLine("[Client] Capability matrix is empty.");
                    return;
                }

                var supported = new List<string>();
                var unsupported = new List<string>();

                foreach (var kv in caps)
                {
                    int flag = 0;
                    try { flag = kv.Value?.GetValue<int>() ?? 0; } catch { }
                    if (flag == 1) supported.Add(kv.Key);
                    else unsupported.Add(kv.Key);
                }
                supported.Sort();
                unsupported.Sort();

                Console.WriteLine($"[Client] Supported APIs ({supported.Count}):");
                foreach (var name in supported) Console.WriteLine($"    [1] {name}");

                if (unsupported.Count > 0)
                {
                    Console.WriteLine($"[Client] Unsupported APIs ({unsupported.Count}):");
                    foreach (var name in unsupported) Console.WriteLine($"    [0] {name}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Client] Failed to parse capabilities: " + ex.Message);
            }
        }
    }

    // ========================================================================
    // Schema helpers
    // ========================================================================

    internal static class SchemaHelpers
    {
        public static bool ValidateSchemaObject(string body, out string err)
        {
            err = "";
            if (string.IsNullOrEmpty(body))
            {
                err = "schema body is empty";
                return false;
            }
            try
            {
                if (JsonNode.Parse(body) is not JsonObject)
                {
                    err = "schema body is not a JSON object";
                    return false;
                }
            }
            catch (Exception ex)
            {
                err = "schema is not valid JSON: " + ex.Message;
                return false;
            }
            return true;
        }

        public static bool ReadTextFile(string path, out string body, out string err)
        {
            body = "";
            err = "";
            try
            {
                body = File.ReadAllText(path, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                err = "cannot read file " + path + ": " + ex.Message;
                return false;
            }
        }

        public static void PrintStatus(PendingSchema schema)
        {
            if (!schema.Active)
            {
                Console.WriteLine("[Client] Structured Output: disabled.");
                return;
            }
            Console.WriteLine(
                $"[Client] Structured Output: enabled. " +
                $"name={schema.Name}, strict={(schema.Strict ? "on" : "off")}, " +
                $"body={schema.Body.Length} chars.");
        }
    }

    // ========================================================================
    // Interactive command handler
    // ========================================================================
    //
    // Returns false if the caller should exit the loop.

    internal static class Commands
    {
        public static bool Handle(
            Client client,
            string line,
            ref string sessionId,
            List<TextAttachment> pendingTexts,
            List<ImageAttachment> pendingImages,
            PendingSchema schema)
        {
            // ---- Split command and argument -----------------------------
            string cmd, arg;
            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            if (sp < 0)
            {
                cmd = line.Substring(1);
                arg = "";
            }
            else
            {
                cmd = line.Substring(1, sp - 1);
                arg = line.Substring(sp + 1).Trim();
            }
            cmd = cmd.ToLowerInvariant();

            // ---- Exit ---------------------------------------------------
            if (cmd == "quit" || cmd == "exit") return false;

            // ---- Help ---------------------------------------------------
            if (cmd == "help")
            {
                Console.WriteLine(
@"Commands:
  /new                      Create a new session
  /use <session_id>         Switch the current session
  /sessions                 List sessions for this client
  /close [id]               Close a session (default: current)
  /cancel                   Cancel the current generation
  /sys <message>            Update the global system message
  /health                   Query the server health
  /capabilities             Show the server capability matrix
  /capabilities refresh     Force a fresh capability fetch
  /thinking on|off          Toggle thinking mode
  /text <file>              Attach a text file to the next turn
  /image <file>             Attach an image file to the next turn
  /attach                   List currently attached files
  /clear                    Clear all attached files
  /schema <file>            Load JSON Schema from file
  /schema template          Load the built-in detector template
  /schema off               Disable Structured Output
  /schema                   Show the current schema
  /schema-name <name>       Set the schema name
  /strict on|off            Toggle strict mode
  /help                     Show this help
  /quit, /exit              Quit
Anything else is sent to the current session as user input.
Pending attachments are sent with that turn and then cleared.
The active schema (if any) applies to every subsequent turn until
disabled with /schema off.");
                return true;
            }

            // ---- /new ---------------------------------------------------
            if (cmd == "new")
            {
                if (!client.CreateSession("", out var sid, out var err))
                {
                    Console.Error.WriteLine("[Client] Failed to create session: " + err);
                }
                else
                {
                    sessionId = sid;
                    client.SetCurrentSessionId(sid);
                    if (pendingTexts.Count > 0 || pendingImages.Count > 0)
                    {
                        pendingTexts.Clear();
                        pendingImages.Clear();
                        Console.WriteLine("[Client] Pending attachments cleared.");
                    }
                    Console.WriteLine("[Client] Created session " + sid);
                    if (schema.Active)
                    {
                        Console.WriteLine(
                            "[Client] Structured Output remains active " +
                            $"(name={schema.Name}, strict={(schema.Strict ? "on" : "off")}).");
                    }
                }
                return true;
            }

            // ---- /use ---------------------------------------------------
            if (cmd == "use")
            {
                if (string.IsNullOrEmpty(arg))
                {
                    Console.WriteLine("[Client] Usage: /use <session_id>");
                }
                else
                {
                    sessionId = arg;
                    client.SetCurrentSessionId(arg);
                    Console.WriteLine("[Client] Now using session " + arg);
                }
                return true;
            }

            // ---- /sessions ----------------------------------------------
            if (cmd == "sessions" || cmd == "list")
            {
                if (!client.ListSessions(out var sessionsJson, out var err))
                {
                    Console.Error.WriteLine("[Client] Failed to list sessions: " + err);
                }
                else
                {
                    SessionsPrinter.Print(sessionsJson, sessionId);
                }
                return true;
            }

            // ---- /close -------------------------------------------------
            if (cmd == "close")
            {
                string target = string.IsNullOrEmpty(arg) ? sessionId : arg;
                if (string.IsNullOrEmpty(target))
                {
                    Console.WriteLine("[Client] No session to close.");
                }
                else if (!client.CloseSession(target, true, out var err))
                {
                    Console.Error.WriteLine("[Client] Failed to close session: " + err);
                }
                else
                {
                    Console.WriteLine("[Client] Session " + target + " closed.");
                    if (target == sessionId)
                    {
                        sessionId = "";
                        if (pendingTexts.Count > 0 || pendingImages.Count > 0)
                        {
                            pendingTexts.Clear();
                            pendingImages.Clear();
                            Console.WriteLine("[Client] Pending attachments cleared.");
                        }
                    }
                }
                return true;
            }

            // ---- /cancel ------------------------------------------------
            if (cmd == "cancel")
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    Console.WriteLine("[Client] No current session.");
                }
                else if (!client.CancelSession(sessionId, out var err))
                {
                    Console.Error.WriteLine("[Client] Cancel failed: " + err);
                }
                else
                {
                    Console.WriteLine("[Client] Cancel requested.");
                }
                return true;
            }

            // ---- /sys ---------------------------------------------------
            if (cmd == "sys")
            {
                if (string.IsNullOrEmpty(arg))
                {
                    Console.WriteLine("[Client] Usage: /sys <message>");
                }
                else if (!client.SetSystemMessage(arg, out var err))
                {
                    Console.Error.WriteLine("[Client] Failed to set system message: " + err);
                }
                else
                {
                    Console.WriteLine("[Client] Global default system message updated.");
                }
                return true;
            }

            // ---- /health ------------------------------------------------
            if (cmd == "health")
            {
                if (!client.Health(out var hj, out var err))
                {
                    Console.Error.WriteLine("[Client] Health check failed: " + err);
                }
                else
                {
                    Console.WriteLine("[Client] Server health:");
                    try
                    {
                        if (JsonNode.Parse(hj) is JsonObject j)
                        {
                            foreach (var kv in j)
                            {
                                Console.WriteLine($"    {kv.Key}: {kv.Value?.ToJsonString()}");
                            }
                        }
                    }
                    catch
                    {
                        Console.WriteLine(hj);
                    }
                }
                return true;
            }

            // ---- /capabilities ------------------------------------------
            if (cmd == "capabilities" || cmd == "caps")
            {
                if (arg == "refresh" || arg == "reload")
                {
                    if (!client.GetApiCapabilities(out _, out var err))
                    {
                        Console.Error.WriteLine("[Client] Failed to refresh capabilities: " + err);
                    }
                    else
                    {
                        Console.WriteLine("[Client] Capability matrix refreshed.");
                    }
                }
                CapabilitiesPrinter.Print(client);
                return true;
            }

            // ---- /thinking ----------------------------------------------
            if (cmd == "thinking")
            {
                Console.WriteLine(
                    "[Client] Thinking mode is controlled by the server. " +
                    "Use --thinking at startup or set the corresponding " +
                    "server option.");
                return true;
            }

            // ---- /text --------------------------------------------------
            if (cmd == "text")
            {
                if (string.IsNullOrEmpty(arg))
                {
                    Console.WriteLine("[Client] Usage: /text <file_path>");
                    return true;
                }
                if (!Client.BuildTextAttachmentFromFile(arg, out var att, out var err))
                {
                    Console.Error.WriteLine("[Client] Failed to load text file: " + err);
                    return true;
                }
                Console.WriteLine(
                    $"[Client] Attached text: {att.Name} " +
                    $"({att.Text.Length} chars, mime={att.Mime})");
                pendingTexts.Add(att);
                return true;
            }

            // ---- /image -------------------------------------------------
            if (cmd == "image" || cmd == "img")
            {
                if (string.IsNullOrEmpty(arg))
                {
                    Console.WriteLine("[Client] Usage: /image <file_path>");
                    return true;
                }
                if (!Client.BuildImageAttachmentFromFile(arg, out var att, out var err))
                {
                    Console.Error.WriteLine("[Client] Failed to load image file: " + err);
                    return true;
                }
                Console.WriteLine(
                    $"[Client] Attached image: {att.Name} " +
                    $"({att.DataB64.Length} base64 chars, mime={att.Mime})");
                pendingImages.Add(att);
                return true;
            }

            // ---- /attach / /attachments ---------------------------------
            if (cmd == "attach" || cmd == "attachments")
            {
                if (pendingTexts.Count == 0 && pendingImages.Count == 0 && !schema.Active)
                {
                    Console.WriteLine("[Client] No attachments or schema pending.");
                    return true;
                }
                if (pendingTexts.Count > 0 || pendingImages.Count > 0)
                {
                    Console.WriteLine("[Client] Pending attachments:");
                    foreach (var t in pendingTexts)
                    {
                        Console.WriteLine($"    [text]  {t.Name} ({t.Text.Length} chars)");
                    }
                    foreach (var im in pendingImages)
                    {
                        Console.WriteLine($"    [image] {im.Name} ({im.DataB64.Length} base64 chars)");
                    }
                }
                else
                {
                    Console.WriteLine("[Client] No pending attachments.");
                }
                SchemaHelpers.PrintStatus(schema);
                return true;
            }

            // ---- /clear -------------------------------------------------
            if (cmd == "clear")
            {
                int n = pendingTexts.Count + pendingImages.Count;
                pendingTexts.Clear();
                pendingImages.Clear();
                Console.WriteLine($"[Client] Cleared {n} pending attachment(s).");
                return true;
            }

            // ---- /schema ------------------------------------------------
            if (cmd == "schema")
            {
                if (string.IsNullOrEmpty(arg) || arg == "show")
                {
                    SchemaHelpers.PrintStatus(schema);
                    return true;
                }

                string argLower = arg.ToLowerInvariant();

                if (argLower == "off" || argLower == "disable" || argLower == "none")
                {
                    schema.Active = false;
                    schema.Body = "";
                    Console.WriteLine("[Client] Structured Output disabled.");
                    return true;
                }

                if (argLower == "template" || argLower == "detector")
                {
                    schema.Active = true;
                    schema.Name = "object_detection";
                    schema.Strict = true;
                    schema.Body = Consts.DetectorTemplate;
                    Console.WriteLine(
                        $"[Client] Loaded detector template: " +
                        $"name=object_detection, strict=on, body={schema.Body.Length} chars.");
                    return true;
                }

                // Treat the argument as a file path.
                if (!SchemaHelpers.ReadTextFile(arg, out var body, out var err))
                {
                    Console.Error.WriteLine("[Client] Failed to read schema file: " + err);
                    return true;
                }
                if (!SchemaHelpers.ValidateSchemaObject(body, out err))
                {
                    Console.Error.WriteLine("[Client] Schema file rejected: " + err);
                    return true;
                }
                schema.Active = true;
                schema.Body = body;
                Console.WriteLine(
                    $"[Client] Schema loaded: name={schema.Name}, " +
                    $"strict={(schema.Strict ? "on" : "off")}, body={schema.Body.Length} chars.");
                return true;
            }

            // ---- /schema-name -------------------------------------------
            if (cmd == "schema-name")
            {
                if (string.IsNullOrEmpty(arg))
                {
                    Console.WriteLine("[Client] Usage: /schema-name <name>");
                    return true;
                }
                schema.Name = arg;
                Console.WriteLine("[Client] Schema name set to: " + schema.Name);
                return true;
            }

            // ---- /strict ------------------------------------------------
            if (cmd == "strict")
            {
                if (string.IsNullOrEmpty(arg))
                {
                    Console.WriteLine(
                        $"[Client] Strict mode is currently " +
                        $"{(schema.Strict ? "ON" : "OFF")}. Usage: /strict on|off");
                    return true;
                }
                string a = arg.ToLowerInvariant();
                if (a is "on" or "1" or "true" or "yes") schema.Strict = true;
                else if (a is "off" or "0" or "false" or "no") schema.Strict = false;
                else
                {
                    Console.WriteLine("[Client] Usage: /strict on|off");
                    return true;
                }
                Console.WriteLine($"[Client] Strict mode set to: {(schema.Strict ? "ON" : "OFF")}");
                return true;
            }

            Console.WriteLine("[Client] Unknown command: /" + cmd);
            return true;
        }
    }

    // ========================================================================
    // One-shot mode
    // ========================================================================

    internal static class OneShotRunner
    {
        public static int Run(Client client, Options opts)
        {
            var texts = new List<TextAttachment>();
            var images = new List<ImageAttachment>();

            foreach (var path in opts.TextFiles)
            {
                if (!Client.BuildTextAttachmentFromFile(path, out var att, out var err))
                {
                    Console.Error.WriteLine("[Client] Failed to load text attachment: " + err);
                    return 1;
                }
                Console.WriteLine($"[Client] Text attachment: {att.Name} ({att.Text.Length} chars)");
                texts.Add(att);
            }

            foreach (var path in opts.ImageFiles)
            {
                if (!Client.BuildImageAttachmentFromFile(path, out var att, out var err))
                {
                    Console.Error.WriteLine("[Client] Failed to load image attachment: " + err);
                    return 1;
                }
                Console.WriteLine($"[Client] Image attachment: {att.Name} ({att.DataB64.Length} base64 chars)");
                images.Add(att);
            }

            // Optional schema loaded from the command line.
            var schema = new PendingSchema();
            if (!string.IsNullOrEmpty(opts.SchemaFile))
            {
                if (!SchemaHelpers.ReadTextFile(opts.SchemaFile, out var body, out var err))
                {
                    Console.Error.WriteLine("[Client] Failed to read schema file: " + err);
                    return 1;
                }
                if (!SchemaHelpers.ValidateSchemaObject(body, out err))
                {
                    Console.Error.WriteLine("[Client] Schema file rejected: " + err);
                    return 1;
                }
                schema.Active = true;
                schema.Body = body;
                schema.Name = opts.SchemaName;
                schema.Strict = opts.Strict;
                Console.WriteLine(
                    $"[Client] Schema loaded: name={schema.Name}, " +
                    $"strict={(schema.Strict ? "on" : "off")}, body={schema.Body.Length} chars.");
            }

            string text = opts.Content;
            if (!string.IsNullOrEmpty(opts.Prompt))
            {
                if (text.Length > 0) text += "\n\n";
                text += opts.Prompt;
            }

            if (string.IsNullOrEmpty(text) && texts.Count == 0 && images.Count == 0)
            {
                Console.Error.WriteLine(
                    "[Client] Nothing to send: provide --content, --text, or --image.");
                return 1;
            }

            string sessionId = opts.SessionId;

            if (string.IsNullOrEmpty(sessionId))
            {
                if (!client.CreateSession(opts.SystemMessage, out sessionId, out var err))
                {
                    Console.Error.WriteLine("[Client] Failed to create session: " + err);
                    return 1;
                }
                Console.WriteLine("[Client] Created session " + sessionId);
            }

            client.SetCurrentSessionId(sessionId);

            var turnState = new TurnState();
            client.OnFinish += (_, reason) =>
            {
                turnState.Reason = reason;
                turnState.Finished = true;
            };

            if (!TurnDispatcher.SendTurnCombined(
                client, text, texts, images, schema, ref sessionId, out var sendErr))
            {
                Console.Error.WriteLine("[Client] Failed to send turn: " + sendErr);
                return 1;
            }

            Console.WriteLine($"[Client] Session {sessionId} queued, streaming...");
            Console.WriteLine("------------------------------------------------------------");

            // Drive the main-loop event queue until the finish event arrives.
            while (!turnState.Finished)
            {
                client.PumpEvents(500);
            }

            Console.WriteLine();
            Console.WriteLine("------------------------------------------------------------");
            Console.WriteLine(
                $"[Client] Turn finished (reason=" +
                $"{(string.IsNullOrEmpty(turnState.Reason) ? "stop" : turnState.Reason)})");

            if (!opts.Keep && !string.IsNullOrEmpty(sessionId))
            {
                if (!client.CloseSession(sessionId, false, out var closeErr))
                {
                    Console.Error.WriteLine("[Client] Failed to close session: " + closeErr);
                }
                else
                {
                    Console.WriteLine("[Client] Session closed.");
                }
            }
            else if (!string.IsNullOrEmpty(sessionId))
            {
                Console.WriteLine($"[Client] Session {sessionId} kept alive.");
            }

            return 0;
        }
    }

    // ========================================================================
    // Interactive mode
    // ========================================================================

    internal static class InteractiveRunner
    {
        public static int Run(Client client, Options opts)
        {
            // ---- Create the initial session -----------------------------
            if (!client.CreateSession(opts.SystemMessage, out var sessionId, out var err))
            {
                Console.Error.WriteLine("[Client] Failed to create session: " + err);
                return 1;
            }
            client.SetCurrentSessionId(sessionId);
            Console.WriteLine("[Client] Created session " + sessionId);

            // ---- Initial schema, if any ---------------------------------
            var schema = new PendingSchema();
            if (!string.IsNullOrEmpty(opts.SchemaFile))
            {
                if (!SchemaHelpers.ReadTextFile(opts.SchemaFile, out var body, out var readErr))
                {
                    Console.Error.WriteLine("[Client] Failed to read schema file: " + readErr);
                    return 1;
                }
                if (!SchemaHelpers.ValidateSchemaObject(body, out var vErr))
                {
                    Console.Error.WriteLine("[Client] Schema file rejected: " + vErr);
                    return 1;
                }
                schema.Active = true;
                schema.Body = body;
                schema.Name = opts.SchemaName;
                schema.Strict = opts.Strict;
                Console.WriteLine(
                    $"[Client] Schema loaded from command line: " +
                    $"name={schema.Name}, strict={(schema.Strict ? "on" : "off")}, " +
                    $"body={schema.Body.Length} chars.");
            }

            // ---- Install the finish handler -----------------------------
            var turnState = new TurnState();
            client.OnFinish += (_, reason) =>
            {
                turnState.Reason = reason;
                turnState.Finished = true;
            };

            // ---- Pending attachments ------------------------------------
            var pendingTexts = new List<TextAttachment>();
            var pendingImages = new List<ImageAttachment>();

            var input = new InputReader();
            input.Start();

            Console.WriteLine();
            Console.WriteLine("Interactive mode. Type /help for commands, /quit to exit.");
            Console.WriteLine();

            bool turnActive = false;
            bool needPrompt = true;
            bool running = true;

            while (running)
            {
                // ---- 1. Drain LLM events --------------------------------
                client.PumpEvents(0);

                // ---- 2. React to a finished turn ------------------------
                if (turnActive && turnState.Finished)
                {
                    turnActive = false;
                    needPrompt = true;
                    Console.WriteLine();
                    Console.WriteLine("------------------------------------------------------------");
                    Console.WriteLine(
                        $"[Client] Turn finished (reason=" +
                        $"{(string.IsNullOrEmpty(turnState.Reason) ? "stop" : turnState.Reason)})");
                    turnState.Finished = false;
                    turnState.Reason = "";
                }

                // ---- 3. Print prompt if due -----------------------------
                if (needPrompt && !turnActive)
                {
                    Console.Write("> ");
                    Console.Out.Flush();
                    needPrompt = false;
                }

                // ---- 4. Process one user input line ---------------------
                if (input.TryPop(out var line))
                {
                    Console.WriteLine();

                    line = line.TrimStart(' ', '\t', '\r', '\n');
                    if (line.Length == 0)
                    {
                        needPrompt = !turnActive;
                        continue;
                    }

                    if (opts.Debug)
                    {
                        Console.Error.Write("[DEBUG] line bytes:");
                        foreach (var b in Encoding.UTF8.GetBytes(line))
                        {
                            Console.Error.Write($" {b:X2}");
                        }
                        Console.Error.WriteLine();
                    }

                    // ---- Slash commands ---------------------------------
                    if (line[0] == '/')
                    {
                        if (turnActive)
                        {
                            Console.WriteLine(
                                "[Client] A turn is running. Please wait for it to finish.");
                            needPrompt = false;
                            continue;
                        }
                        if (!Commands.Handle(client, line, ref sessionId,
                                             pendingTexts, pendingImages, schema))
                        {
                            running = false;
                            continue;
                        }
                        needPrompt = true;
                        continue;
                    }

                    // ---- Ordinary input: send a turn --------------------
                    if (turnActive)
                    {
                        Console.WriteLine(
                            "[Client] A turn is running. Please wait for it to finish.");
                        needPrompt = false;
                        continue;
                    }

                    if (string.IsNullOrEmpty(sessionId))
                    {
                        Console.WriteLine(
                            "[Client] No current session. Use /new to create one.");
                        needPrompt = true;
                        continue;
                    }

                    turnState.Finished = false;
                    turnState.Reason = "";

                    bool hadAttachments =
                        pendingTexts.Count > 0 || pendingImages.Count > 0;

                    if (!TurnDispatcher.SendTurnCombined(
                        client, line, pendingTexts, pendingImages, schema,
                        ref sessionId, out var sendErr))
                    {
                        Console.WriteLine("[Client] Failed to send: " + sendErr);
                        needPrompt = true;
                    }
                    else
                    {
                        if (hadAttachments)
                        {
                            int nText = pendingTexts.Count;
                            int nImage = pendingImages.Count;
                            pendingTexts.Clear();
                            pendingImages.Clear();
                            Console.WriteLine(
                                $"[Client] Attachments cleared after send " +
                                $"(texts={nText}, images={nImage}).");
                        }
                        turnActive = true;
                        needPrompt = false;
                        Console.WriteLine($"[Client] Session {sessionId} queued, streaming...");
                        Console.WriteLine("------------------------------------------------------------");
                    }
                    continue;
                }

                // ---- 5. Nothing to do: sleep briefly and re-loop --------
                if (input.EofReached)
                {
                    running = false;
                    continue;
                }

                Thread.Sleep(Consts.MainLoopPollMs);
            }

            input.Stop();
            return 0;
        }
    }

    // ========================================================================
    // Entry point
    // ========================================================================

    internal static class Program
    {
        private static int Main(string[] argv)
        {
            ConsoleSetup.Apply();

            var opts = new Options();
            if (!ArgParser.Parse(argv, opts, out var parseErr))
            {
                Console.Error.WriteLine("[Client] " + parseErr);
                Console.Error.WriteLine("Run with --help for usage.");
                return 2;
            }
            if (opts.Help)
            {
                ArgParser.PrintUsage(Path.GetFileName(Environment.ProcessPath ?? "llm_csharp_tool"));
                return 0;
            }

            bool color = ConsoleSetup.UseColor();

            Console.WriteLine(
                $"[Client] Connecting to {opts.Endpoint} " +
                $"(server_app={opts.ServerApp}) ...");

            // ---- Runtime loading ----------------------------------------
            //
            // The LingoFuse C# binding resolves and loads the native
            // library lazily on the first P/Invoke call. A missing library
            // surfaces as a LingoFuseException at the first call site
            // (typically PrepareClient). We do not need an explicit
            // LoadLibrary step here: the binding handles it.
            //
            // To make an early failure obvious, we wrap the whole client
            // construction and connect() call in a try/catch.
            var client = new Client(opts.ServerApp, opts.Endpoint, opts.TimeoutMs);

            // ---- Handlers -----------------------------------------------
            client.OnChunk += (_, t) =>
            {
                Console.Write(t);
                Console.Out.Flush();
            };

            client.OnThink += (_, t) =>
            {
                if (color) Console.Write(Consts.StyleThink + t + Consts.StyleReset);
                else Console.Write(t);
                Console.Out.Flush();
            };

            client.OnError += (_, msg) =>
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("[Client] Server error: " + msg);
            };

            client.OnClosed += (sid, reason) =>
            {
                Console.WriteLine();
                Console.WriteLine($"[Client] Session {sid} closed (reason={reason})");
            };

            // The finish handler is installed by the runner, because each
            // runner owns a different TurnState.

            try
            {
                if (!client.Connect(out var connectErr))
                {
                    Console.Error.WriteLine("[Client] Connect failed: " + connectErr);
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Client] Connect threw: " + ex.Message);
                Console.Error.WriteLine(
                    "[Client] Place LingoFuse64.dll / liblingofuse.so next to " +
                    "this executable, or on the OS loader search path.");
                return 1;
            }

            Console.WriteLine($"[Client] Connected (client_name={client.ClientName})");

            // One-shot mode is triggered by any of the data-carrying
            // options. A bare --schema without --content is treated as
            // interactive mode, so an operator can load a schema and then
            // type prompts at the REPL.
            bool oneShot =
                opts.ContentSet ||
                opts.TextFiles.Count > 0 ||
                opts.ImageFiles.Count > 0;

            int rc;
            if (oneShot) rc = OneShotRunner.Run(client, opts);
            else rc = InteractiveRunner.Run(client, opts);

            Console.WriteLine();
            Console.WriteLine("[Client] Shutting down...");
            client.Disconnect();
            client.Dispose();

            Console.WriteLine("[Client] Done.");
            return rc;
        }
    }
}