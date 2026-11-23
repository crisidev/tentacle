using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Tentacle.Configuration;
using Tentacle.Broker;
using Xunit;

namespace Jellyfin.Plugin.Tentacle.Tests;

/// <summary>
/// The plugin's settings as Jellyfin stores them: an XML file read and written with
/// XmlSerializer.
/// </summary>
public class ConfigurationTests
{
    private static PluginConfiguration Load(string xml)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return (PluginConfiguration)serializer.Deserialize(reader)!;
    }

    private static string Save(PluginConfiguration config)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        return writer.ToString();
    }

    [Fact]
    public void TheEarlierOnSwitchBecomesShadowNotActive()
    {
        var config = Load("<?xml version=\"1.0\"?><PluginConfiguration><Placement>Active</Placement><CrossVendor>true</CrossVendor></PluginConfiguration>");
        Assert.True(config.CrossVendor);
        Assert.True(config.MigrateLegacy());
        Assert.Equal(PlacementMode.DryRun, config.CrossVendorMode);
        Assert.Equal(PlacementMode.Active, config.Placement);

        // Saved once in the new form; the old element is gone and stays gone.
        var saved = Save(config);
        Assert.Contains("<CrossVendorMode>DryRun</CrossVendorMode>", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("<CrossVendor>", saved, StringComparison.Ordinal);
        var again = Load(saved);
        Assert.False(again.MigrateLegacy());
        Assert.Equal(PlacementMode.DryRun, again.CrossVendorMode);
    }

    [Fact]
    public void OffStaysOffAndANewSettingIsKept()
    {
        var off = Load("<?xml version=\"1.0\"?><PluginConfiguration><CrossVendor>false</CrossVendor></PluginConfiguration>");
        Assert.False(off.MigrateLegacy());
        Assert.Equal(PlacementMode.Disabled, off.CrossVendorMode);

        // A file with no cross-vendor setting at all (2.0.x): off.
        var old = Load("<?xml version=\"1.0\"?><PluginConfiguration><Placement>Active</Placement></PluginConfiguration>");
        Assert.False(old.MigrateLegacy());
        Assert.Equal(PlacementMode.Disabled, old.CrossVendorMode);

        // Both present (the new one already chosen): the new one wins.
        var both = Load("<?xml version=\"1.0\"?><PluginConfiguration><CrossVendorMode>Active</CrossVendorMode><CrossVendor>true</CrossVendor></PluginConfiguration>");
        Assert.True(both.MigrateLegacy());
        Assert.Equal(PlacementMode.Active, both.CrossVendorMode);
    }

    [Fact]
    public void TheDashboardNeverSeesTheOldSwitch()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new PluginConfiguration { CrossVendor = true, CrossVendorMode = PlacementMode.DryRun });
        Assert.DoesNotContain("\"CrossVendor\"", json, StringComparison.Ordinal);
        Assert.Contains("\"CrossVendorMode\"", json, StringComparison.Ordinal);
    }
}
