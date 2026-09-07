using System;
using System.Collections.Generic;
using System.Numerics;
using Shikari.Model;
namespace Shikari {
 public static class Plugin {
  public static TestInterface PluginInterface = new(); public static TestConfig Config = new(); public static TestLog Log = new();
  public static TestObjects ObjectTable = new(); public static TestMember[] PartyList = Array.Empty<TestMember>(); public static TestActions Actions = new();
 }
 public class TestInterface { public string Directory = ""; public string GetPluginConfigDirectory() => Directory; public void SavePluginConfig(object value) {} }
 public class TestConfig { public string ActivePlanId = ""; public TestTeam Team = new(); public TestTeam GetActiveTeam() => Team; }
 public class TestTeam { public int PinnedSlotIndex = -1; }
 public class TestLog { public void Error(Exception e, string message, params object[] args) {} public void Warning(Exception e, string message, params object[] args) {} }
 public class TestObjects { public TestMember? LocalPlayer; }
 public class TestMember { public TestName Name = new(); public TestJob ClassJob = new(); public Vector3 Position; }
 public class TestName { public string TextValue = ""; } public class TestJob { public uint RowId; }
 public class TestActions { public string JobAbbreviation(uint id) => ""; }
}
namespace Shikari.Services { public static class JobRoles { public static RaidRole RoleFor(string text) => RaidRole.Unknown; } }
namespace Shikari.Services.Live {
 public static class FieldMarkers {
  public readonly record struct PlacedMarker(string Letter, Vector2 World);
  public static List<PlacedMarker> Read() => new() { new("A",new(0,0)),new("B",new(1,0)),new("C",new(0,1)) };
 }
 public static class ReliabilityLiveTest {
  public static void Run() {
   var plan = PlanDocument.CreateDefault();
   plan.Roster[0].JobId = 10; plan.Roster[1].JobId = 20;
   foreach(var marker in FieldMarkers.Read()) plan.Slides[0].Items.Add(new CanvasItem { Kind=CanvasItemKind.Waymark,Text=marker.Letter,Position=marker.World });
   var local = new TestMember(); local.Name.TextValue = "Local"; local.ClassJob.RowId=10;
   var other = new TestMember(); other.Name.TextValue = "Other"; other.ClassJob.RowId=20;
   Plugin.ObjectTable.LocalPlayer=local; Plugin.PartyList=new[] {other,local}; Plugin.Config.Team.PinnedSlotIndex=1;
   var tracker = new ArenaTracker(); var players = tracker.Read(plan,plan.Slides[0]);
   if(players.Count != 2 || players[1].SlotIndex != 1) throw new Exception("Arena reader ignores local pinned seat");
   if(players[0].SlotIndex != -1) throw new Exception("Another player duplicates the pinned seat");
   Plugin.Config.Team.PinnedSlotIndex=-1;
   players=tracker.Read(plan,plan.Slides[0]);
   if(players[0].SlotIndex != 1 || players[1].SlotIndex != 0) throw new Exception("Unpinned unique jobs no longer resolve");
   var authored = plan.Slides[0];
   authored.SourceUrl = "https://raidplan.io/plan/board-one#1";
   var otherBoard = new Slide { SourceUrl = "https://raidplan.io/plan/board-two#1" };
   plan.Slides.Add(otherBoard);
   if(new ArenaTracker().TryAlign(plan, otherBoard, out _)) throw new Exception("Alignment borrowed waymarks from another imported board");
   otherBoard.SourceUrl = "https://raidplan.io/plan/board-one#2";
   if(!new ArenaTracker().TryAlign(plan, otherBoard, out _)) throw new Exception("Steps of the same source board cannot share waymarks");
   otherBoard.SourceUrl = "";
   if(new ArenaTracker().TryAlign(plan, otherBoard, out _)) throw new Exception("Guide-only slide borrowed an imported board's coordinates");
   authored.SourceUrl = "";
   if(!new ArenaTracker().TryAlign(plan, otherBoard, out _)) throw new Exception("Legacy plan waymark fallback was lost");
   otherBoard.ArenaOverride = new ArenaSettings();
   if(new ArenaTracker().TryAlign(plan, otherBoard, out _)) throw new Exception("An unidentified arena override borrowed legacy waymarks");
   otherBoard.ArenaOverride = null;
   tracker = new ArenaTracker();
   if(!tracker.TryAlign(plan, otherBoard, out _)) throw new Exception("Legacy fixture did not align");
   otherBoard.SourceUrl = "https://raidplan.io/plan/changed-board";
   if(tracker.TryAlign(plan, otherBoard, out _)) throw new Exception("Cached alignment survived a source-board change");
   otherBoard.SourceUrl = "";
   tracker = new ArenaTracker();
   if(!tracker.TryAlign(plan, otherBoard, out _)) throw new Exception("Legacy fixture did not align");
   otherBoard.ArenaOverride = new ArenaSettings();
   if(tracker.TryAlign(plan, otherBoard, out _)) throw new Exception("Cached alignment survived a newly isolated board");
   otherBoard.ArenaOverride = null;
   tracker = new ArenaTracker(); tracker.TryAlign(plan, otherBoard, out _);
   var unrelated = PlanDocument.CreateDefault(); unrelated.Slides[0].Id = otherBoard.Id;
   if(tracker.TryAlign(unrelated, unrelated.Slides[0], out _)) throw new Exception("Cached alignment leaked between independent plan objects with the same slide id");
   Console.WriteLine("PASS: source-board alignment isolation, same-board fragment fallback, legacy fallback and cache invalidation");
  }
 }
}
