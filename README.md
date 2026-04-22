# UnityMCP — Model Context Protocol for Unity Editor

**Connects Unity Editor to Claude / Cursor / VS Code via MCP**, allowing AI to control the scene, manipulate objects, and execute arbitrary C# code through Roslyn.

---

## 🏗 Architecture

```
Claude / Cursor / VS Code
        ↕  MCP Protocol (stdio)
   Python: src/unity_mcp/server.py   ← FastMCP Server
        ↕  TCP Socket :9877 (JSON)
   C#: UnityPackage/Editor/UnityMCPBridge.cs   ← TCP Server inside Unity
        ↕  UnityEditor C# API + Roslyn
   Unity 6 Editor
```

---

## 🛠 Installation

### Prerequisites

- **Unity 6** (or newer)
- **Python 3.10+**
- **uv** package manager

To install `uv` on macOS:
```bash
brew install uv
```

---

### Step 1 — Install the Unity Package

1. Open your Unity 6 project.
2. Go to **Window → Package Manager**.
3. Click the **+** icon in the top-left corner → **Add package from disk...**
4. Locate the `UnityPackage/package.json` file from this repository and select it.
5. The Package Manager will automatically fetch necessary dependencies like `Newtonsoft.Json`.

> **Alternatively via Git URL**:
> Wait for the GitHub release, or use: `https://github.com/MaksMykolenko/UnityMCP.git?path=/UnityPackage`

---

### Step 2 — Start the Unity Bridge

1. In Unity, open **Window → UnityMCP**.
2. Make sure the port is set to `9877` (default).
3. Click **▶ Start Server** (or enable **Auto-Start on Launch**).
4. The status should change to **● Running — localhost:9877**.

---

### Step 3 — Configure the MCP Server

You need to tell your AI Assistant to connect to the Python MCP server. 

#### Option A: Install from GitHub (Recommended)
You can let `uvx` automatically fetch the package from GitHub. Add this to your MCP configuration file:

```json
{
  "mcpServers": {
    "unity": {
      "command": "uvx",
      "args": [
        "--from",
        "git+https://github.com/MaksMykolenko/UnityMCP.git",
        "unity-mcp"
      ]
    }
  }
}
```

#### Option B: Local Installation
If you cloned the repository to your machine (e.g., at `/path/to/UnityMCP`), use `uv run` to start the server:

```json
{
  "mcpServers": {
    "unity": {
      "command": "uv",
      "args": [
        "--directory",
        "/path/to/UnityMCP",
        "run",
        "unity-mcp"
      ]
    }
  }
}
```

