# Contributing Guide

> Coding conventions, code placement rules, and contribution workflow for Stalker-14-EN.

This page is the reference for how to write code that fits into the Stalker-14-EN codebase. For project structure rationale and the Shared/Server/Client split, see [Codebase Architecture](Codebase-Architecture).

## Where to Put New Code

### The Three-Layer Rule

```
Base SS14 code           -->  Do not modify (unless absolutely necessary)
_Stalker/ directories    -->  Upstream Stalker code. Do not modify (unless absolutely necessary)
_Stalker_EN/ directories -->  ALL EN-fork code
_Stalker_OW/ directories -->  ALL new OW-fork code goes here
```

Every new feature, bugfix, or extension specific to the OW fork **should** live under a `_Stalker_OW/` directory. This is negotiable in cases of adjustments to EN code.

### Directory Map

| What you are writing | Where it goes |
|---|---|
| Components, events, enums, shared systems | `Content.Shared/_Stalker_OW/YourFeature/` |
| Server-only game logic, authoritative systems | `Content.Server/_Stalker_OW/YourFeature/` |
| UI code, visuals, client-only systems | `Content.Client/_Stalker_OW/YourFeature/` |
| YAML entity/prototype definitions | `Resources/Prototypes/_Stalker_OW/YourFeature/` |
| Localization strings | `Resources/Locale/en-US/_Stalker_OW/yourfeature.ftl` |

### Prototype Path Mirroring

When overriding or extending upstream prototypes, **mirror the upstream folder structure** inside `_Stalker_EN/`. This keeps the relationship between original and overridden prototypes obvious and makes it easy to trace changes back to the source.

```
Upstream prototype at:
  Resources/Prototypes/_Stalker/Entities/Mobs/mutants.yml

Override goes to:
  Resources/Prototypes/_Stalker_OW/Entities/Mobs/mutants.yml
```

Do **not** invent a new folder hierarchy for overridden prototypes. The path under `_Stalker_OW/` should match the path under `_Stalker/` (or the base `Prototypes/` directory if overriding base SS14 prototypes).

### When You Must Touch Upstream Code

1. **Minimize the diff**: smallest possible change
2. **Mark it clearly** with the correct comment marker (see below)
3. **Ask before proceeding**: modifications to upstream code should be reviewed first

**Comment markers for OW-fork changes to upstream files:**

```csharp
// Single line change:
var result = DoSomething(); // stalker-ow

// Multi-line block:
// stalker-ow-start
var custom = NewLogic();
ApplyCustom(custom);
// stalker-ow-end
```

> **Warning**: Upstream Stalker uses `// stalker-changes` for its own modifications. Do **not** use that marker for OW-fork changes.

## Naming Conventions

### C# Identifiers

| Kind | Convention | Example |
|---|---|---|
| Classes, structs, enums | `PascalCase` | `STSoftCritComponent` |
| Interfaces | `IPascalCase` | `IRadarTargetSource` |
| Methods, properties | `PascalCase` | `OnMapInit`, `CrawlSpeedModifier` |
| Private fields | `_camelCase` | `_random`, `_sawmill` |
| Parameters, locals | `camelCase` | `frameTime`, `uid` |
| Constants | `PascalCase` | `DefaultCrawlSpeed` |
| Stalker-specific types | `ST` prefix | `STWeaponSystem`, `STMobVariantComponent` |

> **Note**: The `ST` prefix is **recommended** for all Stalker-specific types. It prevents name collisions with base SS14 types and makes grep-based navigation reliable.

### Localization IDs

Always `kebab-case`, never capitalized. Prefix with `st-` to avoid clashing with base SS14 locale keys. All player-facing strings must use `Loc.GetString()`.

```
st-trophy-examine-quality = [color={$color}]{$quality}[/color]
st-messenger-channel-general = General
st-soft-crit-crawl-popup = You begin crawling...
```

### YAML Identifiers

| Kind | Convention | Example |
|---|---|---|
| Prototype IDs | `PascalCase` | `STMessengerCartridge`, `STGeneral` |
| Component type names | `PascalCase` | `STMessenger`, `STSoftCritEnabled` |
| Data fields | `camelCase` | `crawlSpeedModifier`, `sortOrder` |

## ECS Rules

These are architectural requirements of the Robust Toolbox engine, not style preferences.

### Components Are Pure Data

