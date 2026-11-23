using LeagueOverlay.Core.History;
using LeagueOverlay.Core.Live;
using static LeagueOverlay.Tests.Fixtures;

namespace LeagueOverlay.Tests;

public class ItemSlotTests
{
    private static RecordedPlayer P(string position, int[] items) => new("A#1", "Jinx", "Jinx", position, true, true, 10, 0, 0, 0, 0, 0, items);

    [Fact]
    public void Live_inventory_keeps_slots_and_moves_trinket_and_role_boots()
    {
        var inventory = new[]
        {
            new PlayerItem { ItemID = 3031, Slot = 0 }, new PlayerItem { ItemID = 3071, Slot = 3 },
            new PlayerItem { ItemID = 3340, Slot = 6 }, new PlayerItem { ItemID = 3172, Slot = 7 }, // trinket, role-quest boots
        };
        Assert.Equal([3031, 0, 0, 3071, 0, 0, 3340, 3172], ItemSlots.FromLive(inventory, Data.Items));
    }

    [Fact]
    public void Display_has_six_slots_hides_vision_and_adds_boots_slot_for_adc_only()
    {
        int[] stored = [3031, 0, 2003, 3071, 0, 3363, 3340, 3172]; // a Farsight in slot 5 is hidden too
        var adc = ItemSlots.Of(P("BOTTOM", stored), Data.Items);
        Assert.Equal([3031, 0, 2003, 3071, 0, 0], adc.Regular);
        Assert.Equal(3172, adc.Boots);

        var mid = ItemSlots.Of(P("MIDDLE", stored), Data.Items);
        Assert.Null(mid.Boots);

        var adcNoQuest = ItemSlots.Of(P("BOTTOM", [3031, 3006, 0, 0, 0, 0, 3340, 0]), Data.Items);
        Assert.Equal(0, adcNoQuest.Boots); // shown as an empty boots slot
    }

    [Fact]
    public void Games_saved_before_slots_were_kept_still_show_six_slots()
    {
        var old = ItemSlots.Of(P("TOP", [3031, 3340, 3071]), Data.Items);
        Assert.Equal([3031, 3071, 0, 0, 0, 0], old.Regular);
    }
}
