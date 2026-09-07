using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using Shikari.Model;

namespace Shikari;

/// <summary>
/// Per-team presentation and delivery settings. A static that wants terse calls and a static
/// that wants verbose ones can keep separate profiles against the same plan.
/// </summary>
public sealed class TeamProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Default team";

    /// <summary>
    /// Which roster seat this client is. -1 means "work it out from the character name".
    /// </summary>
    public int PinnedSlotIndex { get; set; } = -1;

    public ReminderChannel Channels { get; set; } =
        ReminderChannel.Overlay | ReminderChannel.Chat | ReminderChannel.Sound;

    /// <summary>Seconds the overlay banner stays up after firing.</summary>
    public float OverlayHoldSeconds { get; set; } = 4f;

    /// <summary>Scale applied to the overlay text on top of the global UI scale.</summary>
    public float OverlayTextScale { get; set; } = 2.0f;

    public Vector4 OverlayTextColor { get; set; } = new(1f, 0.86f, 0.4f, 1f);

    public Vector4 OverlayBackgroundColor { get; set; } = new(0f, 0f, 0f, 0.55f);

    /// <summary>Chat sound effect id (1-16) used when the Sound channel is on.</summary>
    public uint SoundEffectId { get; set; } = 6;

    /// <summary>Speaking rate, -10 to 10. Zero is the voice's own pace.</summary>
    public int SpeechRate { get; set; }

    public int SpeechVolume { get; set; } = 90;

    /// <summary>Installed voice to use. Empty means whichever Windows has set as default.</summary>
    public string SpeechVoice { get; set; } = string.Empty;

    /// <summary>
    /// Read out what everyone else is pressing as well as your own line. Off: it is useful to
    /// glance at and hard to listen to while dodging.
    /// </summary>
    public bool SpeakOtherPlayersCalls { get; set; }

    /// <summary>
    /// Fallback line used when a timeline entry has no call text of its own.
    /// Supports the same tokens as per-entry text.
    /// </summary>
    public string DefaultTemplate { get; set; } = "{label}: {abilities}";

    /// <summary>Prefix stamped in front of every chat-channel call.</summary>
    public string ChatPrefix { get; set; } = "[Shikari]";

    /// <summary>Only deliver calls while in a duty.</summary>
    public bool OnlyInDuty { get; set; } = true;

    /// <summary>Show calls that are addressed to other players too, in a dimmer style.</summary>
    public bool ShowOtherPlayersCalls { get; set; }

    /// <summary>Extra seconds added to every entry's lead time for this client.</summary>
    public float LeadTimeAdjust { get; set; }

    public TeamProfile Clone()
    {
        return new TeamProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Name + " (copy)",
            PinnedSlotIndex = PinnedSlotIndex,
            Channels = Channels,
            OverlayHoldSeconds = OverlayHoldSeconds,
            OverlayTextScale = OverlayTextScale,
            OverlayTextColor = OverlayTextColor,
            OverlayBackgroundColor = OverlayBackgroundColor,
            SoundEffectId = SoundEffectId,
            SpeechRate = SpeechRate,
            SpeechVolume = SpeechVolume,
            SpeechVoice = SpeechVoice,
            SpeakOtherPlayersCalls = SpeakOtherPlayersCalls,
            DefaultTemplate = DefaultTemplate,
            ChatPrefix = ChatPrefix,
            OnlyInDuty = OnlyInDuty,
            ShowOtherPlayersCalls = ShowOtherPlayersCalls,
            LeadTimeAdjust = LeadTimeAdjust,
        };
    }
}

public sealed class Configuration : IPluginConfiguration
{
    public bool ReplayEnabled { get; set; } = true;
    public int ReplayRetention { get; set; } = 10;

    public int Version { get; set; } = 1;

    /// <summary>Id of the plan currently loaded in the planner window.</summary>
    public string ActivePlanId { get; set; } = string.Empty;

    public List<TeamProfile> Teams { get; set; } = new();

    public string ActiveTeamId { get; set; } = string.Empty;

    /// <summary>Master switch for live shotcalls.</summary>
    public bool RemindersEnabled { get; set; } = true;

    /// <summary>Open the planner automatically when a duty starts.</summary>
    public bool OpenOnDutyStart { get; set; }

    /// <summary>Follow the fight live: move the visible slide on as the pull progresses.</summary>
    public bool AutoAdvanceSlides { get; set; } = true;

    /// <summary>
    /// Switch slides the moment a boss cast a step is anchored to is detected, rather than
    /// waiting for that step's call to fire. This is the responsive option; turn it off if you
    /// would rather the slide arrive with the shotcall.
    /// </summary>
    public bool AutoAdvanceOnCast { get; set; } = true;

    /// <summary>Jump back to the first slide when a pull ends badly.</summary>
    public bool ResetSlidesOnWipe { get; set; } = true;

    /// <summary>
    /// How long automatic slide changes stand down after the player changes slides by hand.
    /// A wipe clears this immediately.
    /// </summary>
    public float ManualOverrideSeconds { get; set; } = 20f;

    /// <summary>Record fight timings across pulls and use them to predict mechanics.</summary>
    public bool LearningEnabled { get; set; } = true;

    /// <summary>
    /// How sure the plugin has to be of a learned timing before a predicted step will fire on it.
    /// Below this, the step waits for the real cast instead.
    /// </summary>
    public float MinimumPredictionConfidence { get; set; } = 0.45f;

