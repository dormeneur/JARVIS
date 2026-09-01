"""Tool registry for the agentic chat loop.

Every tool is declared once here: its Ollama JSON schema, its risk class
(which drives the mobile approval prompt), and its executor.

Safety rules enforced at this layer, not by the model:
  * `/Secrets` is unreachable by any tool — same rule the RAG pipeline
    enforces via document_loader.EXCLUDED_FOLDERS.
  * Every path is resolved and re-checked to be inside the vault, so a
    model emitting `../../etc/passwd` or an absolute path cannot escape.
  * Mutating tools never run without an explicit approval from the client.
"""

from __future__ import annotations

import inspect
import logging
import shutil
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from app.config import settings
from app.services.document_loader import EXCLUDED_FOLDERS

logger = logging.getLogger(__name__)

# Risk classes. The mobile client maps these to its permission UI.
RISK_READ = "read"      # auto-approved, never mutates
RISK_WRITE = "write"    # creates/modifies — approval required
RISK_DELETE = "delete"  # destructive — approval required, session-grant refused

# Caps so a confused model cannot flood the vault or the prompt.
MAX_READ_CHARS = 20_000
MAX_LIST_ENTRIES = 200
MAX_WRITE_BYTES = 256 * 1024

# Mirrors sanitizer.ALLOWED_EXTENSIONS — the set the agent may author.
WRITABLE_EXTENSIONS = {
    ".txt", ".md", ".js", ".ts", ".py", ".json", ".yaml", ".yml",
    ".html", ".css", ".sh", ".csv", ".jsx", ".tsx",
}


class ToolError(Exception):
    """A tool failed in a way the model should see and can recover from."""


# ---------------------------------------------------------------------------
# Path safety
# ---------------------------------------------------------------------------

def _vault_root() -> Path:
    return Path(settings.vault_path).resolve()


def safe_path(raw: str, *, must_exist: bool = False) -> Path:
    """Resolve a model-supplied path to a real path inside the vault.

    Raises ToolError (never returns an unsafe path) if the path escapes the
    vault, targets an excluded folder, or is missing when required.
    """
    if not raw or not str(raw).strip():
        raise ToolError("path must not be empty")

    cleaned = str(raw).replace("\\", "/").replace("\x00", "").strip()
    # Strip a leading slash / drive letter so absolute-looking paths are
    # treated as vault-relative rather than rejected outright.
    if len(cleaned) >= 2 and cleaned[1] == ":" and cleaned[0].isalpha():
        cleaned = cleaned[2:]
    cleaned = cleaned.lstrip("/")

    root = _vault_root()
    resolved = (root / cleaned).resolve()

    # The real containment check — survives .., symlinks, and casing games.
    if resolved != root and root not in resolved.parents:
        raise ToolError(f"path escapes the vault: {raw}")

    rel_parts = resolved.relative_to(root).parts
    if rel_parts and rel_parts[0] in EXCLUDED_FOLDERS:
        raise ToolError(
            f"'{rel_parts[0]}' is off-limits to the assistant and cannot be read or modified"
        )

    if must_exist and not resolved.exists():
        raise ToolError(f"no such file or directory: {cleaned}")

    return resolved


def _rel(p: Path) -> str:
    return str(p.relative_to(_vault_root())).replace("\\", "/")


def _check_writable_ext(p: Path) -> None:
    if p.suffix.lower() not in WRITABLE_EXTENSIONS:
        raise ToolError(
            f"cannot write '{p.suffix or 'no extension'}' files; allowed: "
            + ", ".join(sorted(WRITABLE_EXTENSIONS))
        )


# ---------------------------------------------------------------------------
# Executors
# ---------------------------------------------------------------------------

