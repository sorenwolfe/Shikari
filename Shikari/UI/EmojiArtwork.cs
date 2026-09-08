using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace Shikari.UI;

/// <summary>Bounded references to Dalamud-owned shared emoji textures. Never performs HTTP.</summary>
public static class EmojiArtwork
{
    public const int Capacity = 256;
    private sealed record Entry(string Key, ISharedImmediateTexture? Texture);
    private static readonly Dictionary<string, LinkedListNode<Entry>> Loaded = new(StringComparer.Ordinal);
    private static readonly LinkedList<Entry> Recent = new();

    public static bool TryHandle(string key, out ImTextureID handle)
    {
        handle = default;
        if (!Loaded.TryGetValue(key, out var node))
        {
            ISharedImmediateTexture? texture = null;
            try { texture = Plugin.TextureProvider.GetFromManifestResource(Assembly.GetExecutingAssembly(), "Shikari.Resources.Emoji." + key + ".png"); }
            catch { /* Missing artwork is a cosmetic loss; retain a failed entry rather than retry per frame. */ }
            node = Recent.AddLast(new Entry(key, texture));
            Loaded[key] = node;
            if (Loaded.Count > Capacity)
            {
                var first = Recent.First!;
                Loaded.Remove(first.Value.Key);
                Recent.RemoveFirst();
            }
        }
        else { Recent.Remove(node); Recent.AddLast(node); }
        try
        {
            if (node.Value.Texture == null || !node.Value.Texture.TryGetWrap(out var wrap, out _) || wrap == null) return false;
            handle = wrap.Handle;
            return true;
        }
        catch { node.Value = new Entry(key, null); return false; }
    }

    public static void Forget() { Loaded.Clear(); Recent.Clear(); }
}
