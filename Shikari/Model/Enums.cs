namespace Shikari.Model;

/// <summary>Broad party role, used for default colours, sorting and spell filtering.</summary>
public enum RaidRole
{
    Unknown = 0,
    Tank = 1,
    Healer = 2,
    Melee = 3,
    PhysicalRanged = 4,
    MagicalRanged = 5,
}

/// <summary>Outline of the arena drawn behind every slide.</summary>
public enum ArenaShape
{
    None = 0,
    Circle = 1,
    Square = 2,
    Rectangle = 3,
    Octagon = 4,
    Hexagon = 5,
}

/// <summary>What a single object on a slide represents.</summary>
public enum CanvasItemKind
{
    /// <summary>A member of the roster. Follows the roster slot's job colour and label.</summary>
    PlayerToken = 0,

    /// <summary>The boss, an add, or any other hostile marker.</summary>
    EnemyToken = 1,

    /// <summary>A field marker: A/B/C/D or 1/2/3/4.</summary>
    Waymark = 2,

    /// <summary>Free text pinned to the arena.</summary>
    Label = 3,

    /// <summary>A multi-point arrow showing movement.</summary>
    Arrow = 4,

    /// <summary>A telegraphed damage area.</summary>
    Zone = 5,

    /// <summary>A line drawn between two points, e.g. a tether or a chain.</summary>
    Tether = 6,

    /// <summary>Freehand pen strokes.</summary>
    Freehand = 7,

    /// <summary>Positioned mechanic artwork or an emoji, independent of player seats.</summary>
    Symbol = 8,
}

/// <summary>Small, explicitly supported diagrams whose geometry is owned by the renderer.</summary>
public enum SymbolAsset
{
    None = 0,
    Cut4 = 1,
}

/// <summary>Geometry of a <see cref="CanvasItemKind.Zone"/>.</summary>
public enum ZoneShape
{
    Circle = 0,
    Donut = 1,
    Rectangle = 2,
    Cone = 3,
    Line = 4,
    Cross = 5,
}

/// <summary>How a timeline entry decides that its moment has arrived.</summary>
public enum TriggerKind
{
    /// <summary>Fires at a fixed number of seconds after combat starts.</summary>
    CombatTime = 0,

    /// <summary>Fires relative to the boss actually starting a named cast.</summary>
    BossCast = 1,

    /// <summary>Fires a number of seconds after another cast started. Good for pre-warning
    /// a mechanic that has no cast bar of its own.</summary>
    AfterCast = 2,

    /// <summary>Never fires automatically; only reachable from the Test button.</summary>
    Manual = 3,

    /// <summary>
    /// Fires at the time the plugin has learned this cast usually happens, corrected for how
    /// fast the current pull is running. Unlike <see cref="BossCast"/>, this can warn before the
    /// boss has started casting — which is the only way to give more warning than a cast bar is
    /// long. Falls back to <see cref="BossCast"/> behaviour until the fight has been seen a few
    /// times.
    /// </summary>
    Predicted = 4,
}

/// <summary>Where a shotcall is delivered.</summary>
[System.Flags]
public enum ReminderChannel
{
    None = 0,
    Overlay = 1 << 0,
    Chat = 1 << 1,
    Toast = 1 << 2,
    Sound = 1 << 3,

    /// <summary>Read out loud by Windows' own speech service.</summary>
    Speech = 1 << 4,
}

/// <summary>Which members of the party a timeline entry talks to.</summary>
public enum CallAudience
{
    /// <summary>Everyone sees the call.</summary>
    Everyone = 0,

    /// <summary>Only roster slots that have an assignment or a custom line on this entry.</summary>
    AssignedOnly = 1,
}

/// <summary>When the compact in-fight plan window puts itself on screen.</summary>
public enum MiniPlanVisibility
{
    /// <summary>Only when opened by hand with /shikari mini.</summary>
    Never = 0,

    /// <summary>Raids, savage and ultimate. The content people build plans for.</summary>
    RaidContent = 1,

    /// <summary>Anything you are bound by duty in, dungeons and trials included.</summary>
    AnyDuty = 2,

    /// <summary>Inside a duty, but only once the pull is running.</summary>
    InCombatOnly = 3,
}
