// UnityMCP — UnityMCPBridge.cs
// The heart of the Unity side: TCP server that receives JSON commands from the Python
// MCP server, executes them on Unity's main thread, and returns JSON responses.
//
// Architecture (mirrors blender-mcp/addon.py):
//   Background thread: TcpListener → accept → HandleClient (receive JSON, enqueue + wait)
//   Main thread (EditorApplication.update): dequeue → execute command → signal result
//   Special case (execute_code): AssemblyBuilder is async; main thread starts build,
//     buildFinished callback (also main thread) signals the waiting background thread.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMCP.Editor
{
    /// <summary>
    /// TCP bridge server that exposes Unity Editor functionality to the Python MCP server.
    /// Auto-starts when Unity opens via [InitializeOnLoad].
    /// </summary>
    [InitializeOnLoad]
    public static class UnityMCPBridge
    {
        // ──────────────────────────────────────────────────────────────────────
        //  Configuration
        // ──────────────────────────────────────────────────────────────────────

        public const int DEFAULT_PORT = 9877;

        // ──────────────────────────────────────────────────────────────────────
        //  State
        // ──────────────────────────────────────────────────────────────────────

        private static TcpListener _listener;
        private static Thread _serverThread;
        private static bool _running;
        private static int _commandsProcessed;
        private static string _lastError;
        private static readonly List<string> _recentCommands = new List<string>();

        // Pending command queue: each item is (command JSON, result holder)
        private struct PendingCommand
        {
            public JObject Command;
            public CommandResult Result;
        }

        private class CommandResult
        {
            public string Response;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static readonly ConcurrentQueue<PendingCommand> _commandQueue =
            new ConcurrentQueue<PendingCommand>();

        // Pending Roslyn compile jobs: job_id → result holder
        private static readonly ConcurrentDictionary<string, CommandResult> _codeJobs =
            new ConcurrentDictionary<string, CommandResult>();

        // Console log capture
        private static readonly List<LogEntry> _consoleLogs = new List<LogEntry>();
        private static readonly object _logLock = new object();

        // ──────────────────────────────────────────────────────────────────────
        //  Public API
        // ──────────────────────────────────────────────────────────────────────

        public static bool IsRunning => _running;
        public static int CommandsProcessed => _commandsProcessed;
        public static string LastError => _lastError;
        public static IReadOnlyList<string> RecentCommands => _recentCommands;

        // ──────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ──────────────────────────────────────────────────────────────────────

        static UnityMCPBridge()
        {
            // Hook into main thread update loop
            EditorApplication.update += ProcessCommandQueue;

            // Hook console log capture
            Application.logMessageReceived += OnLogMessage;

            // Clean up on script reload or Editor quit
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
            EditorApplication.quitting += Cleanup;

            // Auto-start (can be toggled from UnityMCPWindow)
            bool autoStart = EditorPrefs.GetBool("UnityMCP_AutoStart", true);
            if (autoStart)
            {
                Start();
            }
        }

        public static void Start()
        {
            if (_running)
            {
                Debug.Log("[UnityMCP] Server is already running.");
                return;
            }

            try
            {
                int port = EditorPrefs.GetInt("UnityMCP_Port", DEFAULT_PORT);
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();

                _running = true;
                _lastError = null;

                _serverThread = new Thread(AcceptLoop) { IsBackground = true, Name = "UnityMCP-Accept" };
                _serverThread.Start();

                Debug.Log($"[UnityMCP] Server started on localhost:{port}");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _running = false;
                Debug.LogError($"[UnityMCP] Failed to start server: {ex.Message}");
            }
        }

        public static void Stop()
        {
            _running = false;

            try { _listener?.Stop(); } catch { /* ignored */ }
            _listener = null;

            if (_serverThread != null && _serverThread.IsAlive)
            {
                _serverThread.Join(2000);
            }
            _serverThread = null;

            Debug.Log("[UnityMCP] Server stopped.");
        }

        private static void Cleanup()
        {
            Stop();
            EditorApplication.update -= ProcessCommandQueue;
            Application.logMessageReceived -= OnLogMessage;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  TCP: accept loop (background thread)
        // ──────────────────────────────────────────────────────────────────────

        private static void AcceptLoop()
        {
            _listener.Server.ReceiveTimeout = 1000; // allow checking _running
            while (_running)
            {
                try
                {
                    if (!_listener.Pending()) { Thread.Sleep(50); continue; }

                    TcpClient client = _listener.AcceptTcpClient();
                    Debug.Log($"[UnityMCP] Client connected: {client.Client.RemoteEndPoint}");

                    Thread clientThread = new Thread(() => HandleClient(client))
                    { IsBackground = true, Name = "UnityMCP-Client" };
                    clientThread.Start();
                }
                catch (Exception ex) when (_running)
                {
                    Debug.LogWarning($"[UnityMCP] AcceptLoop error: {ex.Message}");
                    Thread.Sleep(500);
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  TCP: client handler (background thread)
        // ──────────────────────────────────────────────────────────────────────

        private static void HandleClient(TcpClient client)
        {
            client.ReceiveTimeout = 0; // no timeout — we wait as long as needed
            client.SendTimeout = 30000;

            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                byte[] buffer = new byte[65536];
                StringBuilder accumulator = new StringBuilder();

                while (_running)
                {
                    try
                    {
                        // Read until we have a complete JSON object
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break; // client disconnected

                        accumulator.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                        string raw = accumulator.ToString();
                        JObject command;
                        try
                        {
                            command = JObject.Parse(raw);
                            accumulator.Clear();
                        }
                        catch (JsonException)
                        {
                            continue; // incomplete JSON — read more
                        }

                        // Special-case: ping doesn't need main-thread dispatch
                        string cmdType = command["type"]?.ToString();
                        if (cmdType == "ping")
                        {
                            SendToStream(stream, JObject.FromObject(new
                            {
                                status = "success",
                                result = new { pong = true }
                            }));
                            continue;
                        }

                        // Enqueue to main thread and BLOCK until result is ready
                        var result = new CommandResult();
                        _commandQueue.Enqueue(new PendingCommand { Command = command, Result = result });

                        // Wait with generous timeout (matches Python server's 180 s)
                        if (!result.Done.Wait(TimeSpan.FromSeconds(120)))
                        {
                            SendToStream(stream, ErrorResponse("Command timed out on main thread"));
                            continue;
                        }

                        SendToStream(stream, result.Response);
                    }
                    catch (Exception ex) when (_running)
                    {
                        Debug.LogWarning($"[UnityMCP] Client handler error: {ex.Message}");
                        break;
                    }
                }
            }

            Debug.Log("[UnityMCP] Client disconnected.");
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Main thread: command queue processing (EditorApplication.update)
        // ──────────────────────────────────────────────────────────────────────

        private static void ProcessCommandQueue()
        {
            int processed = 0;
            while (processed < 16 && _commandQueue.TryDequeue(out PendingCommand item))
            {
                processed++;
                try
                {
                    string cmdType = item.Command["type"]?.ToString();

                    // execute_code is async (AssemblyBuilder) — don't block main thread
                    if (cmdType == "execute_code")
                    {
                        HandleExecuteCode(item.Command, item.Result);
                    }
                    else if (cmdType == "install_package")
                    {
                        HandleInstallPackageAsync(item.Command, item.Result);
                    }
                    else if (cmdType == "build_project")
                    {
                        HandleBuildProjectAsync(item.Command, item.Result);
                    }
                    else
                    {
                        string response = ExecuteCommand(item.Command);
                        item.Result.Response = response;
                        item.Result.Done.Set();
                    }

                    _commandsProcessed++;

                    // Keep a rolling log of recent command types for the Editor window
                    lock (_recentCommands)
                    {
                        _recentCommands.Insert(0, $"{DateTime.Now:HH:mm:ss}  {cmdType}");
                        if (_recentCommands.Count > 20) _recentCommands.RemoveAt(20);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UnityMCP] Unhandled error in command processing: {ex}");
                    item.Result.Response = ErrorResponse(ex.Message);
                    item.Result.Done.Set();
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Command routing
        // ──────────────────────────────────────────────────────────────────────

        private static string ExecuteCommand(JObject command)
        {
            string cmdType = command["type"]?.ToString();
            JObject p = (command["params"] as JObject) ?? new JObject();

            try
            {
                return cmdType switch
                {
                    "get_scene_info"          => HandleGetSceneInfo(),
                    "get_object_info"         => HandleGetObjectInfo(p),
                    "get_hierarchy"           => HandleGetHierarchy(),
                    "find_gameobjects"        => HandleFindGameObjects(p),
                    "get_components"          => HandleGetComponents(p),
                    "create_gameobject"       => HandleCreateGameObject(p),
                    "delete_gameobject"       => HandleDeleteGameObject(p),
                    "set_transform"           => HandleSetTransform(p),
                    "set_rect_transform"      => HandleSetRectTransform(p),
                    "set_component_property"  => HandleSetComponentProperty(p),
                    "get_code_result"         => HandleGetCodeResult(p),
                    "take_screenshot"         => HandleTakeScreenshot(p),
                    "get_console_logs"        => HandleGetConsoleLogs(p),
                    "get_asset_list"          => HandleGetAssetList(p),
                    "import_asset"            => HandleImportAsset(p),
                    "get_project_settings"    => HandleGetProjectSettings(),
                    "get_play_mode_state"     => HandleGetPlayModeState(),
                    "set_play_mode_state"     => HandleSetPlayModeState(p),
                    "set_time_scale"          => HandleSetTimeScale(p),
                    "instantiate_prefab"      => HandleInstantiatePrefab(p),
                    "apply_prefab_modifications" => HandleApplyPrefabModifications(p),
                    "undo_last_action"        => HandleUndoLastAction(),
                    "redo_last_action"        => HandleRedoLastAction(),
                    _                         => ErrorResponse($"Unknown command: {cmdType}")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityMCP] Error in handler '{cmdType}': {ex}");
                return ErrorResponse(ex.Message);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Handlers — Scene inspection
        // ──────────────────────────────────────────────────────────────────────

        private static string HandleGetSceneInfo()
        {
            Scene scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();

            var objects = rootObjects.Take(20).Select(go => new
            {
                name = go.name,
                active = go.activeSelf,
                childCount = go.transform.childCount,
                position = Vec3(go.transform.position),
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer)
            }).ToArray();

            Camera mainCam = Camera.main;

            return SuccessResponse(new
            {
                scene_name = scene.name,
                scene_path = scene.path,
                object_count = scene.rootCount,
                is_dirty = scene.isDirty,
                root_objects_shown = objects.Length,
                root_objects = objects,
                main_camera = mainCam != null ? mainCam.name : null,
                ambient_color = ColorToHex(RenderSettings.ambientLight)
            });
        }

        private static string HandleGetObjectInfo(JObject p)
        {
            string name = p["name"]?.ToString()
                          ?? throw new ArgumentException("'name' is required");

            GameObject go = FindObject(name)
                            ?? throw new KeyNotFoundException($"GameObject '{name}' not found");

            Transform t = go.transform;

            var componentNames = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => c.GetType().Name)
                .ToArray();

            return SuccessResponse(new
            {
                name = go.name,
                active = go.activeSelf,
                active_in_hierarchy = go.activeInHierarchy,
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                position = Vec3(t.position),
                local_position = Vec3(t.localPosition),
                rotation_euler = Vec3(t.eulerAngles),
                local_rotation_euler = Vec3(t.localEulerAngles),
                scale = Vec3(t.localScale),
                parent = t.parent?.name,
                child_count = t.childCount,
                components = componentNames
            });
        }

        private static string HandleGetHierarchy()
        {
            Scene scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var hierarchy = roots.Select(go => BuildHierarchyNode(go)).ToArray();

            return SuccessResponse(new
            {
                scene_name = scene.name,
                total_root_count = scene.rootCount,
                hierarchy
            });
        }

        private static object BuildHierarchyNode(GameObject go)
        {
            var children = new List<object>();
            for (int i = 0; i < go.transform.childCount; i++)
            {
                children.Add(BuildHierarchyNode(go.transform.GetChild(i).gameObject));
            }

            return new
            {
                name = go.name,
                active = go.activeSelf,
                tag = go.tag,
                components = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToArray(),
                children = children.Count > 0 ? children : null
            };
        }

        private static string HandleFindGameObjects(JObject p)
        {
            string tag = p["tag"]?.ToString();
            string nameContains = p["name_contains"]?.ToString();

            // Unity 6: use FindObjectsByType instead of deprecated FindObjectsOfType
            var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);

            IEnumerable<GameObject> filtered = allObjects;

            if (!string.IsNullOrEmpty(tag) && tag != "Untagged")
            {
                filtered = filtered.Where(go =>
                {
                    try { return go.CompareTag(tag); }
                    catch { return false; }
                });
            }

            if (!string.IsNullOrEmpty(nameContains))
            {
                filtered = filtered.Where(go =>
                    go.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var results = filtered.Take(50).Select(go => new
            {
                name = go.name,
                active = go.activeSelf,
                tag = go.tag,
                position = Vec3(go.transform.position),
                parent = go.transform.parent?.name
            }).ToArray();

            return SuccessResponse(new { count = results.Length, objects = results });
        }

        private static string HandleGetComponents(JObject p)
        {
            string name = p["name"]?.ToString()
                          ?? throw new ArgumentException("'name' is required");

            GameObject go = FindObject(name)
                            ?? throw new KeyNotFoundException($"GameObject '{name}' not found");

            var components = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c =>
                {
                    var props = new Dictionary<string, string>();

                    // Serialize public properties via reflection (safe subset)
                    foreach (var prop in c.GetType()
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(pr => pr.CanRead && !pr.GetIndexParameters().Any())
                        .Take(20))
                    {
                        try
                        {
                            object val = prop.GetValue(c);
                            props[prop.Name] = val?.ToString() ?? "null";
                        }
                        catch { /* skip unreadable */ }
                    }

                    return new { type = c.GetType().Name, properties = props };
                })
                .ToArray();

            return SuccessResponse(new { gameobject = name, components });
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Handlers — Scene manipulation
        // ──────────────────────────────────────────────────────────────────────

        private static string HandleCreateGameObject(JObject p)
        {
            string name         = p["name"]?.ToString() ?? "New GameObject";
            string primitiveStr = p["primitive_type"]?.ToString() ?? "Empty";
            float[] pos         = ParseFloatArray(p["position"], 3, 0f);
            float[] rot         = ParseFloatArray(p["rotation"], 3, 0f);
            float[] scl         = ParseFloatArray(p["scale"], 3, 1f);
            string parentName   = p["parent_name"]?.ToString();

            GameObject go;

            if (primitiveStr == "Empty")
            {
                go = new GameObject(name);
            }
            else if (primitiveStr == "Camera")
            {
                go = new GameObject(name);
                go.AddComponent<Camera>();
            }
            else if (primitiveStr == "Light")
            {
                go = new GameObject(name);
                var light = go.AddComponent<Light>();
                light.type = LightType.Directional;
            }
            else if (primitiveStr == "AudioSource")
            {
                go = new GameObject(name);
                go.AddComponent<AudioSource>();
            }
            else if (Enum.TryParse(primitiveStr, out PrimitiveType primitive))
            {
                go = GameObject.CreatePrimitive(primitive);
                go.name = name;
            }
            else
            {
                throw new ArgumentException($"Unknown primitive_type '{primitiveStr}'. " +
                    "Use: Empty, Cube, Sphere, Capsule, Cylinder, Plane, Quad, Camera, Light, AudioSource");
            }

            go.transform.position    = new Vector3(pos[0], pos[1], pos[2]);
            go.transform.eulerAngles = new Vector3(rot[0], rot[1], rot[2]);
            go.transform.localScale  = new Vector3(scl[0], scl[1], scl[2]);

            if (!string.IsNullOrEmpty(parentName))
            {
                GameObject parent = FindObject(parentName);
                if (parent != null) go.transform.SetParent(parent.transform, worldPositionStays: false);
                else Debug.LogWarning($"[UnityMCP] Parent '{parentName}' not found; created at root.");
            }

            Undo.RegisterCreatedObjectUndo(go, "Create GameObject via MCP");

            // Mark the scene as dirty so changes can be saved
            EditorUtility.SetDirty(go);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            return SuccessResponse(new
            {
                created = go.name,
                instance_id = go.GetInstanceID(),
                position = Vec3(go.transform.position)
            });
        }

        private static string HandleDeleteGameObject(JObject p)
        {
            string name = p["name"]?.ToString()
                          ?? throw new ArgumentException("'name' is required");

            GameObject go = FindObject(name)
                            ?? throw new KeyNotFoundException($"GameObject '{name}' not found");

            string deletedName = go.name;
            Undo.DestroyObjectImmediate(go);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            return SuccessResponse(new { deleted = deletedName, success = true });
        }

        private static string HandleSetTransform(JObject p)
        {
            string name = p["name"]?.ToString()
                          ?? throw new ArgumentException("'name' is required");

            GameObject go = FindObject(name)
                            ?? throw new KeyNotFoundException($"GameObject '{name}' not found");

            Transform t = go.transform;
            Undo.RecordObject(t, "Set Transform via MCP");

            if (p["position"] != null)
            {
                float[] pos = ParseFloatArray(p["position"], 3, 0f);
                t.position = new Vector3(pos[0], pos[1], pos[2]);
            }

            if (p["rotation"] != null)
            {
                float[] rot = ParseFloatArray(p["rotation"], 3, 0f);
                t.eulerAngles = new Vector3(rot[0], rot[1], rot[2]);
            }

            if (p["scale"] != null)
            {
                float[] scl = ParseFloatArray(p["scale"], 3, 1f);
                t.localScale = new Vector3(scl[0], scl[1], scl[2]);
            }

            EditorUtility.SetDirty(go);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            return SuccessResponse(new
            {
                name = go.name,
                position = Vec3(t.position),
                rotation_euler = Vec3(t.eulerAngles),
                scale = Vec3(t.localScale)
            });
        }

        private static string HandleSetRectTransform(JObject p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            string name = p["name"]?.ToString() ?? throw new ArgumentException("'name' is required");

            // Execute FindObject safely on main thread to bubble errors to Python
            GameObject go = FindObject(name) ?? throw new KeyNotFoundException($"GameObject '{name}' not found");
            RectTransform rt = go.GetComponent<RectTransform>() ?? throw new MissingComponentException($"GameObject '{name}' does not have a RectTransform component.");

            EditorThreadDispatcher.Enqueue(() =>
            {
                Undo.RecordObject(rt, "Modify RectTransform");

                if (p["anchored_position"] != null)
                {
                    rt.anchoredPosition = new Vector2(
                        p["anchored_position"]["x"]?.Value<float>() ?? rt.anchoredPosition.x,
                        p["anchored_position"]["y"]?.Value<float>() ?? rt.anchoredPosition.y
                    );
                }

                if (p["size_delta"] != null)
                {
                    rt.sizeDelta = new Vector2(
                        p["size_delta"]["x"]?.Value<float>() ?? rt.sizeDelta.x,
                        p["size_delta"]["y"]?.Value<float>() ?? rt.sizeDelta.y
                    );
                }

                if (p["anchor_min"] != null)
                {
                    rt.anchorMin = new Vector2(
                        p["anchor_min"]["x"]?.Value<float>() ?? rt.anchorMin.x,
                        p["anchor_min"]["y"]?.Value<float>() ?? rt.anchorMin.y
                    );
                }

                if (p["anchor_max"] != null)
                {
                    rt.anchorMax = new Vector2(
                        p["anchor_max"]["x"]?.Value<float>() ?? rt.anchorMax.x,
                        p["anchor_max"]["y"]?.Value<float>() ?? rt.anchorMax.y
                    );
                }

                if (p["pivot"] != null)
                {
                    rt.pivot = new Vector2(
                        p["pivot"]["x"]?.Value<float>() ?? rt.pivot.x,
                        p["pivot"]["y"]?.Value<float>() ?? rt.pivot.y
                    );
                }

                EditorUtility.SetDirty(rt);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            });

            return SuccessResponse(new { success = true, action = "set_rect_transform", gameobject = name });
        }

        private static string HandleSetComponentProperty(JObject p)
        {
            string goName     = p["name"]?.ToString()         ?? throw new ArgumentException("'name' is required");
            string compType   = p["component_type"]?.ToString() ?? throw new ArgumentException("'component_type' is required");
            string propPath   = p["property_name"]?.ToString()  ?? throw new ArgumentException("'property_name' is required");
            string valueJson  = p["value"]?.ToString()          ?? throw new ArgumentException("'value' is required");

            GameObject go = FindObject(goName)
                            ?? throw new KeyNotFoundException($"GameObject '{goName}' not found");

            // Find the component by type name
            Component comp = go.GetComponents<Component>()
                .FirstOrDefault(c => c != null &&
                    (c.GetType().Name == compType || c.GetType().FullName == compType))
                ?? throw new MissingComponentException($"Component '{compType}' not found on '{goName}'");

            // Support dotted paths like "material.color"
            string[] parts = propPath.Split('.');
            object target = comp;
            Type targetType = comp.GetType();

            // Traverse intermediate path segments
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var member = targetType.GetProperty(parts[i], BindingFlags.Public | BindingFlags.Instance)?.GetValue(target)
                          ?? targetType.GetField(parts[i], BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
                if (member == null) throw new MissingMemberException($"Property '{parts[i]}' not found on {targetType.Name}");
                target = member;
                targetType = target.GetType();
            }

            Undo.RecordObject(comp, "Set Component Property via MCP");

            // Set the final property
            string finalProp = parts[^1];
            PropertyInfo pi = targetType.GetProperty(finalProp, BindingFlags.Public | BindingFlags.Instance);
            if (pi != null && pi.CanWrite)
            {
                object converted = Convert.ChangeType(
                    JsonConvert.DeserializeObject(valueJson, pi.PropertyType), pi.PropertyType);
                pi.SetValue(target, converted);
            }
            else
            {
                FieldInfo fi = targetType.GetField(finalProp, BindingFlags.Public | BindingFlags.Instance)
                               ?? throw new MissingMemberException($"Writable property or field '{finalProp}' not found on {targetType.Name}");
                object converted = Convert.ChangeType(
                    JsonConvert.DeserializeObject(valueJson, fi.FieldType), fi.FieldType);
                fi.SetValue(target, converted);
            }

            EditorUtility.SetDirty(comp);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            return SuccessResponse(new
            {
                gameobject = goName,
                component = compType,
                property = propPath,
                set_to = valueJson
            });
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Handlers — Play Mode and Time
        // ──────────────────────────────────────────────────────────────────────

        private static string HandleGetPlayModeState()
        {
            // ExecuteCommand is already on the main thread, but to strictly abide
            // by the intent of thread-safe property access if invoked otherwise:
            bool isPlaying = false;
            bool isPaused = false;
            bool isCompiling = false;

            // We must read these on the main thread. We can do it directly here 
            // since ProcessCommandQueue is on the main thread.
            isPlaying = EditorApplication.isPlaying;
            isPaused = EditorApplication.isPaused;
            isCompiling = EditorApplication.isCompiling;

            string state = isPlaying ? (isPaused ? "paused" : "playing") : "stopped";

            return SuccessResponse(new
            {
                state,
                isPlaying,
                isPaused,
                isCompiling
            });
        }

        private static string HandleSetPlayModeState(JObject p)
        {
            string action = p["action"]?.ToString() ?? throw new ArgumentException("'action' is required");

            // CRITICAL: Wrap Unity API calls inside EditorThreadDispatcher.Enqueue 
            // because they must execute on the Unity Main Thread.
            EditorThreadDispatcher.Enqueue(() =>
            {
                if (action == "play") { EditorApplication.isPlaying = true; EditorApplication.isPaused = false; }
                else if (action == "stop") { EditorApplication.isPlaying = false; }
                else if (action == "pause") { EditorApplication.isPaused = true; }
                else if (action == "unpause") { EditorApplication.isPaused = false; }
                else if (action == "step") { EditorApplication.Step(); }
                else throw new ArgumentException($"Invalid play mode action: {action}");
            });

            return SuccessResponse(new { success = true, action });
        }

        private static string HandleSetTimeScale(JObject p)
        {
            if (p["scale"] == null) throw new ArgumentException("'scale' is required");
            float scale = p["scale"].Value<float>();

            // CRITICAL: Wrap Unity API calls inside EditorThreadDispatcher.Enqueue
            EditorThreadDispatcher.Enqueue(() =>
            {
                UnityEngine.Time.timeScale = scale;
            });

            return SuccessResponse(new { success = true, scale });
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Handlers — Prefabs and Undo/Redo
        // ──────────────────────────────────────────────────────────────────────

        private static string HandleInstantiatePrefab(JObject p)
        {
            string assetPath = p["asset_path"]?.ToString() ?? throw new ArgumentException("'asset_path' is required");
            float[] pos = ParseFloatArray(p["position"], 3, 0f);
            float[] rot = ParseFloatArray(p["rotation"], 3, 0f);

            EditorThreadDispatcher.Enqueue(() =>
            {
                UnityEngine.Object prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefabAsset == null) throw new FileNotFoundException($"Prefab not found at '{assetPath}'");

                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
                instance.transform.position = new Vector3(pos[0], pos[1], pos[2]);
                instance.transform.eulerAngles = new Vector3(rot[0], rot[1], rot[2]);

                Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab via MCP");
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            });

            return SuccessResponse(new { success = true, action = "instantiate_prefab", asset_path = assetPath });
        }

        private static string HandleApplyPrefabModifications(JObject p)
        {
            string gameobjectName = p["gameobject_name"]?.ToString() ?? throw new ArgumentException("'gameobject_name' is required");

            EditorThreadDispatcher.Enqueue(() =>
            {
                GameObject go = FindObject(gameobjectName) ?? throw new KeyNotFoundException($"GameObject '{gameobjectName}' not found");

                if (!PrefabUtility.IsPartOfPrefabInstance(go))
                    throw new InvalidOperationException($"GameObject '{gameobjectName}' is not a prefab instance.");

                PrefabUtility.ApplyPrefabInstance(go, InteractionMode.UserAction);
            });

            return SuccessResponse(new { success = true, action = "apply_prefab_modifications", gameobject = gameobjectName });
        }

        private static string HandleUndoLastAction()
        {
            EditorThreadDispatcher.Enqueue(() =>
            {
                Undo.PerformUndo();
            });

            return SuccessResponse(new { success = true, action = "undo" });
        }

        private static string HandleRedoLastAction()
        {
            EditorThreadDispatcher.Enqueue(() =>
            {
                Undo.PerformRedo();
            });

            return SuccessResponse(new { success = true, action = "redo" });
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Handlers — Packages and Build Pipeline
        // ──────────────────────────────────────────────────────────────────────

        private static async void HandleInstallPackageAsync(JObject command, CommandResult result)
        {
            try
            {
                JObject p = (command["params"] as JObject) ?? new JObject();
                string packageId = p["package_id"]?.ToString() ?? throw new ArgumentException("'package_id' is required");

                UnityEditor.PackageManager.Requests.AddRequest request = null;

                EditorThreadDispatcher.Enqueue(() =>
                {
                    request = UnityEditor.PackageManager.Client.Add(packageId);
                });

                // Wait until the Enqueue has executed and created the request
                while (request == null)
                {
                    await Task.Delay(50);
                }

                // Poll until the UPM operation completes
                while (!request.IsCompleted)
                {
                    await Task.Delay(100);
                }

                if (request.Status == UnityEditor.PackageManager.StatusCode.Success)
                {
                    result.Response = SuccessResponse(new { success = true, package_id = packageId });
                }
                else
                {
                    result.Response = ErrorResponse($"Failed to install package '{packageId}': {request.Error?.message}");
                }
            }
            catch (Exception ex)
            {
                result.Response = ErrorResponse(ex.Message);
            }
            finally
            {
                result.Done.Set();
            }
        }

        private static async void HandleBuildProjectAsync(JObject command, CommandResult result)
        {
            try
            {
                JObject p = (command["params"] as JObject) ?? new JObject();
                string targetPlatform = p["target_platform"]?.ToString() ?? throw new ArgumentException("'target_platform' is required");
                string outputPath = p["output_path"]?.ToString() ?? throw new ArgumentException("'output_path' is required");

                BuildTarget buildTarget = targetPlatform.ToLower() switch
                {
                    "windows" => BuildTarget.StandaloneWindows64,
                    "macos" => BuildTarget.StandaloneOSX,
                    "android" => BuildTarget.Android,
                    "ios" => BuildTarget.iOS,
                    "webgl" => BuildTarget.WebGL,
                    _ => throw new ArgumentException($"Unsupported target platform: {targetPlatform}. Use 'windows', 'macos', 'android', 'ios', or 'webgl'.")
                };

                UnityEditor.Build.Reporting.BuildReport report = null;
                bool isDone = false;

                EditorThreadDispatcher.Enqueue(() =>
                {
                    try
                    {
                        string[] scenes = EditorBuildSettings.scenes
                            .Where(s => s.enabled)
                            .Select(s => s.path)
                            .ToArray();

                        var buildOptions = new BuildPlayerOptions
                        {
                            scenes = scenes,
                            locationPathName = outputPath,
                            target = buildTarget,
                            options = BuildOptions.None
                        };

                        report = BuildPipeline.BuildPlayer(buildOptions);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[UnityMCP] Build failed with exception: {ex.Message}");
                    }
                    finally
                    {
                        isDone = true;
                    }
                });

                while (!isDone)
                {
                    await Task.Delay(200);
                }

                if (report != null && report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
                {
                    result.Response = SuccessResponse(new { success = true, output = outputPath, duration_seconds = report.summary.totalTime.TotalSeconds });
                }
                else
                {
                    int errors = report?.summary.totalErrors ?? 1;
                    result.Response = ErrorResponse($"Build failed with {errors} errors. Check Unity console for details.");
                }
            }
            catch (Exception ex)
            {
                result.Response = ErrorResponse(ex.Message);
            }
            finally
            {
                result.Done.Set();
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Handlers — C# Code Execution via Roslyn (AssemblyBuilder)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>Called on the main thread. Starts async Roslyn compilation.</summary>
        private static void HandleExecuteCode(JObject command, CommandResult commandResult)
        {
            JObject p = (command["params"] as JObject) ?? new JObject();
            string code = p["code"]?.ToString() ?? string.Empty;

            // Generate a unique job id
            string jobId = Guid.NewGuid().ToString("N")[..8];

            // Build temp directory
            string tempDir = Path.Combine(Path.GetTempPath(), "UnityMCP", "CodeExec");
            Directory.CreateDirectory(tempDir);

            string sourceFile = Path.Combine(tempDir, $"MCPExec_{jobId}.cs");
            string outputDll  = Path.Combine(tempDir, $"MCPExec_{jobId}.dll");

            // Wrap user code in a static class with a print() helper
            string wrapped = WrapCode(code);
            File.WriteAllText(sourceFile, wrapped);

            // Register the job result holder
            var jobResult = new CommandResult();
            _codeJobs[jobId] = jobResult;

            // Immediately signal the TCP thread with job info so it can respond
            // to the Python server; Python will then poll get_code_result.
            commandResult.Response = JsonConvert.SerializeObject(new
            {
                status = "success",
                result = new
                {
                    status = "compiling",
                    job_id = jobId,
                    message = "Compilation started — call get_code_result with this job_id."
                }
            });
            commandResult.Done.Set();

            // Set up AssemblyBuilder (Unity's Roslyn wrapper)
            var builder = new AssemblyBuilder(outputDll, new[] { sourceFile })
            {
                flags = AssemblyBuilderFlags.EditorAssembly,
                referencesOptions = ReferencesOptions.UseEngineModules
            };

            builder.buildFinished += (dllPath, messages) =>
            {
                // This callback fires on the main thread in a future EditorApplication.update.
                try
                {
                    var errors = messages?
                        .Where(m => m.type == CompilerMessageType.Error)
                        .Select(m => $"[Line {m.line}] {m.message}")
                        .ToArray() ?? Array.Empty<string>();

                    if (errors.Length > 0)
                    {
                        jobResult.Response = FormattedJobResult(jobId, null,
                            "Compilation failed:\n" + string.Join("\n", errors));
                        jobResult.Done.Set();
                        TryDelete(sourceFile);
                        return;
                    }

                    // Load and execute
                    System.Reflection.Assembly asm = System.Reflection.Assembly.LoadFile(dllPath);
                    Type type = asm.GetType("__MCPExecutor")
                                ?? throw new Exception("__MCPExecutor class not found in compiled assembly");
                    MethodInfo execute = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)
                                        ?? throw new Exception("Execute() method not found");

                    // Redirect Console.Out to capture print() calls and Debug.Log output
                    using var sw = new StringWriter();
                    Console.SetOut(sw);
                    try
                    {
                        var returnValue = execute.Invoke(null, null);
                        string output = sw.ToString();
                        if (!string.IsNullOrWhiteSpace(returnValue?.ToString()))
                        {
                            output += returnValue.ToString();
                        }
                        jobResult.Response = FormattedJobResult(jobId, output, null);
                    }
                    catch (TargetInvocationException tie)
                    {
                        jobResult.Response = FormattedJobResult(jobId, sw.ToString(),
                            $"Runtime error: {tie.InnerException?.Message ?? tie.Message}");
                    }
                    finally
                    {
                        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                    }
                }
                catch (Exception ex)
                {
                    jobResult.Response = FormattedJobResult(jobId, null,
                        $"Assembly load/execute error: {ex.Message}");
                }
                finally
                {
                    jobResult.Done.Set();
                    TryDelete(sourceFile);
                }
            };

            if (!builder.Build())
            {
                _codeJobs.TryRemove(jobId, out _);
                // Update the already-signaled result would be wrong; just log
                Debug.LogError("[UnityMCP] AssemblyBuilder.Build() returned false — build could not start.");
            }
        }

        private static string HandleGetCodeResult(JObject p)
        {
            string jobId = p["job_id"]?.ToString()
                           ?? throw new ArgumentException("'job_id' is required");

            if (!_codeJobs.TryGetValue(jobId, out CommandResult jobResult))
            {
                return ErrorResponse($"No job found with id '{jobId}'");
            }

            if (!jobResult.Done.IsSet)
            {
                return SuccessResponse(new { status = "compiling", job_id = jobId });
            }

            _codeJobs.TryRemove(jobId, out _);
            return jobResult.Response;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Handlers — Screenshot
        // ──────────────────────────────────────────────────────────────────────

        private static string HandleTakeScreenshot(JObject p)
        {
            string filePath = p["filepath"]?.ToString()
                              ?? Path.Combine(Path.GetTempPath(), $"unity_mcp_shot_{DateTime.Now:yyyyMMddHHmmss}.png");
            string view = p["view"]?.ToString() ?? "game";

            // ScreenCapture works in Game view; for Scene view we use EditorWindow approach
            ScreenCapture.CaptureScreenshot(filePath);

            // Wait briefly for the file to be written
            for (int i = 0; i < 20; i++)
            {
                if (File.Exists(filePath) && new FileInfo(filePath).Length > 0) break;
                Thread.Sleep(100);
            }

            if (!File.Exists(filePath))
            {
                return ErrorResponse("Screenshot file not created");
            }

            byte[] bytes = File.ReadAllBytes(filePath);
            string base64 = Convert.ToBase64String(bytes);

            return SuccessResponse(new
            {
                filepath = filePath,
                image_base64 = base64,
                width = 0, // actual dims require loading the PNG header
                view
            });
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Handlers — Console logs
        // ──────────────────────────────────────────────────────────────────────

        private struct LogEntry
        {
            public string Type;
            public string Message;
            public string StackTrace;
            public string Timestamp;
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            lock (_logLock)
            {
                _consoleLogs.Add(new LogEntry
                {
                    Type = type.ToString(),
                    Message = message,
                    StackTrace = stackTrace,
                    Timestamp = DateTime.Now.ToString("HH:mm:ss.fff")
                });

                // Keep last 500 entries
                if (_consoleLogs.Count > 500)
                    _consoleLogs.RemoveAt(0);
            }
        }

        private static string HandleGetConsoleLogs(JObject p)
        {
            int maxLogs = p["max_logs"]?.ToObject<int>() ?? 50;
            string logTypes = p["log_types"]?.ToString() ?? "all";

            List<LogEntry> entries;
            lock (_logLock)
            {
                entries = _consoleLogs
                    .Where(e => logTypes == "all" ||
                                e.Type.Equals(logTypes, StringComparison.OrdinalIgnoreCase) ||
                                (logTypes == "error" && e.Type == "Error") ||
                                (logTypes == "warning" && e.Type == "Warning") ||
                                (logTypes == "log" && e.Type == "Log"))
                    .TakeLast(maxLogs)
                    .ToList();
            }

            var logs = entries.Select(e => new
            {
                type = e.Type,
                message = e.Message,
                timestamp = e.Timestamp,
                stack_trace = e.StackTrace?.Split('\n').FirstOrDefault() // only first line
            }).ToArray();

            return SuccessResponse(new { count = logs.Length, logs });
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Handlers — Asset management
        // ──────────────────────────────────────────────────────────────────────

        private static string HandleGetAssetList(JObject p)
        {
            string folder = p["folder"]?.ToString() ?? "Assets";
            string filter = p["filter"]?.ToString() ?? "";

            string[] guids = AssetDatabase.FindAssets(filter, new[] { folder });
            var assets = guids.Take(100).Select(guid =>
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                return new
                {
                    path,
                    name = Path.GetFileNameWithoutExtension(path),
                    extension = Path.GetExtension(path),
                    guid
                };
            }).ToArray();

            return SuccessResponse(new
            {
                folder,
                filter,
                total_found = guids.Length,
                shown = assets.Length,
                assets
            });
        }

        private static string HandleImportAsset(JObject p)
        {
            string assetPath = p["asset_path"]?.ToString()
                               ?? throw new ArgumentException("'asset_path' is required");

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            return SuccessResponse(new { imported = assetPath, success = true });
        }

        private static string HandleGetProjectSettings()
        {
            return SuccessResponse(new
            {
                company_name = PlayerSettings.companyName,
                product_name = PlayerSettings.productName,
                version = PlayerSettings.bundleVersion,
                build_target = EditorUserBuildSettings.activeBuildTarget.ToString(),
                scripting_backend = PlayerSettings.GetScriptingBackend(
                    EditorUserBuildSettings.selectedBuildTargetGroup).ToString(),
                color_space = QualitySettings.activeColorSpace.ToString(),
                render_pipeline = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline != null
                    ? UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline.GetType().Name
                    : "Built-in",
                physics_gravity = Vec3(Physics.gravity),
                vsync_count = QualitySettings.vSyncCount
            });
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Roslyn code wrapping helpers
        // ──────────────────────────────────────────────────────────────────────

        private static string WrapCode(string code)
        {
            return $@"
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using Object = UnityEngine.Object;

public static class __MCPExecutor
{{
    private static readonly StringBuilder __sb = new StringBuilder();

    /// <summary>Helper: append a value to the output (returned to MCP client).</summary>
    public static void print(object value)
    {{
        __sb.AppendLine(value?.ToString() ?? ""null"");
        Debug.Log(""[MCP] "" + (value?.ToString() ?? ""null""));
    }}

    public static string Execute()
    {{
        __sb.Clear();
        {code}
        return __sb.ToString();
    }}
}}
";
        }

        private static string FormattedJobResult(string jobId, string output, string error)
        {
            if (error != null)
            {
                return JsonConvert.SerializeObject(new
                {
                    status = "success",
                    result = new { status = "error", job_id = jobId, error }
                });
            }

            return JsonConvert.SerializeObject(new
            {
                status = "success",
                result = new { status = "done", job_id = jobId, output = output ?? "" }
            });
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────────────────────────────

        private static GameObject FindObject(string name)
        {
            // Try exact name first (O(1) via Unity's internal lookup)
            var go = GameObject.Find(name);
            if (go != null) return go;

            // Fall back to full scene scan for partial matches
            return UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None)
                .FirstOrDefault(g => g.name == name);
        }

        private static float[] ParseFloatArray(JToken token, int size, float defaultVal)
        {
            if (token == null) return Enumerable.Repeat(defaultVal, size).ToArray();
            try { return token.ToObject<float[]>(); }
            catch { return Enumerable.Repeat(defaultVal, size).ToArray(); }
        }

        private static object Vec3(Vector3 v) => new { x = Round(v.x), y = Round(v.y), z = Round(v.z) };
        private static float Round(float f) => (float)Math.Round(f, 4);

        private static string ColorToHex(Color c) =>
            $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}";

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignored */ }
        }

        private static void SendToStream(NetworkStream stream, string json)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            lock (stream) { stream.Write(data, 0, data.Length); stream.Flush(); }
        }

        private static void SendToStream(NetworkStream stream, JObject obj) =>
            SendToStream(stream, obj.ToString(Formatting.None));

        private static string SuccessResponse(object result) =>
            JsonConvert.SerializeObject(new { status = "success", result });

        private static string ErrorResponse(string message) =>
            JsonConvert.SerializeObject(new { status = "error", message });
    }
}