def _tool_list_directory(path: str = ".", **_: Any) -> str:
    target = safe_path(path or ".", must_exist=True)
    if not target.is_dir():
        raise ToolError(f"not a directory: {_rel(target)}")

    entries: List[str] = []
    for child in sorted(target.iterdir(), key=lambda c: (not c.is_dir(), c.name.lower())):
        if child.name.startswith(".") or child.name in EXCLUDED_FOLDERS:
            continue
        entries.append(f"{child.name}/" if child.is_dir() else child.name)
        if len(entries) >= MAX_LIST_ENTRIES:
            entries.append(f"... (truncated at {MAX_LIST_ENTRIES})")
            break

    label = _rel(target) or "(vault root)"
    if not entries:
        return f"{label} is empty."
    return f"{label} contains:\n" + "\n".join(entries)


def _tool_read_file(path: str, **_: Any) -> str:
    target = safe_path(path, must_exist=True)
    if target.is_dir():
        raise ToolError(f"'{_rel(target)}' is a directory; use list_directory")

    try:
        text = target.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        raise ToolError(f"could not read {_rel(target)}: {exc}") from exc

    if len(text) > MAX_READ_CHARS:
        text = text[:MAX_READ_CHARS] + f"\n[truncated at {MAX_READ_CHARS} chars]"
    return f"Contents of {_rel(target)}:\n{text}"


async def _tool_search_vault(query: str, top_k: int = 5, *, _services: Any = None, **_: Any) -> str:
    """Semantic search over the indexed vault. Wired to the live retriever."""
    if _services is None:
        raise ToolError("search is unavailable right now")
    results = await _services.search(query, top_k=min(int(top_k or 5), 10))
    if not results:
        return f"No vault content matched '{query}'."
    lines = [f"Search results for '{query}':"]
    for r in results:
        snippet = (r.get("content") or "").strip().replace("\n", " ")
        if len(snippet) > 400:
            snippet = snippet[:400] + "…"
        lines.append(f"- [{r['path']}] {snippet}")
    return "\n".join(lines)


def _tool_create_file(path: str, content: str = "", **_: Any) -> str:
    target = safe_path(path)
    _check_writable_ext(target)
    if target.exists():
        raise ToolError(f"{_rel(target)} already exists; use edit_file to change it")

    body = content or ""
    if len(body.encode("utf-8")) > MAX_WRITE_BYTES:
        raise ToolError(f"content exceeds {MAX_WRITE_BYTES} bytes")

    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(body, encoding="utf-8")
    return f"Created {_rel(target)} ({len(body)} chars)."


def _tool_edit_file(path: str, content: str, **_: Any) -> str:
    target = safe_path(path, must_exist=True)
    _check_writable_ext(target)
    if target.is_dir():
        raise ToolError(f"'{_rel(target)}' is a directory")

    body = content or ""
    if len(body.encode("utf-8")) > MAX_WRITE_BYTES:
        raise ToolError(f"content exceeds {MAX_WRITE_BYTES} bytes")

    target.write_text(body, encoding="utf-8")
    return f"Updated {_rel(target)} ({len(body)} chars)."


def _tool_move_file(source: str, destination: str, **_: Any) -> str:
    src = safe_path(source, must_exist=True)
    dst = safe_path(destination)

    # "move X to Folder" — treat an existing directory as the parent.
    if dst.exists() and dst.is_dir():
        dst = dst / src.name
    if dst.exists():
        raise ToolError(f"{_rel(dst)} already exists")
    if src.is_file():
        _check_writable_ext(dst)

    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.move(str(src), str(dst))
    return f"Moved {_rel(src)} to {_rel(dst)}."


def _tool_delete_file(path: str, **_: Any) -> str:
    target = safe_path(path, must_exist=True)
    rel = _rel(target)
    if not rel:
        raise ToolError("refusing to delete the vault root")

    if target.is_dir():
        shutil.rmtree(target)
        return f"Deleted folder {rel} and its contents."
    target.unlink()
    return f"Deleted {rel}."


# ---------------------------------------------------------------------------
# Registry
# ---------------------------------------------------------------------------

