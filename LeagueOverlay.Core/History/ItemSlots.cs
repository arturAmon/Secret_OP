using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.History;

/// <summary>
/// A player's inventory in a fixed layout: six item slots (0 = empty) and, for ADCs, the extra boots slot from the
/// bot lane role quest. Vision items (trinkets, wards, control wards) are left out: they're shown as empty.
/// </summary>
public sealed record ItemSlots(int[] Regular, int? Boots)
{
    public const int RegularCount = 6;
    /// <summary>Stored layout of RecordedPlayer.Items: index = slot, 0-5 items, 6 trinket, 7 role-quest boots.</summary>
    public const int StoredLength = 8, TrinketIndex = 6, RoleIndex = 7;

    public static bool HasBootsSlot(string? position) => string.Equals(position, "BOTTOM", StringComparison.OrdinalIgnoreCase);

    public static bool IsVision(int id, ItemCatalog items) =>
        items.Get(id) is { } info && (info.Tags.Contains("Trinket") || (info.Tags.Contains("Vision") && info.Tags.Contains("Consumable")));

    public static ItemSlots Of(RecordedPlayer p, ItemCatalog items)
    {
        var regular = new int[RegularCount];
        int roleBoots = 0;
        if (p.Items.Length == StoredLength)
        {
            for (int i = 0; i < RegularCount; i++) regular[i] = p.Items[i];
            roleBoots = p.Items[RoleIndex];
        }
        else
        {
            // Saved before slots were kept: items in order, trinket among them.
            int next = 0;
            foreach (var id in p.Items.Where(id => !IsVision(id, items)).Take(RegularCount)) regular[next++] = id;
        }
        for (int i = 0; i < RegularCount; i++) if (regular[i] != 0 && IsVision(regular[i], items)) regular[i] = 0;
        return new ItemSlots(regular, HasBootsSlot(p.Position) ? roleBoots : null);
    }

    /// <summary>
    /// Live inventory to the stored layout. Slots 0-5 are kept; the trinket goes to 6; boots outside 0-5 (the role-quest
    /// slot, whatever number the client gives it) go to 7; anything else lands in the first free item slot.
    /// </summary>
    public static int[] FromLive(IEnumerable<PlayerItem> inventory, ItemCatalog items)
    {
        var slots = new int[StoredLength];
        var leftovers = new List<int>();
        foreach (var item in inventory.OrderBy(i => i.Slot))
        {
            bool trinket = items.Get(item.ItemID)?.Tags.Contains("Trinket") == true;
            if (item.Slot is >= 0 and < RegularCount && slots[item.Slot] == 0 && !trinket) slots[item.Slot] = item.ItemID;
            else if (trinket && slots[TrinketIndex] == 0) slots[TrinketIndex] = item.ItemID;
            else if (item.Slot >= RegularCount && slots[RoleIndex] == 0 && items.IsUpgradedBoots(item)) slots[RoleIndex] = item.ItemID;
            else leftovers.Add(item.ItemID);
        }
        foreach (var id in leftovers)
        {
            int free = Array.IndexOf(slots, 0, 0, RegularCount);
            if (free >= 0) slots[free] = id;
        }
        return slots;
    }
}