No logic, no methods that compute values, no property setters with side effects. All data must be public.

```csharp
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class STSoftCritComponent : Component
{
    [DataField, AutoNetworkedField]
    public float CrawlSpeedModifier = 0.3f;

    [DataField, AutoNetworkedField]
    public FixedPoint2 HardCritThreshold;
}
```

### All Logic Lives in Systems

EntitySystems subscribe to events and operate on entities through their components. Prefer `EntitySystem` proxy methods (`Name(uid)`, `Transform(uid)`) over direct `EntityManager` access.

```csharp
public sealed class STMobVariantSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<STMobVariantConfigComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(EntityUid uid, STMobVariantConfigComponent config, MapInitEvent args)
    {
        // All logic here, never in the component
    }
}
```

### Public API Signatures

Use `Entity<T?>` for entity/component parameters and call `Resolve()` immediately:

```csharp
public void SetCount(Entity<StackComponent?> stack, int count)
{
    if (!Resolve(stack, ref stack.Comp))
        return;

    stack.Comp.Count = count;
    Dirty(stack);
}
```

### Dirty() and DirtyField()

When you modify a networked component's state, you must mark it dirty. Forgetting this is the most common "works on server but clients see stale data" bug.

**`Dirty()`**: Use when most/all fields changed.

```csharp
marker.Quality = variant.Quality;
marker.Applied = true;
Dirty(uid, marker);
```

**`DirtyField()`**: Use for components with 3+ independently changing networked fields. Requires `AutoGenerateComponentState(fieldDeltas: true)` on the component.

```csharp
comp.Health = newValue;
DirtyField(uid, comp, nameof(STExampleComponent.Health));
```

> **Warning**: Server-only components (like `DestructibleComponent`) do not need dirty calls. If unsure whether a component is networked, call it anyway; it is a no-op on non-networked components.

### Events

Events must be **structs** with `[ByRefEvent]`, raised by reference. Name with `Event` suffix; handlers named `OnXEvent`.

```csharp
[ByRefEvent]
public record struct STSoftCritSpeechEvent(EntityUid Source)
{
    public bool Override = false;
}
```

Do not raise events directly for actions. Wrap them in entity system methods:

```csharp
// CORRECT: System method wraps the event
public void ApplyDamage(EntityUid target, DamageSpecifier damage)
{
    var ev = new STDamageAppliedEvent(target, damage);
    RaiseLocalEvent(target, ref ev);
}
```

Use EventBus for simulation code, not C# events. C# events are only for out-of-simulation code (UI). Always unsubscribe from C# events to prevent leaks.

### Optional Entities

Use nullable `EntityUid?` to represent absence. Never use `EntityUid.Invalid`.

```csharp
// CORRECT
public EntityUid? Target;

// WRONG
public EntityUid Target = EntityUid.Invalid;
```

### Additional ECS Rules

- **Mark all classes** as `sealed`, `abstract`, `static`, or `[Virtual]`.
- **No async in simulation code**: use events and `DoAfter` instead.
- **No extension methods** on `EntityUid`, components, or systems.
- **Use `[DataField]`** for YAML-configurable component fields.
- **Use `TimeSpan`** instead of `float` for time durations. Apply `[AutoPausedField]` for absolute time points. Apply `[AutoGenerateComponentPause]` to the component class.
- **Use `ProtoId<T>`** instead of caching prototype references.
- **Use `SoundSpecifier`** (prefer `SoundCollectionSpecifier`) for sound fields.
- **Use `SpriteSpecifier`** for sprite/texture fields.

## The Reusability Mandate

Before writing new code, search the existing codebase thoroughly.

### Search Order

1. `Content.Shared/_Stalker_OW/` and `Content.Server/_Stalker_OW/`: has it been added to the OW fork?
2. `Content.Shared/_Stalker_EN/` and `Content.Server/_Stalker_EN/`: has the EN fork already solved this?
3. `Content.Shared/_Stalker/` and `Content.Server/_Stalker/`: does upstream Stalker have it?
4. `Content.Shared/` and `Content.Server/`: does base SS14 provide this?

### Decision Tree

```
Can an existing system do exactly what I need?
  YES --> Use it directly.
  NO  --> Can it be extended?
            YES --> Extend it (prefer composition over modification).
            NO  --> Create new code in _Stalker_OW/.
```

Duplicating existing functionality is unacceptable.

