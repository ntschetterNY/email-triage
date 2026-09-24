using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class SnoozePresetsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 3, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void All_presets_are_in_the_future()
    {
        foreach (var option in SnoozePresets.For(Now))
            Assert.True(option.When > Now, $"{option.Label} is not in the future");
    }

    [Fact]
    public void Evening_preset_disappears_once_the_evening_has_passed()
    {
        var lateNight = new DateTimeOffset(2026, 3, 11, 23, 0, 0, TimeSpan.Zero);
        var labels = SnoozePresets.For(lateNight).Select(o => o.Label);

        Assert.DoesNotContain("This evening", labels);
    }

    [Fact]
    public void Offers_a_usable_set_of_choices()
    {
        var options = SnoozePresets.For(Now);

        Assert.NotEmpty(options);
        Assert.Contains(options, o => o.Label == "Tomorrow morning");
        Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.Hint)));
    }

    [Fact]
    public void Next_week_lands_on_the_configured_week_start()
    {
        var option = SnoozePresets.For(Now).Single(o => o.Label == "Next week");
        Assert.Equal(DayOfWeek.Monday, option.When.DayOfWeek);
    }
}
