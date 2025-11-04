using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TrailerScoop
{
    public sealed class PluginConfiguration : BasePluginConfiguration
    {
        public string? TmdbApiKey { get; set; }
        public string? PreferredLanguage { get; set; } = "en-US";
        public string? YtDlpPath { get; set; }
        public string? FfmpegPath { get; set; }
        public bool OverwriteExisting { get; set; } = false;
    }

    public class Plugin : BasePlugin<PluginConfiguration>
    {
        public override string Name => "TrailerScoop";
        public override Guid Id => Guid.Parse("2d4a0fa0-4b2b-4d26-9c6b-7b8106e8a5bf");
        public Plugin(IApplicationPaths paths, IXmlSerializer xml) : base(paths, xml) { }
    }
}