class Tool:
    def __init__(
        self,
        name: str,
        description: str,
        parameters: Dict[str, Any],
        risk: str,
        fn: Callable[..., str],
        needs_services: bool = False,
    ):
        self.name = name
        self.description = description
        self.parameters = parameters
        self.risk = risk
        self.fn = fn
        self.needs_services = needs_services

    @property
    def requires_approval(self) -> bool:
        return self.risk != RISK_READ

    def schema(self) -> Dict[str, Any]:
        return {
            "type": "function",
            "function": {
                "name": self.name,
                "description": self.description,
                "parameters": self.parameters,
            },
        }


def _obj(props: Dict[str, Any], required: List[str]) -> Dict[str, Any]:
    return {"type": "object", "properties": props, "required": required}


_STR = {"type": "string"}


TOOLS: Dict[str, Tool] = {
    t.name: t
    for t in [
        Tool(
            "search_vault",
            "Search the user's knowledge vault by meaning to find relevant notes and "
            "documents. Use this whenever you need facts you were not given.",
            _obj(
                {
                    "query": {**_STR, "description": "What to look for"},
                    "top_k": {"type": "integer", "description": "How many results (1-10)"},
                },
                ["query"],
            ),
            RISK_READ,
            _tool_search_vault,
            needs_services=True,
        ),
        Tool(
            "read_file",
            "Read the full text of one file in the vault. Use before editing a file so "
            "you never overwrite content you have not seen.",
            _obj({"path": {**_STR, "description": "Vault-relative path"}}, ["path"]),
            RISK_READ,
            _tool_read_file,
        ),
        Tool(
            "list_directory",
            "List the files and folders in a vault directory. Use '.' for the vault root.",
            _obj({"path": {**_STR, "description": "Vault-relative folder, '.' for root"}}, []),
            RISK_READ,
            _tool_list_directory,
        ),
        Tool(
            "create_file",
            "Create a NEW file with the given content. Fails if the file already exists.",
            _obj(
                {
                    "path": {**_STR, "description": "Vault-relative path including filename and extension"},
                    "content": {**_STR, "description": "Full text content of the file"},
                },
                ["path"],
            ),
            RISK_WRITE,
            _tool_create_file,
        ),
        Tool(
            "edit_file",
            "Replace the entire contents of an existing file. Read the file first so you "
            "preserve anything that should stay.",
            _obj(
                {
                    "path": {**_STR, "description": "Vault-relative path to an existing file"},
                    "content": {**_STR, "description": "The complete new content"},
                },
                ["path", "content"],
            ),
            RISK_WRITE,
            _tool_edit_file,
        ),
        Tool(
            "move_file",
            "Move or rename a file or folder within the vault.",
            _obj(
                {
                    "source": {**_STR, "description": "Existing vault-relative path"},
                    "destination": {**_STR, "description": "New path, or an existing folder to move into"},
                },
                ["source", "destination"],
            ),
            RISK_WRITE,
            _tool_move_file,
        ),
        Tool(
            "delete_file",
            "Permanently delete a file, or a folder and everything inside it. "
            "Only use when the user clearly asked for a deletion.",
            _obj({"path": {**_STR, "description": "Vault-relative path to delete"}}, ["path"]),
            RISK_DELETE,
            _tool_delete_file,
        ),
    ]
}


def tool_schemas() -> List[Dict[str, Any]]:
    return [t.schema() for t in TOOLS.values()]


def risk_of(name: str) -> str:
    tool = TOOLS.get(name)
    return tool.risk if tool else RISK_WRITE  # unknown tool → treat as risky


async def execute(name: str, arguments: Dict[str, Any], services: Any = None) -> str:
    """Run a tool. Raises ToolError with a model-readable message on failure."""
    tool = TOOLS.get(name)
    if tool is None:
        raise ToolError(f"unknown tool '{name}'")

    args = dict(arguments or {})
    if tool.needs_services:
        args["_services"] = services

    try:
        result = tool.fn(**args)
        if inspect.isawaitable(result):
            result = await result
        return result
    except ToolError:
        raise
    except TypeError as exc:
        raise ToolError(f"bad arguments for {name}: {exc}") from exc
    except Exception as exc:  # noqa: BLE001 - surface any failure to the model
        logger.exception("Tool %s failed", name)
        raise ToolError(f"{name} failed: {exc}") from exc
