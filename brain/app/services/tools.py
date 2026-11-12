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
from datetime import datetime, timedelta
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from app.config import settings
from app.services.document_loader import EXCLUDED_FOLDERS, DocumentLoader
from app.services.vault_text import wrap_vault_text

logger = logging.getLogger(__name__)

# Risk classes. The mobile client maps these to its permission UI.
RISK_READ = "read"      # auto-approved, never mutates
RISK_WRITE = "write"    # creates/modifies — approval required
RISK_DELETE = "delete"  # destructive — approval required, session-grant refused

# Caps so a confused model cannot flood the vault or the prompt.
# ~4 chars per token: a tool result may use about a quarter of the context window.
MAX_TOOL_OUTPUT_CHARS = settings.num_ctx
MAX_LIST_ENTRIES = 200
MAX_WRITE_BYTES = 256 * 1024
TRUNCATION_GUARD_MIN = 2000   # files at least this long can't be rewritten much shorter
TRASH_KEEP_DAYS = 30

# Mirrors sanitizer.ALLOWED_EXTENSIONS — the set the agent may author.
WRITABLE_EXTENSIONS = {
    ".txt", ".md", ".js", ".ts", ".py", ".json", ".yaml", ".yml",
    ".html", ".css", ".sh", ".csv", ".jsx", ".tsx",
}


_EXCLUDED_CASEFOLDED = frozenset(f.casefold() for f in EXCLUDED_FOLDERS)


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
    # casefold: the vault is a Windows bind mount, so "secrets/x" opens Secrets/x.
    if rel_parts and rel_parts[0].casefold() in _EXCLUDED_CASEFOLDED:
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
        if target.suffix.lower() in (".pdf", ".docx"):
            # Binary formats: reuse the indexer's extractor instead of decoding raw bytes.
            text = DocumentLoader(str(settings.vault_path))._extract_content(target)
            if text is None:
                raise ToolError(f"could not extract text from {_rel(target)}")
        else:
            text = target.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        raise ToolError(f"could not read {_rel(target)}: {exc}") from exc

    if len(text) > MAX_TOOL_OUTPUT_CHARS:
        text = text[:MAX_TOOL_OUTPUT_CHARS] + f"\n[truncated at {MAX_TOOL_OUTPUT_CHARS} chars]"
    return f"Contents of {_rel(target)}:\n{wrap_vault_text(_rel(target), text)}"


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
        lines.append(f"- [{r['path']}] {wrap_vault_text(r['path'], snippet)}")
    return "\n".join(lines)[:MAX_TOOL_OUTPUT_CHARS]


def _write(target: Path, body: str) -> None:
    """Write UTF-8 with plain \n endings on every OS (write_text would turn them into \r\n on Windows)."""
    target.write_bytes(body.encode("utf-8"))


def _trash_root() -> Path:
    # Under .system, so it is never indexed or synced; safe_path refuses .system, so the model
    # can never reach it either. Daily backups do include it.
    return _vault_root() / ".system" / "brain" / "trash"


def _to_trash(target: Path, *, move: bool) -> None:
    """Keep the old version of a file (copy) or the whole item (move) for TRASH_KEEP_DAYS."""
    dest = _trash_root() / datetime.now().strftime("%Y%m%d-%H%M%S-%f") / _rel(target)
    dest.parent.mkdir(parents=True, exist_ok=True)
    if move:
        shutil.move(str(target), str(dest))
    else:
        shutil.copy2(target, dest)


def prune_trash(days: int = TRASH_KEEP_DAYS) -> int:
    """Remove trash stamps older than `days`. Returns how many were removed. Never raises."""
    removed = 0
    root = _trash_root()
    if not root.is_dir():
        return 0
    cutoff = datetime.now() - timedelta(days=days)
    for stamp in root.iterdir():
        try:
            when = datetime.strptime(stamp.name[:15], "%Y%m%d-%H%M%S")
        except ValueError:
            continue
        if when < cutoff:
            shutil.rmtree(stamp, ignore_errors=True)
            removed += 1
    return removed


def _tool_create_file(path: str, content: str = "", **_: Any) -> str:
    target = safe_path(path)
    _check_writable_ext(target)
    if target.exists():
        raise ToolError(f"{_rel(target)} already exists; use edit_file to change it")

    body = content or ""
    if len(body.encode("utf-8")) > MAX_WRITE_BYTES:
        raise ToolError(f"content exceeds {MAX_WRITE_BYTES} bytes")

    target.parent.mkdir(parents=True, exist_ok=True)
    _write(target, body)
    return f"Created {_rel(target)} ({len(body)} chars)."


