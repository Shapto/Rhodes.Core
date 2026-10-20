# Rhodes.Core

Rhodes.Core is the runtime engine behind Closure, a local-first AI assistant that runs inside the RhodesIsland workspace. It is a .NET 9 ASP.NET Core app that connects to a local model endpoint, manages conversations, stores memory and documents, coordinates file/workspace actions, and enforces approval gates before any risky operation is allowed.

This project is not a standalone toy app: it is designed to operate as the core runtime for a larger local AI workspace, with sibling folders such as `Data`, `Models`, `llama`, and the workspace helper scripts at the root of the RhodesIsland installation.

## What it does

Rhodes.Core gives Closure the following capabilities:

- chat with a local model over an OpenAI-compatible API (LmStudio / llama-server style endpoint)
- maintain multi-turn conversations with automatic titling, regeneration, pinning, and compaction
- inject relevant memories into the active context and keep memory stores ranked and reviewed
- store and search long-lived memory, document text, and prior chats
- read, summarize, and search web pages and GitHub repositories/files
- operate safely on a workspace with file read/write/edit/delete, repo cloning, and command execution
- run Python in a restricted sandbox for exact numeric and data work
- enforce approval rules for web requests, command execution, file writes, and other risky actions
- run background heartbeat cycles that wake the assistant when the user has been away
- expose a web UI for chat, approvals, memory, journal, tests, and dashboard status
- track model health, memory health, and app activity with persistent status data

## Core features

### Conversation and chat

- streaming chat endpoint with SSE-style updates
- conversation creation, deletion, title overrides, and regeneration
- message pinning and conversation compaction to avoid context overflow
- attachment upload support for text or documents
- per-chat tool use, summarization, and model status validation

### Memory system

- persistent SQLite-backed memory store
- memory ranking and retrieval by relevance, recency, and frequency
- automatic extraction of durable facts from chat history
- memory review proposals and approval workflow
- memory category and search endpoints for debugging and inspection

### Document system

- document ingestion, storage, and indexed maps for long files
- search and read helpers for large documents
- line mapping for easier navigation and grounding in long content
- GitHub and web page extraction into searchable document objects

### Workspace and tool orchestration

The app exposes a broad tool set to the model, including:

- `search_memory` — find relevant stored memories
- `save_memory` — persist important facts or lessons
- `search_chats` and `read_chat` — reference previous conversations
- `search_document` and `read_document` — inspect stored documents
- `web_search` and `web_fetch` — search and fetch web pages / GitHub content
- `list_files`, `read_file`, `write_file`, `edit_file`, `delete_file` — workspace operations
- `run_command` — execute commands in the workspace
- `clone_repo` — clone a repository into the workspace
- `run_python` — sandboxed Python execution for math, data, and exact calculations
- `little_handy` — delegate a heavier investigation task to a helper agent with a fresh context
- `ask_shapto` — ask the human for clarification or a decision when needed

### Safety and approvals

- approval cards for risky actions before execution
- allow-once and always-allow rules for specific categories
- approval categories for web, commands, writes, and sensitive actions
- access gate for the web app with token-based auth and localhost allowance
- strict rules around tool execution so write/command tools are only allowed from a chat flow

### Heartbeat / autonomy

- background heartbeat runner to wake Closure after inactivity
- brief cards and journal entries for assistant activity
- model availability checks before chat or heartbeat wake-ups
- activity tracking so Closure can decide whether it should self-activate

### Testing and validation

- behavior test runner for saved prompt scenarios
- runtime test results and status endpoints
- testing support for model/tool behavior validation in a controlled workflow

### Local model integration

- OpenAI-compatible client wrapper for local LMs
- status monitor for endpoint health and availability
- config based model URL / context token tuning
- model readiness checks before responding

## Project structure

