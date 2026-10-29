using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Drone.Engine;

namespace Drone.Server.Wire;

/// <summary>JSON for the browser: camelCase names, enums as their names, and game events as their
/// fields plus "t", the type name without "Event".</summary>
public static class EventWire
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        return o;
    }

    public static JsonObject ToJson(GameEvent e)
    {
        var node = JsonSerializer.SerializeToNode(e, e.GetType(), Options) as JsonObject ?? new JsonObject();
        string name = e.GetType().Name;
        node["t"] = name.EndsWith("Event") ? name[..^5] : name;
        return node;
    }

    /// <summary>A view as JSON (its runtime type, so the phase's own fields go out too).</summary>
    public static JsonNode? ToJson(object? view) => view == null ? null : JsonSerializer.SerializeToNode(view, view.GetType(), Options);
}