def _tool_edit_file(path: str, content: str, **_: Any) -> str:
    target = safe_path(path, must_exist=True)
    _check_writable_ext(target)
    if target.is_dir():
        raise ToolError(f"'{_rel(target)}' is a directory")

    body = content or ""
    if len(body.encode("utf-8")) > MAX_WRITE_BYTES:
        raise ToolError(f"content exceeds {MAX_WRITE_BYTES} bytes")

    old = target.read_text(encoding="utf-8", errors="replace")
    # A small model can only write ~1000 tokens, so a rewrite of a big file that comes out much
    # shorter is almost certainly cut off, and saving it would destroy the rest.
    if len(old) >= TRUNCATION_GUARD_MIN and len(body) < len(old) * 0.6:
        raise ToolError(
            "the new content is much shorter than the file, so it looks truncated. "
            "Use replace_in_file or append_to_file instead."
        )

    _to_trash(target, move=False)
    _write(target, body)
    return f"Updated {_rel(target)} ({len(body)} chars)."


def _tool_replace_in_file(path: str, old_text: str, new_text: str = "", **_: Any) -> str:
    target = safe_path(path, must_exist=True)
    _check_writable_ext(target)
    if target.is_dir():
        raise ToolError(f"'{_rel(target)}' is a directory")
    if not old_text:
        raise ToolError("old_text must not be empty")

    # Compare on \n endings; the file is written back with \n.
    text = target.read_text(encoding="utf-8", errors="replace").replace("\r\n", "\n")
    old_text = old_text.replace("\r\n", "\n")
    n = text.count(old_text)
    if n == 0:
        raise ToolError("old_text not found; read_file first and copy it exactly")
    if n > 1:
        raise ToolError(f"old_text appears {n} times; include more surrounding text")

    body = text.replace(old_text, (new_text or "").replace("\r\n", "\n"), 1)
    if len(body.encode("utf-8")) > MAX_WRITE_BYTES:
        raise ToolError(f"result exceeds {MAX_WRITE_BYTES} bytes")
    _to_trash(target, move=False)
    _write(target, body)
    return f"Edited {_rel(target)} (replaced {len(old_text)} chars with {len(new_text or '')})."


def _tool_append_to_file(path: str, text: str, **_: Any) -> str:
    target = safe_path(path)
    _check_writable_ext(target)
    if target.is_dir():
        raise ToolError(f"'{_rel(target)}' is a directory")
    add = text or ""
    if not add:
        raise ToolError("text must not be empty")

    if target.exists():
        existing = target.read_text(encoding="utf-8", errors="replace").replace("\r\n", "\n")
        sep = "" if existing == "" or existing.endswith("\n") else "\n"
        body = existing + sep + add
        if len(body.encode("utf-8")) > MAX_WRITE_BYTES:
            raise ToolError(f"result exceeds {MAX_WRITE_BYTES} bytes")
        _to_trash(target, move=False)
        verb = "Appended to"
    else:
        body = add
        if len(body.encode("utf-8")) > MAX_WRITE_BYTES:
            raise ToolError(f"content exceeds {MAX_WRITE_BYTES} bytes")
        target.parent.mkdir(parents=True, exist_ok=True)
        verb = "Created"
    _write(target, body)
    return f"{verb} {_rel(target)} ({len(add)} chars added)."


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

    kind = "folder" if target.is_dir() else "file"
    _to_trash(target, move=True)
    return f"Moved {kind} {rel} to trash (kept {TRASH_KEEP_DAYS} days)."


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
            "replace_in_file",
            "Change one passage in an existing file. old_text must appear exactly once; "
            "copy it from read_file. Prefer this over edit_file for any file longer than a few lines.",
            _obj(
                {
                    "path": {**_STR, "description": "Vault-relative path to an existing file"},
                    "old_text": {**_STR, "description": "The exact text to replace (must appear once)"},
                    "new_text": {**_STR, "description": "What to put there instead"},
                },
                ["path", "old_text", "new_text"],
            ),
            RISK_WRITE,
            _tool_replace_in_file,
        ),
        Tool(
            "append_to_file",
            "Add text to the end of a file (creates the file if it does not exist). "
            "Use this to record new facts or notes without rewriting what is already there.",
            _obj(
                {
                    "path": {**_STR, "description": "Vault-relative path including filename and extension"},
                    "text": {**_STR, "description": "The text to add"},
                },
                ["path", "text"],
            ),
            RISK_WRITE,
            _tool_append_to_file,
        ),
        Tool(
            "edit_file",
            "Replace the whole file. Only for small files (under 2000 characters); for bigger "
            "files use replace_in_file or append_to_file. Read the file first.",
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
            "Delete a file, or a folder and everything inside it (moved to trash, kept 30 days). "
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
