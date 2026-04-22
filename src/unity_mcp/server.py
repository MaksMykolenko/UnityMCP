# unity_mcp_server.py
from mcp.server.fastmcp import FastMCP, Context, Image
import socket
import json
import asyncio
import logging
import tempfile
import threading
import time
from dataclasses import dataclass, field
from contextlib import asynccontextmanager
from typing import AsyncIterator, Dict, Any, Optional
import os
import base64

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger("UnityMCPServer")

# Default configuration
DEFAULT_HOST = "localhost"
DEFAULT_PORT = 9877
RESPONSE_TIMEOUT = 180.0


# ──────────────────────────────────────────────────────────────────────────────
#  Unity connection
# ──────────────────────────────────────────────────────────────────────────────

@dataclass
class UnityConnection:
    host: str
    port: int
    sock: socket.socket = field(default=None, repr=False)

    def connect(self) -> bool:
        """Connect to the Unity Editor socket bridge."""
        if self.sock:
            return True
        try:
            self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self.sock.connect((self.host, self.port))
            logger.info(f"Connected to Unity Editor at {self.host}:{self.port}")
            return True
        except Exception as e:
            logger.error(f"Failed to connect to Unity: {e}")
            self.sock = None
            return False

    def disconnect(self):
        """Disconnect from the Unity Editor bridge."""
        if self.sock:
            try:
                self.sock.close()
            except Exception as e:
                logger.error(f"Error disconnecting: {e}")
            finally:
                self.sock = None

    def _receive_full_response(self) -> bytes:
        """Receive a complete JSON response (potentially in multiple chunks)."""
        chunks = []
        self.sock.settimeout(RESPONSE_TIMEOUT)
        try:
            while True:
                try:
                    chunk = self.sock.recv(65536)
                    if not chunk:
                        if not chunks:
                            raise ConnectionError("Connection closed before any data received")
                        break
                    chunks.append(chunk)
                    data = b"".join(chunks)
                    try:
                        json.loads(data.decode("utf-8"))
                        logger.debug(f"Received complete response ({len(data)} bytes)")
                        return data
                    except json.JSONDecodeError:
                        continue
                except socket.timeout:
                    logger.warning("Socket timeout during receive")
                    break
                except (ConnectionError, BrokenPipeError, ConnectionResetError) as e:
                    logger.error(f"Connection error during receive: {e}")
                    raise
        except socket.timeout:
            logger.warning("Outer socket timeout")
        except Exception:
            raise

        if chunks:
            data = b"".join(chunks)
            try:
                json.loads(data.decode("utf-8"))
                return data
            except json.JSONDecodeError:
                raise Exception("Incomplete JSON response")
        raise Exception("No data received")

    def send_command(self, command_type: str, params: Dict[str, Any] = None) -> Dict[str, Any]:
        """Send a command to Unity Editor and return the parsed response."""
        if not self.sock and not self.connect():
            raise ConnectionError("Not connected to Unity Editor")

        command = {"type": command_type, "params": params or {}}

        try:
            logger.info(f"→ Unity: {command_type}  params={params}")
            self.sock.sendall(json.dumps(command).encode("utf-8"))
            self.sock.settimeout(RESPONSE_TIMEOUT)

            raw = self._receive_full_response()
            response = json.loads(raw.decode("utf-8"))
            logger.info(f"← Unity: status={response.get('status', '?')}")

            if response.get("status") == "error":
                raise Exception(response.get("message", "Unknown error from Unity"))

            return response.get("result", {})

        except socket.timeout:
            self.sock = None
            raise Exception("Timeout waiting for Unity response — try simplifying your request")
        except (ConnectionError, BrokenPipeError, ConnectionResetError) as e:
            self.sock = None
            raise Exception(f"Connection to Unity lost: {e}")
        except json.JSONDecodeError as e:
            raise Exception(f"Invalid JSON from Unity: {e}")
        except Exception as e:
            self.sock = None
            raise Exception(f"Communication error with Unity: {e}")


