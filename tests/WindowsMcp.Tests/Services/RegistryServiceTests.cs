using FluentAssertions;
using Microsoft.Win32;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class RegistryServiceTests : IDisposable
{
    private readonly string _ns = $"Software\\WindowsMcp.Tests\\{Guid.NewGuid():N}";
    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_ns); } catch { }
    }

    [Fact]
    public async Task Set_then_Get_roundtrips_string_value()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "TestVal", "hello", "String");
        var v = await svc.GetAsync("HKCU", _ns, "TestVal");
        v.Data.Should().Be("hello");
    }

    [Fact]
    public async Task Get_throws_KeyNotFound_for_missing_path()
    {
        var svc = new RegistryService();
        Func<Task> act = () => svc.GetAsync("HKCU", "Software\\DoesNotExistXYZ123", null);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    /// <summary>
    /// A-12: the mocked <c>VirtualDesktopServiceTests</c> mimic this exact pair of failures, so
    /// the real behaviour is pinned here — a missing <b>key</b> is a KeyNotFoundException (above),
    /// a missing <b>value</b> under a key that exists is an IOException out of GetValueKind, not
    /// a dto with null Data. A mock that got this wrong would let a service that swallows the
    /// wrong exception ship green.
    /// </summary>
    [Fact]
    public async Task Get_throws_IOException_for_a_missing_value_under_an_existing_key()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "Present", "hello", "String");

        Func<Task> act = () => svc.GetAsync("HKCU", _ns, "NoSuchValue");

        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task EnumerateValues_returns_all_values_with_kinds()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "Alpha", "one", "String");
        await svc.SetAsync("HKCU", _ns, "Beta", 42, "DWord");

        var vals = await svc.EnumerateValuesAsync("HKCU", _ns);

        vals.Select(v => v.Name).Should().BeEquivalentTo(new[] { "Alpha", "Beta" });
        vals.Single(v => v.Name == "Beta").Kind.Should().Be("DWord");
    }

    [Fact]
    public async Task EnumerateValues_reads_binary_data_as_byte_array()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "Bin", new byte[] { 3, 0, 0, 0 }, "Binary");

        var vals = await svc.EnumerateValuesAsync("HKCU", _ns);

        vals.Single(v => v.Name == "Bin").Data.Should().BeOfType<byte[]>()
            .Which.Should().Equal((byte)3, (byte)0, (byte)0, (byte)0);
    }

    [Fact]
    public async Task EnumerateValues_returns_empty_for_missing_key()
    {
        var svc = new RegistryService();
        var vals = await svc.EnumerateValuesAsync("HKCU", "Software\\DoesNotExistXYZ123");
        vals.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateSubKeys_returns_child_key_names()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns + "\\Child1", "x", "1", "String");
        await svc.SetAsync("HKCU", _ns + "\\Child2", "x", "1", "String");

        var subs = await svc.EnumerateSubKeysAsync("HKCU", _ns);

        subs.Should().Contain(new[] { "Child1", "Child2" });
    }

    [Fact]
    public async Task EnumerateSubKeys_returns_empty_for_missing_key()
    {
        var svc = new RegistryService();
        var subs = await svc.EnumerateSubKeysAsync("HKCU", "Software\\DoesNotExistXYZ123");
        subs.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateSubKeys_for_empty_path_lists_the_hive_root()
    {
        // The startup report enumerates HKU\<SID> via an empty root path; ensure that works
        // (and does not throw from disposing the predefined base key).
        var svc = new RegistryService();
        var subs = await svc.EnumerateSubKeysAsync("HKCU", "");
        // Registry key names are case-insensitive; the returned casing varies by
        // environment (e.g. "Software" on a typical desktop vs "SOFTWARE" on hosted
        // CI runners), so compare case-insensitively rather than by exact casing.
        subs.Should().Contain(k => string.Equals(k, "Software", StringComparison.OrdinalIgnoreCase));
    }

    // ---- C-2 (R4): the read shape and the deletes, against the real registry -------------------

    /// <summary>
    /// C-2 (R1/R4): one call returns both halves of a key. The mocked tool test proves the tool
    /// forwards; only this proves the enumerators actually produce values and sub-keys together,
    /// and that the sub-key list is the immediate children (not the whole tree).
    /// </summary>
    [Fact]
    public async Task List_returns_the_values_and_the_immediate_sub_keys()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "Alpha", "one", "String");
        await svc.SetAsync("HKCU", _ns, "Beta", 42, "DWord");
        await svc.SetAsync("HKCU", _ns + @"\Child1", "x", "1", "String");
        await svc.SetAsync("HKCU", _ns + @"\Child2\Grandchild", "x", "1", "String");

        var key = await svc.ListAsync("HKCU", _ns);

        key.Path.Should().Be(_ns);
        key.Values.Select(v => v.Name).Should().BeEquivalentTo(new[] { "Alpha", "Beta" });
        key.Values.Single(v => v.Name == "Beta").Kind.Should().Be("DWord");
        key.SubKeys.Should().BeEquivalentTo(new[] { "Child1", "Child2" },
            "sub-keys are the immediate children; Grandchild belongs to Child2's own listing");
    }

    /// <summary>
    /// C-2 (R1): an absent key is an exception here even though the enumerators return empty -
    /// an empty listing and a key that is not there mean different things to the caller.
    /// </summary>
    [Fact]
    public async Task List_throws_KeyNotFound_for_an_absent_key()
    {
        var svc = new RegistryService();

        Func<Task> act = () => svc.ListAsync("HKCU", _ns + @"\NoSuchKey");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task DeleteValue_removes_the_value_and_says_it_existed_only_the_first_time()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "Doomed", "bye", "String");
        await svc.SetAsync("HKCU", _ns, "Keeper", "stay", "String");

        var first = await svc.DeleteValueAsync("HKCU", _ns, "Doomed");
        var again = await svc.DeleteValueAsync("HKCU", _ns, "Doomed");

        first.Should().BeTrue();
        again.Should().BeFalse("deleting what is gone is a no-op, not an error");
        var left = await svc.ListAsync("HKCU", _ns);
        left.Values.Select(v => v.Name).Should().BeEquivalentTo(new[] { "Keeper" });
    }

    [Fact]
    public async Task DeleteValue_under_a_missing_key_reports_existed_false()
    {
        var svc = new RegistryService();

        var existed = await svc.DeleteValueAsync("HKCU", _ns + @"\NoSuchKey", "Whatever");

        existed.Should().BeFalse();
    }

    /// <summary>
    /// C-2 (R3/R4): the refusal that keeps <c>registry_delete</c> from taking a tree the caller
    /// did not ask for, and the message that tells the caller which flag would have allowed it.
    /// </summary>
    [Fact]
    public async Task DeleteKey_without_recursive_refuses_a_key_that_has_sub_keys()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns + @"\Tree\A", "x", "1", "String");

        Func<Task> act = () => svc.DeleteKeyAsync("HKCU", _ns + @"\Tree", recursive: false);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*recursive*");
        (await svc.ListAsync("HKCU", _ns + @"\Tree")).SubKeys.Should().Contain("A",
            "a refused delete must leave the tree exactly as it was");
    }

    [Fact]
    public async Task DeleteKey_with_recursive_removes_the_tree_and_counts_the_descendants()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns + @"\Tree\A\A1", "x", "1", "String");
        await svc.SetAsync("HKCU", _ns + @"\Tree\B", "x", "1", "String");

        var result = await svc.DeleteKeyAsync("HKCU", _ns + @"\Tree", recursive: true);

        result.Existed.Should().BeTrue();
        result.SubKeysRemoved.Should().Be(3, "A, A1 and B went with Tree; Tree itself is not a descendant");
        (await svc.EnumerateSubKeysAsync("HKCU", _ns)).Should().NotContain("Tree");
    }

    [Fact]
    public async Task DeleteKey_of_a_leaf_key_needs_no_recursive()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns + @"\Leaf", "x", "1", "String");

        var result = await svc.DeleteKeyAsync("HKCU", _ns + @"\Leaf", recursive: false);

        result.Existed.Should().BeTrue();
        result.SubKeysRemoved.Should().Be(0);
        (await svc.EnumerateSubKeysAsync("HKCU", _ns)).Should().NotContain("Leaf");
    }

    [Fact]
    public async Task DeleteKey_of_a_missing_key_reports_existed_false()
    {
        var svc = new RegistryService();

        var result = await svc.DeleteKeyAsync("HKCU", _ns + @"\NoSuchKey", recursive: true);

        result.Existed.Should().BeFalse();
        result.SubKeysRemoved.Should().Be(0);
    }

    // ---- C-2 (R1/R4): the edges the coverage pass found -----------------------------------------

    /// <summary>
    /// C-2 (R1): "an empty path lists the hive root". The implementation takes a different branch
    /// for it - the predefined base key instead of an OpenSubKey - and must <b>not</b> dispose that
    /// base key afterwards, so the second call here is the assertion that matters: a disposed
    /// Registry.CurrentUser would take every later registry read in the process down with it.
    /// </summary>
    [Fact]
    public async Task List_on_an_empty_path_lists_the_hive_root_twice_over()
    {
        var svc = new RegistryService();

        var first = await svc.ListAsync("HKCU", "");
        var second = await svc.ListAsync("HKCU", "");

        first.Path.Should().BeEmpty("the listing echoes the path it was asked for");
        foreach (var key in new[] { first, second })
            key.SubKeys.Should().Contain(k => string.Equals(k, "Software", StringComparison.OrdinalIgnoreCase),
                "the hive root's immediate children are the profile's top-level keys");
    }

    /// <summary>
    /// C-2 (deviation): the tool's <c>RegistryGuard</c> refuses a hive root, and the service
    /// refuses it a second time so a direct caller - anything that is not the tool - cannot delete
    /// HKCU itself either.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeleteKey_refuses_a_hive_root_even_without_the_tools_guard(string path)
    {
        var svc = new RegistryService();

        Func<Task> act = () => svc.DeleteKeyAsync("HKCU", path, recursive: true);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*path*");
    }

    /// <summary>
    /// C-2: <c>registry_delete</c>'s description advertises HKCU|HKLM|HKCR|HKU, in both the short
    /// and the long spelling, and the new methods resolve the hive the same way the old ones do.
    /// A read is enough to prove the resolution; nothing is written.
    /// </summary>
    [Theory]
    [InlineData("HKCU")]
    [InlineData("HKEY_CURRENT_USER")]
    [InlineData("hkcu")]
    [InlineData("HKLM")]
    [InlineData("HKEY_LOCAL_MACHINE")]
    [InlineData("HKCR")]
    [InlineData("HKEY_CLASSES_ROOT")]
    [InlineData("HKU")]
    [InlineData("HKEY_USERS")]
    public async Task List_resolves_every_advertised_hive(string hive)
    {
        var svc = new RegistryService();

        var key = await svc.ListAsync(hive, "");

        key.SubKeys.Should().NotBeEmpty("every one of the four hives has top-level keys");
    }

    /// <summary>C-2: an unknown hive is a caller error that names the parameter.</summary>
    [Fact]
    public async Task An_unknown_hive_is_refused_by_the_read_and_by_both_deletes()
    {
        var svc = new RegistryService();

        await ((Func<Task>)(() => svc.ListAsync("HKXX", _ns)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*HKXX*");
        await ((Func<Task>)(() => svc.DeleteValueAsync("HKXX", _ns, "v")))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*HKXX*");
        await ((Func<Task>)(() => svc.DeleteKeyAsync("HKXX", _ns, recursive: false)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*HKXX*");
    }

    /// <summary>
    /// C-2: a cancelled request must not delete anything. The check is the first statement of each
    /// delete, so the key is still there afterwards - proven, not assumed.
    /// </summary>
    [Fact]
    public async Task A_cancelled_token_stops_a_delete_before_the_key_is_touched()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns + @"\Survivor", "x", "1", "String");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await ((Func<Task>)(() => svc.DeleteKeyAsync("HKCU", _ns + @"\Survivor", recursive: true, cts.Token)))
            .Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => svc.DeleteValueAsync("HKCU", _ns + @"\Survivor", "x", cts.Token)))
            .Should().ThrowAsync<OperationCanceledException>();

        var left = await svc.ListAsync("HKCU", _ns + @"\Survivor");
        left.Values.Select(v => v.Name).Should().Contain("x", "a cancelled delete removes nothing");
    }
}
