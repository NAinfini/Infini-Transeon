using System.Text.Json;
using System.Text.Json.Nodes;

namespace InfiniTranseon.Core.Profiles;

public sealed class ProfileMigrationException : Exception
{
    public ProfileMigrationException(string message) : base(message) { }
    public ProfileMigrationException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ProfileMigrator
{
    public ProfileDocument Migrate(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            JsonObject root = JsonNode.Parse(json, null, new JsonDocumentOptions
            {
                MaxDepth = 64,
            })?.AsObject() ?? throw new ProfileMigrationException("Profile document must be a JSON object.");
            int version = root["schemaVersion"]?.GetValue<int>() ??
                throw new ProfileMigrationException("Profile document has no integer schemaVersion.");
            if (version > ProfileDocument.CurrentVersion || version < 0)
            {
                throw new ProfileMigrationException($"Profile schema version {version} is not supported.");
            }

            if (version == 0)
            {
                root["targets"] ??= new JsonArray();
                root["hotkeys"] ??= new JsonArray();
                root["history"] ??= JsonSerializer.SerializeToNode(new ProfileHistorySettings(), ProfileJson.Options);
                root["schemaVersion"] = 1;
                version = 1;
            }

            if (version == 1)
            {
                // Group identities are profile-local. A fixed legacy identity is therefore safe
                // and, unlike Guid.NewGuid(), makes the same v1 document migrate identically.
                const string legacyGroupId = "00000000-0000-0000-0000-000000000001";
                root["translationGroups"] ??= new JsonArray
                {
                    new JsonObject
                    {
                        ["translationGroupId"] = legacyGroupId,
                        ["name"] = "Default",
                    },
                };
                root["activeTranslationGroupId"] ??= legacyGroupId;
                if (root["targets"] is JsonArray targets)
                {
                    foreach (JsonNode? targetNode in targets)
                    {
                        if (targetNode is null) continue;
                        var regions = new List<JsonNode?>();
                        if (targetNode["regions"] is JsonArray explicitRegions)
                            regions.AddRange(explicitRegions);
                        if (targetNode["remainingAreaRegion"] is JsonNode remainingArea)
                            regions.Add(remainingArea);
                        foreach (JsonNode? regionNode in regions)
                        {
                            if (regionNode?["translationChannels"] is not JsonArray channels) continue;
                            foreach (JsonNode? channelNode in channels)
                                channelNode?["translationGroupId"] ??= legacyGroupId;
                        }
                    }
                }
                root["schemaVersion"] = 2;
            }

            return ProfileJson.Deserialize(root.ToJsonString(ProfileJson.Options));
        }
        catch (ProfileMigrationException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            throw new ProfileMigrationException("Profile document is corrupt or has invalid field types.", error);
        }
    }
}