# ──────────────────────────────────────────────────────────────────────────────
#  FastMCP server setup
# ──────────────────────────────────────────────────────────────────────────────

@asynccontextmanager
async def server_lifespan(server: FastMCP) -> AsyncIterator[Dict[str, Any]]:
    """Manage server startup and shutdown lifecycle."""
    global _unity_connection
    logger.info("UnityMCP server starting up")
    try:
        try:
            conn = get_unity_connection()
            logger.info("Successfully connected to Unity on startup")
        except Exception as e:
            logger.warning(f"Could not connect to Unity on startup: {e}")
            logger.warning("Make sure UnityMCPBridge is running in the Unity Editor")
        yield {}
    finally:
        if _unity_connection:
            logger.info("Disconnecting from Unity on shutdown")
            _unity_connection.disconnect()
            _unity_connection = None
        logger.info("UnityMCP server shut down")


mcp = FastMCP("UnityMCP", lifespan=server_lifespan)

_unity_connection: Optional[UnityConnection] = None
_connection_lock = threading.Lock()


def get_unity_connection() -> UnityConnection:
    """Get or create a persistent, thread-safe Unity Editor connection."""
    global _unity_connection

    with _connection_lock:
        if _unity_connection is not None:
            try:
                _unity_connection.send_command("ping")
                return _unity_connection
            except Exception as e:
                logger.warning(f"Existing connection invalid: {e}")
                try:
                    _unity_connection.disconnect()
                except Exception:
                    pass
                _unity_connection = None

        host = os.getenv("UNITY_HOST", DEFAULT_HOST)
        port = int(os.getenv("UNITY_PORT", DEFAULT_PORT))
        _unity_connection = UnityConnection(host=host, port=port)

        if not _unity_connection.connect():
            _unity_connection = None
            raise Exception(
                "Could not connect to Unity Editor. "
                "Make sure UnityMCPBridge is running (Window → UnityMCP → Start Server)."
            )

        logger.info("New persistent connection to Unity established")
        return _unity_connection


# ──────────────────────────────────────────────────────────────────────────────
#  MCP Tools — Scene inspection
# ──────────────────────────────────────────────────────────────────────────────

