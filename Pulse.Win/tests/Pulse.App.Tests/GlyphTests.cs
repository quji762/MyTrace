using Pulse.App;
using Pulse.Core.Providers;
using Xunit;

namespace Pulse.App.Tests;

public class GlyphTests
{
    [Fact]
    public void Every_Provider_Draws_A_Mark()
    {
        foreach (var id in ProviderCatalog.All)
        {
            var geometry = ProviderGlyph.GeometryFor(id);
            Assert.NotNull(geometry);
            Assert.True(geometry!.Bounds.Width > 0, ProviderCatalog.DisplayName(id));
            Assert.True(geometry.Bounds.Height > 0, ProviderCatalog.DisplayName(id));
        }
    }
}
