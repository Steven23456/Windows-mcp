using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-2 (R2): the pure guard behind <c>registry_delete</c>'s key branch. A model that types
/// <c>registry_delete("HKCU", "Software", recursive:true, confirm:true)</c> loses the profile, so
/// the denylist is checked before the service is ever touched — and it is checked on a
/// <b>normalised</b> path, because <c>software/</c>, <c>SOFTWARE\</c> and <c>Software\\</c> are
/// the same key to Windows and would each slip past a naive string compare.
/// </summary>
[Trait("Category", "Unit")]
public class RegistryGuardTests
{
    /// <summary>
    /// The spellings the note's normalisation has to collapse: case, <c>/</c> for <c>\</c>, a
    /// leading or trailing separator, a doubled separator, surrounding whitespace.
    /// </summary>
    private static string[] Variants(string root) =>
    [
        root,
        root.ToUpperInvariant(),
        root.ToLowerInvariant(),
        root.Replace('\\', '/'),
        root + @"\",
        root + "/",
        @"\" + root,
        root.Replace(@"\", @"\\"),
        "  " + root + "  ",
    ];

    [Theory]
    [InlineData(@"Software")]
    [InlineData(@"Software\Classes")]
    [InlineData(@"Software\Microsoft")]
    [InlineData(@"Software\Microsoft\Windows")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion")]
    [InlineData(@"Software\Microsoft\Windows NT")]
    [InlineData(@"Software\Microsoft\Windows NT\CurrentVersion")]
    [InlineData(@"Software\Policies")]
    [InlineData(@"Software\WOW6432Node")]
    [InlineData(@"System")]
    [InlineData(@"SYSTEM\CurrentControlSet")]
    [InlineData(@"SAM")]
    [InlineData(@"SECURITY")]
    [InlineData(@"Environment")]
    [InlineData(@"Control Panel")]
    [InlineData(@"Volatile Environment")]
    public void Every_denylisted_root_is_refused_however_it_is_written(string root)
    {
        foreach (var variant in Variants(root))
            RegistryGuard.Refusal(variant).Should().NotBeNullOrWhiteSpace(
                "'{0}' is the denylisted root '{1}' written differently", variant, root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\")]
    [InlineData("/")]
    public void A_hive_root_is_refused(string path)
    {
        // An empty path is the hive itself; deleting it is never what the caller meant.
        RegistryGuard.Refusal(path).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_refusal_names_the_path_it_refused()
    {
        // The tool surfaces this string to the model; a refusal that does not say what was refused
        // costs a round trip to find out.
        var refusal = RegistryGuard.Refusal(@"software\microsoft\windows");

        refusal.Should().NotBeNull();
        refusal!.Should().ContainEquivalentOf("windows");
    }

    [Theory]
    [InlineData(@"Software\MyApp")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Run\Thing")]
    [InlineData(@"Software\WindowsMcpTests\abc123")]
    [InlineData(@"Software\Classes\AppUserModelId\Windows-MCP")]
    [InlineData(@"Environment\Sub")]
    [InlineData(@"Control Panel\Desktop")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\MyService")]
    public void A_key_below_a_guarded_root_is_allowed(string path)
    {
        // The list guards the catastrophic roots, not every unwise delete: confirm: true and the
        // client's destructiveHint do the rest.
        RegistryGuard.Refusal(path).Should().BeNull();
    }

    [Theory]
    [InlineData(@"SoftwareFoo")]
    [InlineData(@"Software\MicrosoftEdge")]
    [InlineData(@"Systems")]
    [InlineData(@"SAMPLES")]
    [InlineData(@"Control Panels")]
    public void A_name_that_merely_starts_with_a_guarded_root_is_allowed(string path)
    {
        // The comparison is per path segment, not StartsWith: 'SoftwareFoo' is somebody's key.
        RegistryGuard.Refusal(path).Should().BeNull();
    }

    /// <summary>
    /// The guard's defensive branch. 'path' is non-nullable, but the guard is the last thing
    /// between a model's arguments and a recursive delete, and a null that arrives from a
    /// deserialiser that does not honour nullability must land on the refusal, not on a
    /// NullReferenceException the tool layer would report as an internal error.
    /// </summary>
    [Fact]
    public void A_null_path_is_refused_like_an_empty_one()
    {
        RegistryGuard.Refusal(null!).Should().NotBeNullOrWhiteSpace();
    }
}