@mcp.tool()
def get_scene_info(ctx: Context) -> str:
    """
    Get information about the currently open Unity scene:
    name, path, object count, root GameObjects list, active camera, lighting info.
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("get_scene_info")
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"get_scene_info failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def get_object_info(ctx: Context, object_name: str) -> str:
    """
    Get detailed information about a specific GameObject in the Unity scene.

    Parameters:
    - object_name: Exact name of the GameObject (or full hierarchy path, e.g. 'Canvas/Button')
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("get_object_info", {"name": object_name})
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"get_object_info failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def get_hierarchy(ctx: Context) -> str:
    """
    Get the complete GameObject hierarchy of the active Unity scene as a tree.
    Returns every GameObject with its parent path, active state, and direct children.
    Useful for understanding scene structure before making changes.
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("get_hierarchy")
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"get_hierarchy failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def find_gameobjects(ctx: Context, tag: str = None, name_contains: str = None) -> str:
    """
    Find GameObjects in the scene matching optional filters.

    Parameters:
    - tag: Filter by Unity tag (e.g. 'Player', 'MainCamera', 'Untagged')
    - name_contains: Filter by partial name match (case-insensitive)

    Returns a list of matching GameObjects with basic info.
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("find_gameobjects", {
            "tag": tag,
            "name_contains": name_contains
        })
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"find_gameobjects failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def get_components(ctx: Context, object_name: str) -> str:
    """
    List all Components attached to a GameObject, including their serialized properties.

    Parameters:
    - object_name: Exact name of the target GameObject
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("get_components", {"name": object_name})
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"get_components failed: {e}")
        return f"Error: {e}"


# ──────────────────────────────────────────────────────────────────────────────
#  MCP Tools — Scene manipulation
# ──────────────────────────────────────────────────────────────────────────────

@mcp.tool()
def create_gameobject(
    ctx: Context,
    name: str,
    primitive_type: str = "Empty",
    position: list = None,
    rotation: list = None,
    scale: list = None,
    parent_name: str = None
) -> str:
    """
    Create a new GameObject in the active Unity scene.

    Parameters:
    - name: Name for the new GameObject
    - primitive_type: One of 'Empty', 'Cube', 'Sphere', 'Capsule', 'Cylinder',
                      'Plane', 'Quad', 'Camera', 'Light', 'AudioSource'
    - position: [x, y, z] in world space (default [0, 0, 0])
    - rotation: [x, y, z] Euler angles in degrees (default [0, 0, 0])
    - scale: [x, y, z] (default [1, 1, 1])
    - parent_name: Name of existing GameObject to parent to (optional)
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("create_gameobject", {
            "name": name,
            "primitive_type": primitive_type,
            "position": position or [0, 0, 0],
            "rotation": rotation or [0, 0, 0],
            "scale": scale or [1, 1, 1],
            "parent_name": parent_name
        })
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"create_gameobject failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def delete_gameobject(ctx: Context, object_name: str) -> str:
    """
    Delete (destroy) a GameObject from the active Unity scene.

    Parameters:
    - object_name: Exact name of the GameObject to delete
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("delete_gameobject", {"name": object_name})
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"delete_gameobject failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def set_transform(
    ctx: Context,
    object_name: str,
    position: list = None,
    rotation: list = None,
    scale: list = None
) -> str:
    """
    Set the Transform (position, rotation, scale) of an existing GameObject.
    Only the provided fields are updated; null/missing fields are left unchanged.

    Parameters:
    - object_name: Exact name of the target GameObject
    - position: [x, y, z] world-space position
    - rotation: [x, y, z] Euler angles in degrees
    - scale: [x, y, z] local scale
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("set_transform", {
            "name": object_name,
            "position": position,
            "rotation": rotation,
            "scale": scale
        })
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"set_transform failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def set_component_property(
    ctx: Context,
    object_name: str,
    component_type: str,
    property_name: str,
    value: str
) -> str:
    """
    Set a property on a component attached to a GameObject.
    The value is passed as a JSON string and Unity will parse it to the correct type.

    Parameters:
    - object_name: Name of the target GameObject
    - component_type: C# type name of the component (e.g. 'MeshRenderer', 'Light', 'Rigidbody')
    - property_name: Name of the public field or property on the component
    - value: JSON-encoded value to set (e.g. '"red"', '1.5', 'true', '[1,0,0]')

    Examples:
    - Set MeshRenderer material color: component_type='MeshRenderer', property_name='material.color',
      value='{"r":1,"g":0,"b":0,"a":1}'
    - Set Light intensity: component_type='Light', property_name='intensity', value='2.5'
    - Toggle Rigidbody gravity: component_type='Rigidbody', property_name='useGravity', value='false'
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("set_component_property", {
            "name": object_name,
            "component_type": component_type,
            "property_name": property_name,
            "value": value
        })
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"set_component_property failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def set_rect_transform(
    ctx: Context,
    name: str,
    anchored_position: dict = None,
    size_delta: dict = None,
    anchor_min: dict = None,
    anchor_max: dict = None,
    pivot: dict = None
) -> str:
    """
    Updates the RectTransform of a UI element in the Unity scene.

    Parameters:
    - name: Exact name of the target UI GameObject
    - anchored_position: Mapping of {"x": float, "y": float}
    - size_delta: Mapping of {"x": float, "y": float}
    - anchor_min: Mapping of {"x": float, "y": float}
    - anchor_max: Mapping of {"x": float, "y": float}
    - pivot: Mapping of {"x": float, "y": float}
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("set_rect_transform", {
            "name": name,
            "anchored_position": anchored_position,
            "size_delta": size_delta,
            "anchor_min": anchor_min,
            "anchor_max": anchor_max,
            "pivot": pivot
        })
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"set_rect_transform failed: {e}")
        return f"Error: {e}"


