using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
namespace Shikari.Services.WtfDig;

/// <summary>Reads a bounded subset of TypeScript data. No JavaScript runtime, calls or imports.</summary>
public sealed class LiteralData
{
    public const int MaxSourceCharacters = 2 * 1024 * 1024;
    private readonly record struct Token(string Text, bool Quoted = false, bool Dynamic = false);
    private readonly List<Token> tokens;
    private int at;
    private LiteralData(string source) => tokens = Lex(source);
    public static bool IsUnsupported(JToken? value) => value is JObject obj && obj["$unsupported"] != null;
    private static JObject Unsupported(string reason) => new() { ["$unsupported"] = reason };
    private string Current => at < tokens.Count ? tokens[at].Text : "";
    private bool Eat(string text) { if (Current != text) return false; at++; return true; }

    public static Dictionary<string, JToken> Read(string source)
    {
        if (source.Length > MaxSourceCharacters) throw new InvalidDataException("WTFDIG source exceeds the 2 MiB character limit.");
        var reader = new LiteralData(source);
        var raw = new Dictionary<string, JToken>(StringComparer.Ordinal);
        var scope = 0;
        while (reader.at < reader.tokens.Count)
        {
            var token = reader.tokens[reader.at];
            if (token.Quoted) { reader.at++; continue; }
            if (token.Text == "{") scope++;
            if (token.Text == "}") scope--;
            if (scope != 0 || !reader.Eat("const")) { reader.at++; continue; }
            var name = reader.Current; reader.at++;
            while (reader.at < reader.tokens.Count && reader.Current != "=" && reader.Current != ";") reader.at++;
            if (!reader.Eat("=")) continue;
            raw[name] = reader.Value(0);
            if (raw.Count > 4096) throw new InvalidDataException("Too many data declarations.");
        }
        var budget = 250000;
        return raw.ToDictionary(p => p.Key, p => Resolve(p.Value, raw, new HashSet<string>(), 0, ref budget), StringComparer.Ordinal);
    }

