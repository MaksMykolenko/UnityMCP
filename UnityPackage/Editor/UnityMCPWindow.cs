// UnityMCP — UnityMCPWindow.cs
// Editor window that shows the server status, connection info, and recent commands.
// Open via:  Window → UnityMCP

using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Unity Editor window for the UnityMCP bridge.
    /// Shows status, port configuration, and a live command log.
    /// </summary>
    public class UnityMCPWindow : EditorWindow
    {
        // ──────────────────────────────────────────────────────────────────────
        //  State
        // ──────────────────────────────────────────────────────────────────────

        private int _port = UnityMCPBridge.DEFAULT_PORT;
        private bool _autoStart = true;
        private Vector2 _logScroll;

        // Colors
        private static readonly Color ColorRunning  = new Color(0.25f, 0.85f, 0.35f);
        private static readonly Color ColorStopped  = new Color(0.85f, 0.30f, 0.25f);
        private static readonly Color ColorHeader   = new Color(0.15f, 0.15f, 0.18f);

        // ──────────────────────────────────────────────────────────────────────
        //  Window registration
        // ──────────────────────────────────────────────────────────────────────

        [MenuItem("Window/UnityMCP")]
        public static void ShowWindow()
        {
            var win = GetWindow<UnityMCPWindow>();
            win.titleContent =new GUIContent("UnityMCP", EditorGUIUtility.IconContent("NetworkView Icon").image);
            win.minSize = new Vector2(340, 420);
            win.Show();
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ──────────────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _port      = EditorPrefs.GetInt("UnityMCP_Port", UnityMCPBridge.DEFAULT_PORT);
            _autoStart = EditorPrefs.GetBool("UnityMCP_AutoStart", true);

            // Keep the window refreshing so status stays current
            EditorApplication.update += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  GUI
        // ──────────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            DrawHeader();
            GUILayout.Space(8);
            DrawStatus();
            GUILayout.Space(8);
            DrawConfiguration();
            GUILayout.Space(8);
            DrawControls();
            GUILayout.Space(8);
            DrawCommandLog();
        }

        private void DrawHeader()
        {
            // Dark header bar
            Rect headerRect = new Rect(0, 0, position.width, 54);
            EditorGUI.DrawRect(headerRect, ColorHeader);

            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUILayout.Space(12);

            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                normal = { textColor = Color.white }
            };
            GUILayout.Label("⟳ UnityMCP Bridge", titleStyle);

            GUILayout.FlexibleSpace();
            GUIStyle versionStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.6f, 0.6f, 0.7f) }
            };
            GUILayout.Label("v1.0.0", versionStyle);
            GUILayout.Space(12);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
        }

        private void DrawStatus()
        {
            bool running = UnityMCPBridge.IsRunning;
            Color statusColor = running ? ColorRunning : ColorStopped;
            string statusText = running
                ? $"● Running  —  localhost:{EditorPrefs.GetInt("UnityMCP_Port", UnityMCPBridge.DEFAULT_PORT)}"
                : "● Stopped";

            // Status pill
            GUILayout.BeginHorizontal();
            GUILayout.Space(12);
            Rect statusRect = GUILayoutUtility.GetRect(100, 30, GUILayout.ExpandWidth(true));
            GUILayout.Space(12);
            GUILayout.EndHorizontal();

            EditorGUI.DrawRect(new Rect(statusRect.x - 2, statusRect.y - 2,
                statusRect.width + 4, statusRect.height + 4), new Color(0, 0, 0, 0.15f));
            EditorGUI.DrawRect(statusRect, new Color(statusColor.r, statusColor.g, statusColor.b, 0.15f));

            GUIStyle statusStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = statusColor },
                fontSize = 13,
                padding = new RectOffset(10, 0, 7, 0)
            };
            EditorGUI.LabelField(statusRect, statusText, statusStyle);

            GUILayout.Space(16);

            // Stats row
            GUILayout.BeginHorizontal();
            GUILayout.Space(12);
            EditorGUILayout.LabelField("Commands processed:", $"{UnityMCPBridge.CommandsProcessed}",
                GUILayout.Width(position.width - 24));
            GUILayout.EndHorizontal();

            string lastError = UnityMCPBridge.LastError;
            if (!string.IsNullOrEmpty(lastError))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(12);
                GUIStyle errStyle = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = ColorStopped }, wordWrap = true };
                EditorGUILayout.LabelField($"⚠ {lastError}", errStyle);
                GUILayout.EndHorizontal();
            }
        }

        private void DrawConfiguration()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Configuration", EditorStyles.boldLabel);
            GUILayout.Space(4);

            // Port
            EditorGUI.BeginChangeCheck();
            int newPort = EditorGUILayout.IntField("Port", _port);
            if (EditorGUI.EndChangeCheck())
            {
                _port = Mathf.Clamp(newPort, 1024, 65535);
                EditorPrefs.SetInt("UnityMCP_Port", _port);
            }

            // Auto-start
            EditorGUI.BeginChangeCheck();
            bool newAutoStart = EditorGUILayout.Toggle("Auto-Start on Launch", _autoStart);
            if (EditorGUI.EndChangeCheck())
            {
                _autoStart = newAutoStart;
                EditorPrefs.SetBool("UnityMCP_AutoStart", _autoStart);
            }

            GUILayout.EndVertical();
        }

        private void DrawControls()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(12);

            bool running = UnityMCPBridge.IsRunning;

            // Start button
            GUI.enabled = !running;
            GUI.backgroundColor = ColorRunning;
            if (GUILayout.Button("▶  Start Server", GUILayout.Height(32)))
            {
                EditorPrefs.SetInt("UnityMCP_Port", _port);
                UnityMCPBridge.Start();
            }

            GUILayout.Space(8);

            // Stop button
            GUI.enabled = running;
            GUI.backgroundColor = ColorStopped;
            if (GUILayout.Button("■  Stop Server", GUILayout.Height(32)))
            {
                UnityMCPBridge.Stop();
            }

            GUI.enabled = true;
            GUI.backgroundColor = Color.white;
            GUILayout.Space(12);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // Copy config snippet
            GUILayout.BeginHorizontal();
            GUILayout.Space(12);
            if (GUILayout.Button("📋  Copy Cursor/Claude Config", GUILayout.Height(26)))
            {
                string config = GetConfigSnippet();
                EditorGUIUtility.systemCopyBuffer = config;
                Debug.Log("[UnityMCP] Config copied to clipboard:\n" + config);
            }
            GUILayout.Space(12);
            GUILayout.EndHorizontal();
        }

        private void DrawCommandLog()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Recent Commands", EditorStyles.boldLabel);
            GUILayout.Space(2);

            _logScroll = GUILayout.BeginScrollView(_logScroll,
                GUILayout.Height(Mathf.Min(position.height - 270, 180)));

            var recent = UnityMCPBridge.RecentCommands;
            if (recent == null || recent.Count == 0)
            {
                GUIStyle emptyStyle = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = new Color(0.5f, 0.5f, 0.5f) } };
                GUILayout.Label("No commands received yet.", emptyStyle);
            }
            else
            {
                GUIStyle logStyle = new GUIStyle(EditorStyles.miniLabel) { richText = true };
                foreach (string entry in recent)
                {
                    string colored = entry.Contains("error")
                        ? $"<color=#e05050>{entry}</color>"
                        : entry;
                    GUILayout.Label(colored, logStyle);
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Config snippet helper
        // ──────────────────────────────────────────────────────────────────────

        private string GetConfigSnippet()
        {
            return "{\n" +
                   "  \"mcpServers\": {\n" +
                   "    \"unity\": {\n" +
                   "      \"command\": \"uvx\",\n" +
                   "      \"args\": [\"unity-mcp\"]\n" +
                   "    }\n" +
                   "  }\n" +
                   "}";
        }
    }
}
