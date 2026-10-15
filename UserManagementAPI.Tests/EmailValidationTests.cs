using UserManagementAPI.Models;

namespace UserManagementAPI.Tests;

public class EmailValidationTests
{
    private static readonly StrictEmailAttribute Rule = new();

    [Theory]
    [InlineData("a@example.com")]
    [InlineData("first.last@sub.example.co.uk")]
    [InlineData("a+tag@example.com")]
    [InlineData("o'brien@example.com")]
    [InlineData("a@xn--mnchen-3ya.de")]
    [InlineData("a@my-company.example.com")]
    public void Accepts_ordinary_addresses(string email) => Assert.True(Rule.IsValid(email));

    [Fact]
    public void Accepts_the_maximum_part_lengths()
    {
        Assert.True(Rule.IsValid(new string('a', 64) + "@example.com"));
        Assert.True(Rule.IsValid("a@" + new string('b', 63) + ".com"));
    }

    [Theory]
    [InlineData("a@b")]
    [InlineData("a@b.c")]
    [InlineData("a@example.123")]
    [InlineData("a@1.2.3.4")]
    [InlineData("a@-example.com")]
    [InlineData("a@example-.com")]
    [InlineData("a@exam_ple.com")]
    [InlineData("a@sub..example.com")]
    [InlineData("a@.example.com")]
    [InlineData("a@example.com.")]
    [InlineData(".a@example.com")]
    [InlineData("a.@example.com")]
    [InlineData("a..b@example.com")]
    [InlineData("a@[127.0.0.1]")]
    [InlineData("\"first last\"@example.com")]
    [InlineData("a b@example.com")]
    [InlineData("Bob <a@example.com>")]
    [InlineData("a@b@example.com")]
    [InlineData("ünï@example.com")]
    [InlineData("a@münchen.de")]
    [InlineData("a@example.co​")]
    [InlineData("a​@example.com")]
    [InlineData("a@example.com\n")]
    public void Rejects_malformed_or_non_policy_addresses(string email) => Assert.False(Rule.IsValid(email));

    [Fact]
    public void Rejects_parts_over_the_dns_length_limits()
    {
        Assert.False(Rule.IsValid(new string('a', 65) + "@example.com"));
        Assert.False(Rule.IsValid("a@" + new string('b', 64) + ".com"));
    }

    [Fact]
    public void Rejects_null_and_non_strings()
    {
        Assert.False(Rule.IsValid(null));
        Assert.False(Rule.IsValid(42));
    }
}
