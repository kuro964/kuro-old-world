# Codebase Architecture

> Project structure, the three-layer convention, and where things live.

[Back to Home](Home)

## The Three-Layer Convention

| Directory | Owner | Rule |
|-----------|-------|------|
| Base directories (e.g., `Content.Server/Station/`) | Upstream SS14 | Do not modify unless absolutely necessary |
| `_Stalker/` | Upstream Stalker-14 (closed Russian fork) | Do not modify unless absolutely necessary |
| `_Stalker_EN/` | EN fork of Stalker | Code may be adjusted if neccessary for cohesion |
| `_Stalker_OW` | **This fork** | **New additions should be made here**

### Why This Exists

Stalker14-Old-World sits at the end of a four-project inheritance chain:

```
Space Station 14 (upstream engine + content)
  +-- Stalker-14 (closed Russian fork)
       +-- stalker-14-EN (English localization + new features)
            +-- Stalker14-Old-World (Continuation of abandoned EN project)
```

Upstream Stalker-14 changes get merged periodically. The `_Stalker_OW/` convention creates a clean boundary: upstream merges only touch base and `_Stalker/` files, while OW work only touches `_Stalker_EN/` and `_Stalker_OW` files. Conflicts become rare and isolated.

> **Warning**: When you *must* modify a file outside `_Stalker_OW/`, mark changes with `// st:ow` (single line) or `// stalker-ow-start` / `// stalker-ow-end` (multi-line blocks). Do **not** use `// stalker-changes`, which is the upstream Stalker marker.

## Directory Structure

```
stalker-14-EN/
+-- Content.Client/                  # Client-side code (UI, rendering, visual-only systems)
|   +-- _Stalker/                    # Upstream Stalker client code (DO NOT MODIFY)
|   +-- _Stalker_EN/                 # EN-specific client code
|
+-- Content.Server/                  # Server-side code (authoritative game logic)
|   +-- _Stalker/                    # Upstream Stalker server code (DO NOT MODIFY)
|   +-- _Stalker_EN/                 # EN-specific server code
|
+-- Content.Shared/                  # Shared code (runs on BOTH client and server)
|   +-- _Stalker/                    # Upstream Stalker shared code (DO NOT MODIFY)
|   +-- _Stalker_EN/                 # EN-specific shared code
|
+-- Content.Server.Database/         # EF Core database models and migrations
+-- Content.Shared.Database/         # Database enums and types shared with other projects
+-- Content.IntegrationTests/        # Integration tests (NUnit)
+-- Content.Tests/                   # Unit tests (NUnit)
|
+-- Resources/
|   +-- Prototypes/
|   |   +-- _Stalker/               # Upstream YAML prototypes
|   |   +-- _Stalker_EN/            # EN-specific YAML prototypes
|   +-- Locale/
|   |   +-- en-US/_Stalker_EN/      # EN localization strings (.ftl files)
|   |   +-- ru-RU/                  # Russian locale (upstream)
|   +-- Textures/_Stalker_EN/       # EN-specific sprites
|   +-- Audio/_Stalker_EN/          # EN-specific audio assets
|   +-- Maps/                       # Map files
|
+-- Corvax/                          # Corvax interface projects (upstream dependency)
+-- RobustToolbox/                   # Engine submodule (NEVER MODIFY)
+-- Stalker14.sln                    # Solution file
```

## Client / Server / Shared Split

**Shared** (`Content.Shared/`): Compiled into both client and server. Components, events, shared systems (prediction), data types, enums.

**Server** (`Content.Server/`): Server-only. Authoritative game logic, anti-cheat, database access, hidden AI decisions.

**Client** (`Content.Client/`): Client-only. UI (always XAML), visual effects, input handling, prediction overrides.

### How to Decide

1. **Both sides need this type or logic?** Shared.
2. **Determines what "really happened"?** Server.
3. **Purely visual, UI, or input?** Client.
4. **Unsure?** Default to Server.

> **Tip**: Components almost always go in Shared, even if the system is server-only. The client needs the component definition to deserialize entity state.

## CCVars: Runtime Configuration

CCVars can be changed at runtime through the server console or config files without recompiling.

EN-specific CCVars are defined in `Content.Shared/_Stalker_EN/CCVar/` as partial classes on `STCCVars`:

| File | Feature Area |
|------|-------------|
| `STCCVars.Emission.cs` | Emission system visuals and raycasting |
| `STCCVars.FactionRelations.cs` | Discord webhook, cooldown, proposal expiration |
| `STCCVars.LateJoin.cs` | Late join announcement toggle |
| `STCCVars.Loadout.cs` | Rate limiting |
| `STCCVars.Messenger.cs` | Discord webhook, max message length |

All EN CVars use the `stalkeren.*` prefix. Upstream Stalker uses `stalker.*`. Do not add new variables to `_Stalker/CCCCVars/`; use the `STCCVars` partial class in `_Stalker_EN/`.

To add a new CCVar: create `STCCVars.YourFeature.cs` in `Content.Shared/_Stalker_EN/CCVar/`. Naming: `stalkeren.feature.variable_name`. Flags: `CVar.SERVERONLY`, `CVar.CLIENTONLY`, `CVar.REPLICATED`, `CVar.CONFIDENTIAL`.

## Related Pages

- [Home](Home)
- [Contributing Guide](Contributing-Guide): coding conventions, ECS rules, naming, style, and PR workflow
- [Robust Toolbox ECS](https://docs.spacestation14.com/en/robust-toolbox/ecs.html): engine ECS documentation
- [SS14 Codebase Organization](https://docs.spacestation14.com/en/general-development/codebase-info/codebase-organization.html)
- [SS14 Conventions](https://docs.spacestation14.com/en/general-development/codebase-info/conventions.html)
- [SS14 PR Guidelines](https://docs.spacestation14.com/en/general-development/codebase-info/pull-request-guidelines.html)