# ──────────────────────────────────────────────────────────────────────────────
#  MCP Tools — Code execution (Variant B — Roslyn via AssemblyBuilder)
# ──────────────────────────────────────────────────────────────────────────────

@mcp.tool()
def execute_editor_code(ctx: Context, code: str) -> str:
    """
    Execute arbitrary C# code inside Unity Editor using Roslyn compilation.
    The code runs in the context of UnityEditor — all Unity APIs are available.

    A helper 'print(object)' function is provided that appends to the returned output.
    Use UnityEngine.Debug.Log() for persistent console output (shows in Unity console).

    HOW IT WORKS:
    Your code is wrapped in a static Execute() method so it runs synchronously
    on Unity's main thread. The compiled assembly is loaded, executed, and unloaded.
    This is a job-based system: this call starts the job and may return a job_id if
    compilation takes longer than a moment. Use get_code_result() to fetch the result.

    Parameters:
    - code: Valid C# code to execute. Available namespaces:
            UnityEngine, UnityEditor, UnityEditor.SceneManagement,
            System, System.Linq, System.Collections.Generic

    Example:
      var objs = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
      print($"Found {objs.Length} GameObjects");
      foreach (var go in objs) print(go.name);
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("execute_code", {"code": code})

        # If Unity is still compiling, result contains a job_id to poll
        if isinstance(result, dict) and result.get("status") == "compiling":
            return json.dumps({
                "status": "compiling",
                "job_id": result.get("job_id"),
                "message": "Compilation started. Call get_code_result(job_id) to fetch output."
            }, indent=2)

        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"execute_editor_code failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def get_code_result(ctx: Context, job_id: str) -> str:
    """
    Poll the result of a pending C# code execution job.
    Call this after execute_editor_code() returns a job_id.

    Parameters:
    - job_id: The job identifier returned by execute_editor_code()

    Returns:
    - status 'done' with output, or status 'compiling' if still in progress,
      or status 'error' with compilation/runtime error details.
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("get_code_result", {"job_id": job_id})
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"get_code_result failed: {e}")
        return f"Error: {e}"


# ──────────────────────────────────────────────────────────────────────────────
#  MCP Tools — Screenshots and console
# ──────────────────────────────────────────────────────────────────────────────

@mcp.tool()
def take_screenshot(ctx: Context, view: str = "game") -> Image:
    """
    Capture a screenshot from Unity Editor.

    Parameters:
    - view: Which view to capture — 'game' (Game View) or 'scene' (Scene View)

    Returns the screenshot as an image for visual inspection of the scene.
    """
    try:
        unity = get_unity_connection()
        temp_path = os.path.join(tempfile.gettempdir(), f"unity_screenshot_{os.getpid()}.png")

        result = unity.send_command("take_screenshot", {
            "filepath": temp_path,
            "view": view
        })

        if "error" in result:
            raise Exception(result["error"])

        # Unity may return base64 directly or a file path
        if "image_base64" in result:
            image_bytes = base64.b64decode(result["image_base64"])
        elif os.path.exists(temp_path):
            with open(temp_path, "rb") as f:
                image_bytes = f.read()
            os.remove(temp_path)
        else:
            raise Exception("Screenshot file not created")

        return Image(data=image_bytes, format="png")

    except Exception as e:
        logger.error(f"take_screenshot failed: {e}")
        raise Exception(f"Screenshot failed: {e}")