    /// <summary>Position of the overlay banner, as a fraction of the main viewport.</summary>
    public Vector2 OverlayAnchor { get; set; } = new(0.5f, 0.28f);

    /// <summary>Let the overlay be dragged with the mouse.</summary>
    public bool OverlayUnlocked { get; set; }

    /// <summary>FF Logs API client id, from fflogs.com/api/clients. Only needed for log import.</summary>
    public string FfLogsClientId { get; set; } = string.Empty;

    public string FfLogsClientSecret { get; set; } = string.Empty;

    /// <summary>Last report someone imported, so the box is not empty next time.</summary>
    public string LastReportUrl { get; set; } = string.Empty;

    /// <summary>Show only cooldowns in the action picker, rather than a job's whole kit.</summary>
    public bool CooldownsOnly { get; set; } = true;

    /// <summary>Print every detected boss cast to the log window, for building timelines.</summary>
    public bool LogDetectedCasts { get; set; }

    // ---------------------------------------------------------------- theme

    /// <summary>The dark glass look. Off falls back to whatever Dalamud's style is.</summary>
    public bool ThemeEnabled { get; set; } = true;

    /// <summary>Accent colour as 0xRRGGBB. Zero means the default indigo.</summary>
    public uint ThemeAccent { get; set; }

    /// <summary>Soft shadow behind the plugin's windows.</summary>
    public bool ThemeShadows { get; set; } = true;

    /// <summary>Icons on the drawing tools instead of text labels.</summary>
    public bool ThemeToolIcons { get; set; } = true;

    // ---------------------------------------------------------------- live positions

    /// <summary>
    /// Draw the party where they actually are, over the plan. Needs the waymarks placed, since
    /// they are the only thing that says which way round the arena is.
    /// </summary>
    public bool ShowLivePositions { get; set; } = true;

    /// <summary>Line each player up with the spot the plan gives their seat.</summary>
    public bool LivePositionGuides { get; set; } = true;

    /// <summary>Show them in the planner too, not only in the small in-fight window.</summary>
    public bool LivePositionsInPlanner { get; set; } = true;

    // ---------------------------------------------------------------- mini plan

    /// <summary>When the compact in-fight window appears by itself.</summary>
    public MiniPlanVisibility MiniPlanMode { get; set; } = MiniPlanVisibility.RaidContent;

    /// <summary>
    /// Size limits for the mini plan, in unscaled pixels. The game's minimap is around 218.
    /// </summary>
    /// <remarks>
    /// One pair, used by the settings slider, the corner grip and the window itself. They were
    /// three separate pairs of numbers, so dragging the corner past what the slider allowed left
    /// a window at a size the slider would snap away the next time it was opened.
    /// </remarks>
    public const float MiniPlanMinSize = 120f;
    public const float MiniPlanMaxSize = 640f;

    /// <summary>Width of the mini plan in unscaled pixels.</summary>
    public float MiniPlanSize { get; set; } = 220f;

    /// <summary>Where it sits, as a fraction of the viewport, so it survives a resolution change.</summary>
    public Vector2 MiniPlanAnchor { get; set; } = new(0.87f, 0.62f);

    /// <summary>Overall opacity of the panel behind the arena.</summary>
    public float MiniPlanOpacity { get; set; } = 0.85f;

    /// <summary>Ring the token belonging to this client's seat.</summary>
    public bool MiniPlanHighlightMe { get; set; } = true;

    /// <summary>
    /// Play down everyone else on the mini window so your own instruction stands out.
    /// </summary>
    /// <remarks>
    /// On by default. Eight tokens and eight sets of movement is a lot to read in the second you
    /// have; one is not. Only the players are dimmed — every shape describing the mechanic itself
    /// stays exactly as bright, because that is what you are dodging.
    /// </remarks>
    public bool MiniPlanOnlyMe { get; set; } = true;

    /// <summary>Hide other planned and observed players in the mini, keeping mechanics and your destinations.</summary>
    public bool MiniPlanYourView { get; set; }

    /// <summary>How far you can be from your spot and still count as standing on it, in yalms.</summary>
    public float MiniPlanSettleYalms { get; set; } = 2.5f;

    /// <summary>Whether your own spot from the current slide is drawn on the arena floor.</summary>
    public bool ShowArenaSpot { get; set; } = true;

    /// <summary>How big that circle is, in yalms.</summary>
    /// <remarks>
    /// A radius rather than a tolerance: this is the area the plan means, so it is the area drawn
    /// and the area that counts as standing in it. Two numbers for one idea is how they end up
    /// disagreeing.
    /// </remarks>
    public float ArenaSpotYalms { get; set; } = 2f;

    /// <summary>Show the slide's notes under the arena in the mini window.</summary>
    public bool MiniPlanShowNotes { get; set; } = true;

    /// <summary>How many lines of notes the mini window will show before it stops growing.</summary>
    public int MiniPlanNoteLines { get; set; } = 4;

    /// <summary>Keep it draggable during a pull. Off by default so it can't eat a click.</summary>
    public bool MiniPlanUnlocked { get; set; }

    public TeamProfile GetActiveTeam()
    {
        if (Teams.Count == 0)
            Teams.Add(new TeamProfile());

        foreach (var team in Teams)
        {
            if (team.Id == ActiveTeamId)
                return team;
        }

        ActiveTeamId = Teams[0].Id;
        return Teams[0];
    }
}