    private JToken Value(int depth)
    {
        if (depth > 64) throw new InvalidDataException("Data nesting is too deep.");
        JToken result;
        if (at >= tokens.Count) throw new InvalidDataException("Incomplete data source.");
        var token = tokens[at++];
        if (token.Quoted) result = token.Dynamic ? Unsupported("Interpolated text") : new JValue(token.Text);
        else if (token.Text == "{")
        {
            var obj = new JObject();
            while (at < tokens.Count && Current != "}")
            {
                var key = tokens[at++];
                if (key.Text == "...") obj["$spread" + obj.Count] = Value(depth + 1);
                else if (Eat(":")) obj[key.Text] = Value(depth + 1);
                else if (Current is "," or "}") obj[key.Text] = new JObject { ["$ref"] = key.Text };
                else { SkipExpression(); obj["$unsupported-property"] = Unsupported("Computed property or method"); }
                if (!Eat(",")) break;
            }
            if (!Eat("}")) throw new InvalidDataException("Unsupported or incomplete object in guide source.");
            result = obj;
        }
        else if (token.Text == "[")
        {
            var array = new JArray();
            while (at < tokens.Count && Current != "]")
            {
                array.Add(Eat("...") ? new JObject { ["$spread"] = Value(depth + 1) } : Value(depth + 1));
                if (!Eat(",")) break;
            }
            if (!Eat("]")) throw new InvalidDataException("Incomplete array in guide source.");
            result = array;
        }
        else if (token.Text is "true" or "false") result = new JValue(token.Text == "true");
        else if (token.Text == "null") result = JValue.CreateNull();
        else if (double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) result = new JValue(number);
        else if (token.Text == "-" && double.TryParse(Current, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) { at++; result = new JValue(-number); }
        else if (token.Text.Length > 0 && (char.IsLetter(token.Text[0]) || token.Text[0] is '_' or '$')) result = new JObject { ["$ref"] = token.Text };
        else { at--; SkipExpression(); return Unsupported("Expression"); }

        if (Eat("+"))
        {
            var right = Value(depth + 1);
            result = result.Type == JTokenType.String && right.Type == JTokenType.String ? new JValue((string?)result + (string?)right) : Unsupported("Computed concatenation");
        }
        if (Current == "as" && at + 1 < tokens.Count && tokens[at + 1].Text == "const") at += 2;
        if (Current is not ("," or ";" or "}" or "]" or "")) { SkipExpression(); return Unsupported("Computed expression"); }
        return result;
    }

    private void SkipExpression()
    {
        var depth = 0;
        while (at < tokens.Count)
        {
            var token = tokens[at];
            if (!token.Quoted)
            {
                if (depth == 0 && token.Text is "," or ";" or "}" or "]") return;
                if (token.Text is "(" or "[" or "{") depth++;
                if (token.Text is ")" or "]" or "}") depth--;
            }
            at++;
        }
    }

    private static JToken Resolve(JToken value, Dictionary<string, JToken> raw, HashSet<string> path, int depth, ref int budget)
    {
        if (--budget <= 0 || depth > 64) throw new InvalidDataException("Guide references expand beyond the supported limit.");
        if (value is JObject obj)
        {
            if (obj["$ref"] is JValue reference)
            {
                var name = reference.ToString();
                if (!raw.TryGetValue(name, out var target) || !path.Add(name)) return Unsupported("Unresolved constant: " + name);
                var result = Resolve(target, raw, path, depth + 1, ref budget); path.Remove(name); return result;
            }
            var resolved = new JObject();
            foreach (var p in obj.Properties())
            {
                var child = Resolve(p.Value, raw, path, depth + 1, ref budget);
                if (p.Name.StartsWith("$spread", StringComparison.Ordinal) && child is JObject spread && !IsUnsupported(spread))
                    foreach (var field in spread.Properties()) resolved[field.Name] = field.Value.DeepClone();
                else resolved[p.Name] = child;
            }
            return resolved;
        }
        if (value is JArray array)
        {
            var resolved = new JArray();
            foreach (var item in array)
            {
                if (item is JObject spread && spread["$spread"] is JToken target)
                {
                    var child = Resolve(target, raw, path, depth + 1, ref budget);
                    if (child is JArray items) foreach (var entry in items) resolved.Add(entry);
                    else resolved.Add(Unsupported("Unresolved array spread"));
                }
                else resolved.Add(Resolve(item, raw, path, depth + 1, ref budget));
            }
            return resolved;
        }
        return value.DeepClone();
    }

    private static List<Token> Lex(string source)
    {
        var result = new List<Token>();
        for (var i = 0; i < source.Length;)
        {
            if (result.Count > 250000) throw new InvalidDataException("Guide token limit exceeded.");
            var c = source[i++];
            if (char.IsWhiteSpace(c)) continue;
            if (c == '/' && i < source.Length && source[i] == '/') { while (i < source.Length && source[i] != '\n') i++; continue; }
            if (c == '/' && i < source.Length && source[i] == '*')
            {
                var end = source.IndexOf("*/", i + 1, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException("Unterminated comment."); i = end + 2; continue;
            }
            if (c is '\'' or '"' or '`')
            {
                var text = new StringBuilder(); var closed = false;
                while (i < source.Length)
                {
                    var next = source[i++];
                    if (next == c) { closed = true; break; }
                    if (next == '\\' && i < source.Length)
                    {
                        next = source[i++];
                        if (next is 'u' or 'x')
                        {
                            var count = next == 'u' ? 4 : 2;
                            if (i + count > source.Length || !ushort.TryParse(source.AsSpan(i, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw new InvalidDataException("Unsupported string escape.");
                            text.Append((char)code); i += count; continue;
                        }
                        next = next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => next };
                    }
                    text.Append(next);
                }
                if (!closed) throw new InvalidDataException("Unterminated string.");
                result.Add(new Token(text.ToString(), true, c == '`' && text.ToString().Contains("${"))); continue;
            }
            if (char.IsLetterOrDigit(c) || c is '_' or '$')
            {
                var start = i - 1;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '_' or '$' || (char.IsDigit(c) && source[i] == '.'))) i++;
                result.Add(new Token(source[start..i])); continue;
            }
            if (c == '.' && i + 1 < source.Length && source[i] == '.' && source[i + 1] == '.') { i += 2; result.Add(new Token("...")); }
            else result.Add(new Token(c.ToString()));
        }
        return result;
    }
}