- `Approvals/` — approval requests, rule storage, and decision flow
- `Chat/` — chat loop, conversation state, titles, compaction, status, and streaming logic
- `Memory/` — memory store, memory ranking, review, and retrieval logic
- `Documents/` — document stores, document maps, search/read functionality
- `Heartbeat/` — heartbeat loop, activity tracking, journal/brief cards, background behavior
- `Tools/` — tool implementations and tool abstractions
- `Web/` — web fetching, GitHub page extraction, and web reader code
- `Workspace/` — workspace folder utilities and file-system operations
- `Sandbox/` — sandboxed Python execution environment
- `Security/` — access gate, login, and auth flow
- `Testing/` — behavior test runner and validation tooling
- `wwwroot/` — static web UI assets (HTML, CSS, JS)
- `Program.cs` — dependency wiring, route setup, hosted services, and startup
- `appsettings.json` / `appsettings.Development.json` — runtime configuration

## Stack

- C# / .NET 9
- ASP.NET Core Minimal APIs
- SQLite via `Microsoft.Data.Sqlite`
- JSON-driven tool specs and event payloads
- OpenAI-compatible local model endpoint (LmStudio / llama-server style)
- Client-side UI in HTML/CSS/JavaScript

## Required environment

Requirements:

- .NET 9 SDK
- a local model endpoint compatible with the OpenAI chat completions API
- optional GitHub token for better rate limits when fetching GitHub content
- a RhodesIsland workspace layout with a `Data` folder for local storage

## Getting started

From this repository, or from a solution root that contains it:

```bash
dotnet restore
dotnet build
dotnet run --project Rhodes.Core.csproj
```

Then open the local web app in your browser. The app logs the listening URL and uses the configured model endpoint to serve the chat interface.

If you are running Rhodes.Core inside the full RhodesIsland setup, the workspace root typically contains the following folders and helpers:

- `Backups/`
- `Core/`
- `Data/`
- `llama/`
- `Models/`
- `Rhodes.Core/`
- `Publish-Core.bat`
- `Rollback-Rhodes.bat`
- `Setup-Rhodes.bat`
- `Start-Rhodes.bat`

This project expects to find its configuration and data files in the RhodesIsland `Data` folder, and it is designed to run from that workspace layout without losing local settings across updates.

## Configuration

The app reads configuration from:

- `appsettings.json`
- `appsettings.Development.json`
- `appsettings.Local.json` (optional, loaded from the RhodesIsland `Data` folder when present)

Common configuration areas include:

- LLM base URL and model name
- workspace path
- memory database and backup path
- document storage path
- GitHub token
- security token settings
- model context size and autosave behavior

## Runtime behavior

`Program.cs` wires together the key runtime components:

- chat loop and conversation manager
- memory ranker, extractor, and reviewer
- document and upload stores
- approval service and approval rules
- model status monitoring
- web and GitHub readers
- heartbeat runner and hosted service
- security access gate
- workspace and tool providers

This makes the app behave like a local AI control plane: the model can reason, plan actions, call tools, request approval, and keep working safely inside the user’s environment while preserving memory and exposing clear status information.

## Why it exists

Rhodes.Core exists to provide a safe, local, workspace-aware assistant runtime. It blends memory, tool use, approval checks, and model orchestration into a single core that can operate as a private local AI helper for coding, research, analysis, and file operations without requiring a cloud-controlled agent.

## Example prompts

- "Summarize this chat and tell me what memory is relevant right now."
- "Search the workspace for the likely root cause and explain the safest next step."
- "Fetch and summarize a GitHub repo README and compare it to the local project structure."
- "Review my last few conversations and suggest any facts worth preserving in memory."
- "Run a small Python calculation in the sandbox and show the result."
- "Open a workspace folder, inspect files, and propose the safest next change."

## Operational notes

- The app intentionally gates risky actions behind approvals.
- The workspace is treated as a sandbox: write and command tools are expected to be used under the user’s explicit approval.
- Model health is checked before processing a chat or wake-up cycle.
- Memory compaction and review happen automatically to keep context manageable over time.

## License

This project is distributed under the terms of the included `LICENSE.txt` file.
