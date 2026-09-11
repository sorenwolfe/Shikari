using System;
namespace Shikari;
public static class Plugin
{
    public static TestInterface PluginInterface = new();
    public static TestConfig Config = new();
    public static TestLog Log = new();
}
public sealed class TestInterface
{
    public string Directory = "";
    public string GetPluginConfigDirectory() => Directory;
    public void SavePluginConfig(object value) { }
}
public sealed class TestConfig { public string ActivePlanId = ""; }
public sealed class TestLog { public void Error(Exception error, string message, params object[] args) { } }
