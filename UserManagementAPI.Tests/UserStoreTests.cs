using UserManagementAPI.Models;
using UserManagementAPI.Services;

namespace UserManagementAPI.Tests;

public class UserStoreTests
{
    private static UserRequest Req(string? first = "A", string? email = "a@b.co") =>
        new() { FirstName = first, LastName = "B", Email = email, Department = "IT" };

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, -5)]
    public void GetPage_rejects_non_positive_arguments(int page, int pageSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UserStore().GetPage(page, pageSize));

    [Fact]
    public void GetPage_with_valid_arguments_still_works()
    {
        var (items, total) = new UserStore().GetPage(1, 1);
        Assert.Single(items);
        Assert.Equal(2, total);
    }

    [Theory]
    [InlineData(null, "a@b.co")]
    [InlineData("  ", "a@b.co")]
    [InlineData("A", null)]
    [InlineData("A", " a@b.co ")]
    [InlineData(" A", "a@b.co")]
    public void Add_and_Update_reject_requests_that_skipped_validation_and_trimming(string? first, string? email)
    {
        var store = new UserStore();
        Assert.Throws<ArgumentException>(() => store.Add(Req(first, email)));
        Assert.Throws<ArgumentException>(() => store.Update(1, Req(first, email)));
        Assert.Equal(2, store.GetPage(1, 100).Total);
    }

    [Fact]
    public void Untrimmed_email_cannot_bypass_the_uniqueness_check()
    {
        var store = new UserStore();
        Assert.Equal(StoreStatus.Ok, store.Add(Req(email: "sp@x.co")).Status);
        Assert.Throws<ArgumentException>(() => store.Add(Req(email: " sp@x.co ")));
        Assert.Equal(StoreStatus.DuplicateEmail, store.Add(Req(email: "SP@x.co")).Status);
    }
}