#### Where to put this config?
- **Claude Desktop:** 
  - **macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`
  - **Windows**: `%APPDATA%\Claude\claude_desktop_config.json`
- **Cursor / VS Code:** Add via **Settings → MCP → Add Server** or edit `.cursor/mcp.json`.

> 💡 **Tip**: The UnityMCP window inside Unity has a **📋 Copy Cursor/Claude Config** button for local installation.

---

## 🛠 Available Commands (MCP Tools)

| Tool | Description |
|---|---|
| `get_scene_info` | Information about the current scene |
| `get_hierarchy` | Full hierarchy of GameObjects |
| `get_object_info(name)` | Details of a specific GameObject |
| `find_gameobjects(tag, name_contains)` | Search objects by tag/name |
| `get_components(name)` | Output all components on a GameObject |
| `create_gameobject(name, primitive_type, ...)` | Create a new GameObject |
| `delete_gameobject(name)` | Delete a GameObject |
| `set_transform(name, position, rotation, scale)` | Update position/rotation/scale |
| `set_rect_transform(name, ...)` | Modify UI elements (anchors, pivot, size, position) |
| `set_component_property(name, component_type, property, value)` | Modify component properties via Reflection |
| `get_play_mode_state` | Get current Play Mode state (playing, paused, stopped) |
| `set_play_mode_state(action)` | Control Play Mode (play, stop, pause, unpause, step) |
| `set_time_scale(scale)` | Change game speed (e.g., 1.0 for normal, 0.0 for paused) |
| `instantiate_prefab(asset_path, ...)` | Instantiate a prefab from an asset path |
| `apply_prefab_modifications(name)` | Apply overrides back to the source prefab |
| `undo_last_action` | Trigger Unity's Undo system |
| `redo_last_action` | Trigger Unity's Redo system |
| `install_package(package_id)` | Install a Unity package via UPM |
| `build_project(target, output_path)` | Trigger a project build to a specific platform |
| `execute_editor_code(code)` | Execute arbitrary C# code natively (using Roslyn) |
| `get_code_result(job_id)` | Poll the result of an async code execution job |
| `take_screenshot(view)` | Capture the Game view or Scene view |
| `get_console_logs(max_logs, log_types)` | Read Unity console logs |
| `get_asset_list(folder, filter)` | Browse assets in the AssetDatabase |
| `import_asset(asset_path)` | Force re-import of an asset |
| `get_project_settings` | Get Editor & Project settings |

---

## ⚡ Examples of Usage

**Inspect the scene:**
> "Show me all objects in the current Unity scene"

**Create an environment:**
> "Create a directional light at position (0, 10, 0) rotated (50, -30, 0)"

**Batch rename using C# script:**
> "Write C# code to find all GameObjects tagged 'Enemy' and add a Rigidbody component to each."

**Debugging exceptions:**
> "Check the console logs and tell me what errors occurred."

**Visual feedback:**
> "Take a screenshot of the game view so I can see the current state."

---

## 💻 C# Code Execution (Roslyn)

`execute_editor_code` compiles and executes your C# dynamically using Unity's `AssemblyBuilder`.

```csharp
// Example request:
var objects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
print($"Total objects: {objects.Length}");

foreach (var go in objects.Where(g => g.name.Contains("Cube")))
{
    go.transform.localScale = Vector3.one * 2;
    print($"Scaled: {go.name}");
}
```

> **Compilation takes ~1-3 seconds.** The command responds with a `job_id`. Your AI will follow up with `get_code_result(job_id)` a few seconds later to read the output.

---

## 🔌 Custom Port Configuration

By default, the Bridge uses port `9877`. 

**In Unity:** Change it under Window → UnityMCP → Port field.  
**In Python (MCP Server):** Pass an environment variable:

```json
{
  "mcpServers": {
    "unity": {
      "command": "uvx",
      "args": ["--from", "git+https://github.com/MaksMykolenko/UnityMCP.git", "unity-mcp"],
      "env": { "UNITY_PORT": "9877" }
    }
  }
}
```

---

## 📂 Project Structure

```
UnityMCP/
├── pyproject.toml                          ← Python Package Definition
├── main.py                                 ← Local entry point for uv path
├── src/unity_mcp/
│   └── server.py                           ← The MCP server logic (FastMCP)
└── UnityPackage/                           ← The actual Unity Editor package (UPM)
    ├── package.json
    └── Editor/
        ├── UnityMCP.Editor.asmdef
        ├── UnityMCPBridge.cs               ← Main TCP Backend
        ├── UnityMCPWindow.cs               ← Graphical Editor Window UI
        └── EditorThreadDispatcher.cs       ← Thread-safe Unity task dispatcher
```

---

## ⚠️ Known Limitations & Security

- **Editor Only:** UnityMCP is designed solely for the Unity Editor. It is not included in builds.
- **Asynchronous Execution:** Complex actions using `execute_editor_code` depend on Unity's AssemblyBuilder, which runs asynchronously. The server polls using `get_code_result`.
- **Single Connection:** Only handles one active TCP client connection at a time.
- **Security:** Modifying scenes and executing arbitrary code poses security risks. **Only use this with trusted AI models & local environments.**

---

*Inspired by [blender-mcp](https://github.com/ahujasid/blender-mcp) by Siddharth Ahuja.*