@mcp.tool()
def get_console_logs(ctx: Context, max_logs: int = 50, log_types: str = "all") -> str:
    """
    Retrieve recent log messages from the Unity Editor console.

    Parameters:
    - max_logs: Maximum number of log entries to return (default 50)
    - log_types: Which types to include — 'all', 'error', 'warning', 'log'

    Useful for debugging — check what Unity is outputting after code execution.
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("get_console_logs", {
            "max_logs": max_logs,
            "log_types": log_types
        })
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"get_console_logs failed: {e}")
        return f"Error: {e}"


# ──────────────────────────────────────────────────────────────────────────────
#  MCP Tools — Play Mode Control
# ──────────────────────────────────────────────────────────────────────────────

@mcp.tool()
def get_play_mode_state(ctx: Context) -> str:
    """
    Requests the current state of Play Mode in Unity.
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("get_play_mode_state")
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"get_play_mode_state failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def set_play_mode_state(ctx: Context, action: str) -> str:
    """
    Control the Play Mode state in Unity.

    Parameters:
    - action: One of "play", "stop", "pause", "unpause", or "step".
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("set_play_mode_state", {"action": action})
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"set_play_mode_state failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def set_time_scale(ctx: Context, scale: float) -> str:
    """
    Change the speed of the game in Unity.

    Parameters:
    - scale: Float to change the game's speed (e.g., 1.0 for normal, 0.0 for paused).
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("set_time_scale", {"scale": scale})
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"set_time_scale failed: {e}")
        return f"Error: {e}"


# ──────────────────────────────────────────────────────────────────────────────
#  MCP Tools — Prefabs and Undo/Redo
# ──────────────────────────────────────────────────────────────────────────────

@mcp.tool()
def instantiate_prefab(ctx: Context, asset_path: str, position: list = None, rotation: list = None) -> str:
    """
    Instantiate a prefab from the given project asset path.

    Parameters:
    - asset_path: Project-relative path to the prefab asset (e.g., 'Assets/Prefabs/Enemy.prefab')
    - position: [x, y, z] in world space (default [0, 0, 0])
    - rotation: [x, y, z] Euler angles in degrees (default [0, 0, 0])
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("instantiate_prefab", {
            "asset_path": asset_path,
            "position": position or [0, 0, 0],
            "rotation": rotation or [0, 0, 0]
        })
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"instantiate_prefab failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def apply_prefab_modifications(ctx: Context, gameobject_name: str) -> str:
    """
    Apply overrides from a prefab instance in the scene back to its source prefab asset.

    Parameters:
    - gameobject_name: Exact name of the prefab instance GameObject in the scene
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("apply_prefab_modifications", {"gameobject_name": gameobject_name})
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"apply_prefab_modifications failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def undo_last_action(ctx: Context) -> str:
    """
    Triggers Unity's Undo system to revert the last recorded action.
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("undo_last_action")
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"undo_last_action failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def redo_last_action(ctx: Context) -> str:
    """
    Triggers Unity's Redo system to re-apply the last undone action.
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("redo_last_action")
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"redo_last_action failed: {e}")
        return f"Error: {e}"


# ──────────────────────────────────────────────────────────────────────────────
#  MCP Tools — Package Manager & Build Pipeline
# ──────────────────────────────────────────────────────────────────────────────

@mcp.tool()
def install_package(ctx: Context, package_id: str) -> str:
    """
    Installs a Unity package via the Unity Package Manager (UPM).

    Parameters:
    - package_id: The identifier of the package (e.g., 'com.unity.cinemachine', 'com.unity.postprocessing')
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("install_package", {"package_id": package_id})
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"install_package failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def build_project(ctx: Context, target_platform: str, output_path: str) -> str:
    """
    Triggers a project build using the active scenes in Editor Build Settings.

    Parameters:
    - target_platform: One of 'Windows', 'macOS', 'Android', 'iOS', 'WebGL'.
    - output_path: Absolute or project-relative path where the build should be saved.
                   For Windows/macOS, this should include the executable name (e.g. 'Builds/Game.exe').
                   For Android/iOS/WebGL, it should be a directory path or appropriate extension.
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("build_project", {
            "target_platform": target_platform,
            "output_path": output_path
        })
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"build_project failed: {e}")
        return f"Error: {e}"


