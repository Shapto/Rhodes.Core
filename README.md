# Rhodes.Core

Rhodes.Core is a .NET 9 ASP.NET Core application that powers a local AI assistant runtime called Shapto. It combines a chat loop, model connectivity, persistent memory, document and workspace tools, web reading, and an approval gate so the assistant can safely act on the user's behalf.

## What it does

The project is built around a conversational agent that can:

- chat with a local model through LM Studio
- keep multi-turn conversations and a title system
- persist memory and documents in SQLite-backed stores
- read and summarize web pages and GitHub repositories/files
- execute file/workspace operations with user approval
- manage approval rules for web requests, commands, and writes
- expose a small web UI for chat, status, and approval cards

## Project structure

- `Approvals/` - approval requests, decision flow, and database-backed allow rules
- `Chat/` - conversation lifecycle, chat loop, LM Studio client, status monitoring, title generation, and streaming filters
- `Memory/` - memory store and retrieval logic
- `Documents/` - document storage and retrieval
- `Heartbeat/` - background heartbeat and scheduler services
- `Tools/` - tool definitions and tool execution wiring
- `Web/` - HTTP/web fetching and GitHub page/file reading helpers
- `Workspace/` - workspace and file operations
- `wwwroot/` - static web UI assets (HTML, CSS, JavaScript)
- `Program.cs` - app startup, dependency injection, hosted services, and endpoint registration

## Stack

- C# / .NET 9
- ASP.NET Core Minimal APIs
- SQLite via `Microsoft.Data.Sqlite`
- JSON-driven tool and event models
- Local model support through LM Studio

## Getting started

Requirements:

- .NET 9 SDK
- A running local model endpoint compatible with LM Studio
- Optional GitHub token for higher API rate limits when reading GitHub repos/files

Install and run:

```bash
dotnet restore
dotnet build
dotnet run --project Rhodes.Core.csproj
```

Then open the local web app in your browser.

## Configuration

The app reads configuration from:

- `appsettings.json`
- `appsettings.Development.json`

These files are used for the app's runtime settings, model endpoints, and local database paths.

## Key runtime behavior

`Program.cs` wires together the core services for:

- chat and conversation state
- memory and document storage
- approval enforcement
- model status monitoring
- web fetching and GitHub reading
- hosted services such as the heartbeat loop

The project is designed to act like a local AI control plane: the model can reason, decide which tools to call, and consult the user before making risky actions like web requests, file writes, or terminal commands.

## Why it exists

Rhodes.Core is a foundation for an autonomous, local-first assistant that can safely operate in a workspace while preserving memory, surfacing approvals, and reading sources on the web when needed. It focuses on practical agent behaviors rather than a single-purpose chatbot frontend.

## Try asking

- "Summarize the current chat and memory state for this session."
- "Fetch and summarize a GitHub repository README from the web tools."
- "Open a workspace folder, inspect files, and suggest the safest next step."

## License

This project is distributed under the terms of the included `LICENSE.txt` file.
