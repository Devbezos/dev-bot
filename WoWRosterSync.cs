using DevClient.Data;
using DevClient.Data.WoW;
using DevClient.Data.WoW.WoWAudit;
using DevClient.Data.WoW.WoWUtils;
using System.Net;

public partial class BotService
{
    private static bool HasRosterSyncConfig(RosterSyncSettings? rosterSync) =>
        rosterSync != null
        && rosterSync.Enabled
        && !string.IsNullOrWhiteSpace(rosterSync.WoWAuditToken)
        && !string.IsNullOrWhiteSpace(rosterSync.WoWUtilsGroupId)
        && !string.IsNullOrWhiteSpace(rosterSync.WoWUtilsApiKey);

    private static List<WoWUtilsRosterMember> FindMissingWoWAuditCharacters(
        IEnumerable<WoWUtilsRosterMember> rosterMembers,
        IEnumerable<WoWAuditCharacter> existingCharacters)
    {
        var trackedKeys = existingCharacters
            .Select(c => BuildRosterMemberKey(c.Name, c.Realm))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return rosterMembers
            .Where(member => !trackedKeys.Contains(BuildRosterMemberKey(member.Name, member.Realm)))
            .ToList();
    }

    private static string BuildRosterMemberKey(string name, string realm) =>
        $"{name.Trim()}|{realm.Trim()}";

    public async Task SyncWoWUtilsRosterToWoWAudit()
    {
        var guilds = AppSettings.Guilds.Where(g => HasRosterSyncConfig(g.RosterSync)).ToList();
        if (guilds.Count == 0)
            return;

        LogInfo($"WoWUtilsRosterSync: START {guilds.Count} guild(s) configured");

        foreach (var guild in guilds)
        {
            var rosterSync = guild.RosterSync!;
            try
            {
                var rosterMembers = await _wowUtilsClient.GetRosterMembers(rosterSync.WoWUtilsGroupId!, rosterSync.WoWUtilsApiKey!);
                var existingCharacters = await _wowAuditClient.GetCharacters(guild.Name, rosterSync.WoWAuditToken!);
                var missingMembers = FindMissingWoWAuditCharacters(rosterMembers, existingCharacters);

                var addedCount = 0;
                var failedCount = 0;

                foreach (var member in missingMembers)
                {
                    try
                    {
                        await _wowAuditClient.TrackCharacter(guild.Name, rosterSync.WoWAuditToken!, new WoWAuditTrackCharacterRequest
                        {
                            Character = new WoWAuditTrackCharacterPayload
                            {
                                Name = member.Name,
                                Realm = member.Realm,
                                Spec = member.Spec,
                                Role = NormalizeWoWAuditRole(member.Role),
                                Rank = member.Rank
                            }
                        });
                        addedCount++;
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    {
                        failedCount++;
                        LogWarn($"WoWUtilsRosterSync: WoW Audit hit {(int?)ex.StatusCode} for guild {guild.Name}; stopping this guild's sync for this run");
                        break;
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        LogWarn($"WoWUtilsRosterSync: failed to track {member.Name}-{member.Realm} for guild {guild.Name}: {ex.Message}");
                    }
                }

                LogInfo($"WoWUtilsRosterSync: {guild.Name} - roster {rosterMembers.Count}, tracked {existingCharacters.Count}, missing {missingMembers.Count}, added {addedCount}, failed {failedCount}");
            }
            catch (Exception ex)
            {
                LogError($"WoWUtilsRosterSync: failed for guild {guild.Name}: {ex.Message}");
            }
        }

        LogInfo("WoWUtilsRosterSync: END");
    }
}
