using System.ComponentModel.DataAnnotations;
using System.Net.Mail;

namespace UserManagementAPI.Models;

public record User(int Id, string FirstName, string LastName, string Email, string Department, DateTime CreatedAt);

[AttributeUsage(AttributeTargets.Property)]
public sealed class StrictEmailAttribute : ValidationAttribute
{
    public StrictEmailAttribute() : base("The {0} field is not a valid e-mail address.") { }

    // Policy: printable ASCII only, no quoted local parts or address literals, DNS-style
    // domain labels, and a TLD of at least two characters that is not all digits.
    public override bool IsValid(object? value) =>
        value is string s
        && s.All(c => c is > ' ' and < '')
        && !s.Contains('"')
        && MailAddress.TryCreate(s, out var parsed)
        && parsed.Address == s
        && IsValidLocalPart(parsed.User)
        && IsValidDomain(parsed.Host);

    private static bool IsValidLocalPart(string local) =>
        local.Length is >= 1 and <= 64 && local[0] != '.' && local[^1] != '.';

    private static bool IsValidDomain(string host)
    {
        var labels = host.Split('.');
        return labels.Length >= 2
            && labels.All(l => l.Length is >= 1 and <= 63
                && l[0] != '-' && l[^1] != '-'
                && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            && labels[^1].Length >= 2
            && !labels[^1].All(char.IsAsciiDigit);
    }
}

public class UserRequest
{
    [Required, StringLength(50)]
    public string? FirstName { get; set; }

    [Required, StringLength(50)]
    public string? LastName { get; set; }

    [Required, StringLength(100), StrictEmail]
    public string? Email { get; set; }

    [Required, StringLength(50)]
    public string? Department { get; set; }

    public void Normalize()
    {
        FirstName = FirstName?.Trim();
        LastName = LastName?.Trim();
        Email = Email?.Trim();
        Department = Department?.Trim();
    }
}
