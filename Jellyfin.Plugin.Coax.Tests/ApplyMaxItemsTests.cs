using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Coax.Indexing;
using Jellyfin.Plugin.Coax.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.Coax.Tests;

/// <summary>
/// Unit tests for <see cref="CoaxIndexBuilder.ApplyMaxItems"/>, the global item cap that also
/// enforces <see cref="CoaxContract.DefaultMaxItemsCeiling"/> as an unconditional backstop.
/// </summary>
public class ApplyMaxItemsTests
{
    private static List<BaseItem> MakeItems(int count) =>
        Enumerable.Range(0, count).Select(_ => (BaseItem)new Movie()).ToList();

    [Fact]
    public void UnderClientCap_PassesThroughUntouched()
    {
        var items = MakeItems(5);
        var truncated = false;

        var result = CoaxIndexBuilder.ApplyMaxItems(items, maxItems: 10, ref truncated);

        Assert.Same(items, result);
        Assert.Equal(5, result.Count);
        Assert.False(truncated);
    }

    [Fact]
    public void AboveClientCap_ClampsToCapAndFlagsTruncated()
    {
        var items = MakeItems(20);
        var truncated = false;

        var result = CoaxIndexBuilder.ApplyMaxItems(items, maxItems: 8, ref truncated);

        Assert.Equal(8, result.Count);
        Assert.True(truncated);
    }

    [Fact]
    public void NoClientCap_ClampsToCeiling()
    {
        var items = MakeItems(CoaxContract.DefaultMaxItemsCeiling + 50);
        var truncated = false;

        var result = CoaxIndexBuilder.ApplyMaxItems(items, maxItems: null, ref truncated);

        Assert.Equal(CoaxContract.DefaultMaxItemsCeiling, result.Count);
        Assert.True(truncated);
    }

    [Fact]
    public void NoClientCap_UnderCeiling_PassesThroughUntouched()
    {
        var items = MakeItems(CoaxContract.DefaultMaxItemsCeiling - 1);
        var truncated = false;

        var result = CoaxIndexBuilder.ApplyMaxItems(items, maxItems: null, ref truncated);

        Assert.Same(items, result);
        Assert.False(truncated);
    }

    [Fact]
    public void ClientCapAboveCeiling_ClampsToCeiling()
    {
        var items = MakeItems(CoaxContract.DefaultMaxItemsCeiling + 100);
        var truncated = false;

        var result = CoaxIndexBuilder.ApplyMaxItems(
            items,
            maxItems: CoaxContract.DefaultMaxItemsCeiling * 5,
            ref truncated);

        Assert.Equal(CoaxContract.DefaultMaxItemsCeiling, result.Count);
        Assert.True(truncated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveClientCap_TreatedAsNoCap_ClampsToCeiling(int cap)
    {
        var items = MakeItems(CoaxContract.DefaultMaxItemsCeiling + 10);
        var truncated = false;

        var result = CoaxIndexBuilder.ApplyMaxItems(items, cap, ref truncated);

        Assert.Equal(CoaxContract.DefaultMaxItemsCeiling, result.Count);
        Assert.True(truncated);
    }
}