# ──────────────────────────────────────────────────────────────────────────────
#  MCP Tools — Asset management
# ──────────────────────────────────────────────────────────────────────────────

@mcp.tool()
def get_asset_list(ctx: Context, folder: str = "Assets", filter: str = "") -> str:
    """
    List assets in the Unity project's Asset Database.

    Parameters:
    - folder: Root folder to search under (default 'Assets')
    - filter: AssetDatabase search filter string
              Examples: 't:Texture2D', 't:Material', 't:Prefab myname', 'l:MyLabel'

    Returns asset paths relative to the project root.
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("get_asset_list", {
            "folder": folder,
            "filter": filter
        })
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"get_asset_list failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def import_asset(ctx: Context, asset_path: str) -> str:
    """
    Force re-import an asset in Unity's Asset Database.
    Useful after modifying asset files on disk externally.

    Parameters:
    - asset_path: Project-relative path to the asset (e.g. 'Assets/Models/Car.fbx')
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("import_asset", {"asset_path": asset_path})
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"import_asset failed: {e}")
        return f"Error: {e}"


@mcp.tool()
def get_project_settings(ctx: Context) -> str:
    """
    Retrieve Unity project settings including:
    build target, render pipeline, quality settings, physics settings,
    company/product name, version, and scripting backend.
    """
    try:
        unity = get_unity_connection()
        result = unity.send_command("get_project_settings")
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"get_project_settings failed: {e}")
        return f"Error: {e}"


# ──────────────────────────────────────────────────────────────────────────────
#  MCP Prompt — Strategy for working with Unity
# ──────────────────────────────────────────────────────────────────────────────

@mcp.prompt()
def unity_workflow_strategy() -> str:
    """Defines the preferred strategy for working with Unity through MCP"""
    return """When working with a Unity project through UnityMCP, follow these guidelines:

    1. ALWAYS START by calling get_scene_info() to understand the current state of the scene.

    2. INSPECTION BEFORE MODIFICATION:
       - Use get_hierarchy() to see all GameObjects and their parent-child relationships.
       - Use get_object_info(name) to inspect a specific object before modifying it.
       - Use get_components(name) to see what components exist on an object.

    3. MAKING CHANGES:
       - Prefer the specific manipulation tools (create_gameobject, delete_gameobject,
         set_transform, set_component_property) for straightforward changes.
       - Use execute_editor_code() for complex logic, batch operations, or anything
         that requires conditional logic that the specific tools can't express.

    4. EXECUTING C# CODE:
       - Break complex operations into small, focused code blocks.
       - Always check get_console_logs() after code execution to see if there were errors.
       - The 'print(value)' helper in the execution context returns output via the tool result.
       - For code that takes >1 second to compile, use get_code_result(job_id) to poll.
       - Available in code: UnityEngine.*, UnityEditor.*, System.Linq, etc.

    5. VERIFYING RESULTS:
       - After making scene changes, call get_scene_info() or get_object_info() to verify.
       - Use take_screenshot() to visually confirm the result in the Game or Scene view.

    6. UNITY 6 SPECIFICS:
       - Use Object.FindObjectsByType<T>(FindObjectsSortMode.None) instead of the deprecated FindObjectsOfType.
       - Prefer UnityEditor.SceneManagement.EditorSceneManager for scene operations.
       - Mark objects dirty after changes: EditorUtility.SetDirty(obj).

    7. COMMON PATTERNS:
       - Create a lit scene: create directional light + camera, set skybox via execute_editor_code.
       - Add physics: use set_component_property or execute_editor_code to modify Rigidbody settings.
       - Batch rename: use execute_editor_code with FindObjectsByType + loop.
    """


# ──────────────────────────────────────────────────────────────────────────────
#  Entry point
# ──────────────────────────────────────────────────────────────────────────────

def main():
    """Run the UnityMCP MCP server."""
    mcp.run()


if __name__ == "__main__":
    main()
