using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

public class ContactLimitsTests
{
    [Fact]
    public void Validate_NormalContact_ReturnsNull()
    {
        var c = new Contact
        {
            Name = "Alice",
            Email = "alice@example.com",
            Phone = "+1-555-0100",
            Notes = "met at conference",
        };
        Assert.Null(ContactLimits.Validate(c));
    }

    [Fact]
    public void Validate_OversizedName_ReturnsNameError()
    {
        var c = new Contact { Name = new string('x', ContactLimits.MaxNameLength + 1) };
        var err = ContactLimits.Validate(c);
        Assert.NotNull(err);
        Assert.Equal("contact", err!.Resource);
        Assert.Equal("name", err.Field);
    }

    [Fact]
    public void Validate_OversizedEmail_ReturnsEmailError()
    {
        var c = new Contact { Name = "ok", Email = new string('e', ContactLimits.MaxEmailLength + 1) };
        var err = ContactLimits.Validate(c);
        Assert.NotNull(err);
        Assert.Equal("email", err!.Field);
    }

    [Fact]
    public void Validate_OversizedPhone_ReturnsPhoneError()
    {
        var c = new Contact { Name = "ok", Phone = new string('1', ContactLimits.MaxPhoneLength + 1) };
        var err = ContactLimits.Validate(c);
        Assert.NotNull(err);
        Assert.Equal("phone", err!.Field);
    }

    [Fact]
    public void Validate_OversizedNotes_ReturnsNotesError()
    {
        var c = new Contact { Name = "ok", Notes = new string('n', ContactLimits.MaxNotesLength + 1) };
        var err = ContactLimits.Validate(c);
        Assert.NotNull(err);
        Assert.Equal("notes", err!.Field);
    }

    [Fact]
    public void Validate_NullOptionalFields_AreAllowed()
    {
        var c = new Contact { Name = "Bob", Email = null, Phone = null, Notes = null };
        Assert.Null(ContactLimits.Validate(c));
    }
}
