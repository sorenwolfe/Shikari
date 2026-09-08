using System;
using System.Numerics;
using Newtonsoft.Json;
namespace Dalamud.Configuration { public interface IPluginConfiguration { int Version { get; set; } } }
namespace Shikari.Tests
{
    public static class BuddySettingsTests
    {
        static void Check(bool value,string message) { if(!value) throw new Exception(message); }
        public static void Run()
        {
            var old=JsonConvert.DeserializeObject<Configuration>("{\"Version\":1,\"MiniPlanYourView\":true}")!;
            Check(!old.BuddyEnabled && !old.BuddyUnlocked,"Existing installations must not gain an enabled or draggable HUD");
            Check(old.MiniPlanYourView,"Old settings must remain intact");
            old.BuddyEnabled=true; old.BuddyAnchor=new Vector2(.42f,.66f); old.BuddyScale=1.25f; old.BuddyReducedMotion=true;
            var restored=JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(old))!;
            Check(restored.BuddyEnabled && restored.BuddyReducedMotion && restored.BuddyScale==1.25f && restored.BuddyAnchor==old.BuddyAnchor,"Buddy preferences must survive saving and reload");
            restored.BuddyEnabled=false;
            Check(!JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(restored))!.BuddyEnabled,"Disabled buddy stays disabled on reload");
            Console.WriteLine("PASS: real configuration legacy defaults, buddy opt-in, placement, scale, reduced motion and disabling round trip");
        }
    }
}