## Code Style

### Formatting

- **4-space indentation** (spaces, not tabs)
- **120-character max line length**
- **Allman-style braces** (opening brace on its own line)
- **File-scoped namespaces**
- **Fields and auto-properties before methods**
- **Trailing commas** in multiline initializer lists
- Use `var` when the type is apparent
- No `this.` qualification
- Use C# keywords over BCL types (`int` not `Int32`)

```csharp
namespace Content.Server._Stalker_EN.MobVariant;

public sealed class STMobVariantSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;

    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = Logger.GetSawmill("st.mob.variant");
        SubscribeLocalEvent<STMobVariantConfigComponent, MapInitEvent>(OnMapInit);
    }
}
```

### Documentation

Use XML documentation comments on public classes, methods, properties, and non-obvious fields. Comments should explain **why**, not restate **what**.

```csharp
/// <summary>
/// Runtime component added/removed when an entity is in the soft crit sub-phase.
/// Its presence means the entity can crawl slowly and whisper but cannot stand,
/// use items, or speak normally.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class STSoftCritComponent : Component
```

### YAML Prototype Style

- Separate prototypes with one blank line
- No blank lines between components within a prototype
- Field order: `type` -> `abstract` -> `parent` -> `id` -> `categories` -> `name` -> `suffix` -> `description` -> `components`
- Engine components before content components
- `name:` and `description:` without quotes unless punctuation requires single quotes
- Do not specify textures in abstract prototypes
- Use `# MARK: Topic Name` comments to separate logical sections in multi-section YAML files

```yaml
# MARK: Base Prototypes

- type: entity
  parent: BaseItem
  id: STMessengerCartridge
  name: st-messenger-cartridge-name
  description: st-messenger-cartridge-description
  components:
  - type: Sprite
    sprite: Objects/Devices/cartridge.rsi
    state: cart-y
  - type: UIFragment
    ui: !type:STMessengerUi
  - type: Cartridge
    programName: st-messenger-program-name
  - type: STMessenger
```

### UI Code

Always use **XAML over C#-defined UIs**. Extending existing C#-defined UIs is acceptable temporarily, but new UI should be XAML.

## New Feature Directory Layout

```
Content.Shared/_Stalker_OW/YourFeature/
    YourComponent.cs           # Component(s) - pure data
    YourEvents.cs              # Event definitions
    SharedYourSystem.cs        # Shared logic (if any)

Content.Server/_Stalker_OW/YourFeature/
    YourSystem.cs              # Server authoritative logic

Content.Client/_Stalker_OW/YourFeature/
    YourUi.cs                  # BUI / UI fragment
    YourWindow.xaml            # XAML UI layout (always XAML, not C#)
    YourWindow.xaml.cs         # Code-behind

Resources/Prototypes/_Stalker_EN/YourFeature/
    entities.yml               # Entity prototypes

Resources/Locale/en-US/_Stalker_OW/yourfeature/
    yourfeature.ftl            # Localization strings
```

Single-file systems do not need subfolders.

## Contribution Workflow

### Branch and PR Process

1. Remote clone [Stalker-Old-World/Stalker14-Old-World] (https://github.com/Stalker-Old-World/Stalker14-Old-World)
2. Create a feature branch from `master`
3. Follow the conventions on this page
4. Test all changes in-game (not just compilation)
5. Push to your fork and open a pull request against `master` on the main repo
6. Address review feedback

## Upstream References

- [SS14 Codebase Conventions](https://docs.spacestation14.com/en/general-development/codebase-info/conventions.html): base rules this project inherits
- [SS14 Pull Request Guidelines](https://docs.spacestation14.com/en/general-development/codebase-info/pull-request-guidelines.html): upstream PR expectations

Where this page and the SS14 docs conflict, **this page takes precedence**.

## Contributing to the Wiki

Wiki source files live in `docs/wiki/` in the main repository. Edit or add `.md` files and submit a PR like any code change. A GitHub Action syncs `docs/wiki/` to the GitHub wiki on merge to `master`.

### Page Conventions

- **Filenames**: `Page-Name.md` (PascalCase words separated by hyphens)
- **Link format**: `[Link Text](Page-Name)` (no `.md` extension)
- **New feature pages**: Use the `stalker-wiki-writer` agent via Claude Code to generate documentation

---

[Back to Home](Home)
