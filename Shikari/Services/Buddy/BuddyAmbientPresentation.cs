using System;

namespace Shikari.Services.Buddy;

public enum BuddyAmbientState { Idle, Sleeping, Welcoming, Focused }
public sealed record BuddyAmbientPresentation(BuddyAmbientState State, DateTime StartedAtUtc);
