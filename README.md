# Rhodes.Core

Rhodes.Core is a .NET 9 ASP.NET Core application that powers a local AI assistant runtime called Closure. It combines a chat loop, model connectivity, persistent memory, document and workspace tools, and a small web UI for interacting with the assistant.

Important: this repository is typically used as part of a larger RhodesIsland workspace. See the "RhodesIsland workspace" section below for the local directory layout and helper scripts that are commonly used when running the whole system.

## What it does

The project is built around a conversational agent that can:

- chat with a local model through an OpenAI-compatible endpoint (llama-server)
- keep multi-turn conversations and a title system
- persist memory and documents in SQLite-backed stores
- read and summarize web pages and GitHub repositories/files
- execute file/workspace operations with user approval
- manage approval rules for web requests, commands, and writes
- expose a small web UI for chat, status, and approval cards

## Project structure

- `Approvals/` - approval requests, decision flow, and database-backed allow rules
- `Chat/` - conversation lifecycle, chat loop, OpenAI-compatible client, status monitoring, title generation, and streaming filters
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
- OpenAI-compatible local model endpoint (llama-server)

## Getting started

Requirements:

- .NET 9 SDK
- A running local model endpoint compatible with the OpenAI chat completions API (llama-server)
- Optional GitHub token for higher API rate limits when reading GitHub repos/files

Install and run (from this repository or a solution root containing this project):

```bash
dotnet restore
dotnet build
dotnet run --project Rhodes.Core.csproj
```

Then open the local web app in your browser (the app will print the listening URL to the console).

If you run this repository as part of the RhodesIsland workspace (see below), there are convenience scripts to help initialize and start the whole environment.

## Configuration

The app reads configuration from (in order of precedence):

- `appsettings.json`
- `appsettings.Development.json`

These files are used for the app's runtime settings, model endpoints, and local database paths. Environment variables can also be used to override configuration values.

Key configuration points you will commonly change:

- Model endpoint URL (the OpenAI-compatible endpoint / chat completions URL)
- Local database file path for memory and documents
- Optional GitHub token for higher rate limits when reading repositories

## Running as part of the RhodesIsland workspace

When Rhodes.Core is used inside the RhodesIsland root workspace, the root typically contains multiple sibling folders and helper scripts. An example local workspace layout (your environment) is:

- Backups/
- Core/
- Data/
- llama/
- Models/
- Rhodes.Core/

And useful helper scripts at the workspace root include:

- `Publish-Core.bat` — publish/build helper for the Core projects
- `Rollback-Rhodes.bat` — rollback helper for deployments
- `Setup-Rhodes.bat` — initial setup script for the workspace
- `Start-Rhodes.bat` — convenience script to start the workspace services

If you have the full workspace, run `Setup-Rhodes.bat` once to prepare data and dependencies, then use `Start-Rhodes.bat` to start services. The exact behavior of those scripts depends on your local environment and are maintained in the workspace root.

## Key runtime behavior

`Program.cs` wires together the core services for:

- chat and conversation state
- memory and document storage
- approval enforcement
- model status monitoring
- web fetching and GitHub reading
- hosted services such as the heartbeat loop

The project is designed to act like a local AI control plane: the model can reason, decide which tools to call, and consult the user before making risky actions like web requests, file writes, or other operations that require explicit approval.

## Why it exists

Rhodes.Core is a foundation for an autonomous, local-first assistant that can safely operate in a workspace while preserving memory, surfacing approvals, and reading sources on the web when needed.

## Try asking

- "Summarize the current chat and memory state for this session."
- "Fetch and summarize a GitHub repository README from the web tools."
- "Open a workspace folder, inspect files, and suggest the safest next step."

## Contributing

This repository is typically developed as part of the larger RhodesIsland workspace. If you send contributions, try to include context on how changes affect other components in the workspace (model endpoint, workspace scripts, Data/Models folders, etc.).

## Troubleshooting

- If the web UI does not appear, check the console output for the listening URL and ensure there are no model endpoint errors.
- If the app cannot reach your model endpoint, verify the endpoint URL and that the llama-server (or equivalent) is running.
- For GitHub reads, set a `GITHUB_TOKEN` environment variable to avoid low rate limits.

## License

This project is distributed under the terms of the included `LICENSE.txt` file.
