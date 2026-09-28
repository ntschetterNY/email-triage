using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class PersonResolverTests
{
    private static readonly Recipient[] Thread =
    {
        new("Sam Lee", "sam@corp.com"),
        new("Alex Morgan", "alex@corp.com"),
        new("Samantha Price", "sprice@vendor.com"),
    };

    [Fact]
    public void Empty_text_resolves_to_nobody()
        => Assert.Null(PersonResolver.Resolve("  ", Thread));

    [Fact]
    public void First_name_finds_the_thread_participant()
        => Assert.Equal("alex@corp.com", PersonResolver.Resolve("alex", Thread)!.Value.Address);

    [Fact]
    public void Ties_go_to_the_earlier_candidate()
        => Assert.Equal("sam@corp.com", PersonResolver.Resolve("sam", Thread)!.Value.Address);

    [Fact]
    public void Exact_name_wins_over_fuzzy()
        => Assert.Equal("sprice@vendor.com",
            PersonResolver.Resolve("samantha price", Thread)!.Value.Address);

    [Fact]
    public void Address_matches_the_known_person()
        => Assert.Equal("Alex Morgan", PersonResolver.Resolve("ALEX@corp.com", Thread)!.Value.Name);

    [Fact]
    public void Unknown_address_is_taken_literally()
        => Assert.Equal(new Recipient("pat@x.com", "pat@x.com"), PersonResolver.Resolve("pat@x.com", Thread));

    [Fact]
    public void Angle_bracket_form_is_parsed()
        => Assert.Equal(new Recipient("Pat Kim", "pat@x.com"),
            PersonResolver.Resolve("Pat Kim <pat@x.com>", Thread));

    [Fact]
    public void Unknown_name_has_no_address()
        => Assert.Equal(new Recipient("Jordan", ""), PersonResolver.Resolve("Jordan", Thread));
}
