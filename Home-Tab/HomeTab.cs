using Blish_HUD;
using Blish_HUD.Common.UI.Views;
using Blish_HUD.Controls;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using static Blish_HUD.GameService;

namespace entrhopi.HomeTab
{
    [Export(typeof(Blish_HUD.Modules.Module))]
    public class Module : Blish_HUD.Modules.Module
    {

        private const string NewModulesFeedUrl = "https://pkgs.blishhud.com/new-modules.xml";
        private const string ModuleUpdatesFeedUrl = "https://pkgs.blishhud.com/module-updates.xml";
        private const int PanelGap = 8;

        private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";
        private static readonly Regex HtmlTagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        private static readonly Logger Logger = Logger.GetLogger<Module>();

        #region Service Managers
        internal SettingsManager SettingsManager => this.ModuleParameters.SettingsManager;
        internal ContentsManager ContentsManager => this.ModuleParameters.ContentsManager;
        internal DirectoriesManager DirectoriesManager => this.ModuleParameters.DirectoriesManager;
        internal Gw2ApiManager Gw2ApiManager => this.ModuleParameters.Gw2ApiManager;
        #endregion

        private WindowTab _moduleTab;

        private Texture2D _homeIcon;

        private readonly List<RssItem> _newModules = new List<RssItem>();
        private readonly List<RssItem> _moduleUpdates = new List<RssItem>();

        internal string TabName = "Home";

        [ImportingConstructor]
        public Module([Import("ModuleParameters")] ModuleParameters moduleParameters) : base(moduleParameters) { }

        protected override void DefineSettings(SettingCollection settings)
        {

        }

        protected override void Initialize()
        {
            _homeIcon = ContentsManager.GetTexture("255369.png");
        }

        protected override async Task LoadAsync()
        {
            await Task.WhenAll(
                LoadFeed(NewModulesFeedUrl, _newModules),
                LoadFeed(ModuleUpdatesFeedUrl, _moduleUpdates)).ConfigureAwait(false);
        }

        private static async Task LoadFeed(string url, List<RssItem> target)
        {
            try
            {
                using (var client = new WebClient())
                {
                    var xml = await client.DownloadStringTaskAsync(url).ConfigureAwait(false);
                    ParseFeed(xml, url, target);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to load RSS feed from {url}.", url);
            }
        }

        protected override void OnModuleLoaded(EventArgs e)
        {
            _moduleTab = Overlay.BlishHudWindow.AddTab(
                TabName,
                _homeIcon,
                () => new StaticPanelView(HomeTabView(Overlay.BlishHudWindow)),
                int.MinValue);

            // Base handler must be called
            base.OnModuleLoaded(e);
        }

        private static void ParseFeed(string xml, string url, List<RssItem> target)
        {
            var channel = XDocument.Parse(xml)?.Root?.Element("channel");
            if (channel == null)
            {
                Logger.Warn("RSS feed from {url} did not contain a channel.", url);
                return;
            }

            target.Clear();
            target.AddRange(channel.Elements("item").Select(item => new RssItem()
            {
                Title       = ElementValue(item, "title"),
                Link        = ElementValue(item, "link"),
                Published   = ElementValue(item, "pubDate"),
                Author      = ElementValue(item, DcNamespace + "creator"),
                Description = ToPlainText(ElementValue(item, "description"))
            }));
        }

        private static string ElementValue(XElement parent, XName name)
        {
            return parent.Element(name)?.Value.Trim() ?? string.Empty;
        }

        private static string ToPlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;

            var text = WebUtility.HtmlDecode(html);
            text = HtmlTagRegex.Replace(text, string.Empty);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            return text.Trim();
        }

        private Panel HomeTabView(WindowBase wndw)
        {
            var parentPanel = new Panel()
            {
                CanScroll = false,
                Size = wndw.ContentRegion.Size
            };

            var newModulesPanel = new Panel()
            {
                Parent           = parentPanel,
                Location         = Point.Zero,
                HeightSizingMode = SizingMode.Fill
            };

            var moduleUpdatesPanel = new Panel()
            {
                Parent           = parentPanel,
                HeightSizingMode = SizingMode.Fill
            };

            parentPanel.Resized += (sender, args) => UpdateColumnLayout(parentPanel, newModulesPanel, moduleUpdatesPanel);
            UpdateColumnLayout(parentPanel, newModulesPanel, moduleUpdatesPanel);

            BuildFeedList(newModulesPanel, _newModules);
            BuildFeedList(moduleUpdatesPanel, _moduleUpdates);

            return parentPanel;
        }

        private static void UpdateColumnLayout(Panel parent, Panel left, Panel right)
        {
            var leftWidth = Math.Max(0, (parent.ContentRegion.Width - PanelGap) / 2);

            left.Width = leftWidth;
            right.Location = new Point(leftWidth + PanelGap, 0);
            right.Width = Math.Max(0, parent.ContentRegion.Width - right.Left);
        }

        private static void BuildFeedList(Panel parent, IEnumerable<RssItem> items)
        {
            var feedList = new FlowPanel()
            {
                Parent              = parent,
                Location            = Point.Zero,
                WidthSizingMode     = SizingMode.Fill,
                HeightSizingMode    = SizingMode.Fill,
                CanScroll           = true,
                FlowDirection       = ControlFlowDirection.SingleTopToBottom,
                ControlPadding      = new Vector2(0, 10),
                OuterControlPadding = new Vector2(10, 10)
            };

            foreach (var item in items)
            {
                var lines = new List<string>();

                if (!string.IsNullOrWhiteSpace(item.Title)) lines.Add(item.Title);

                var meta = string.Join("  •  ", new[] { item.Published, item.Author }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                if (!string.IsNullOrWhiteSpace(meta)) lines.Add(meta);

                if (!string.IsNullOrWhiteSpace(item.Description)) lines.Add(item.Description);
                if (!string.IsNullOrWhiteSpace(item.Link)) lines.Add(item.Link);

                var itemPanel = new Panel()
                {
                    Parent           = feedList,
                    WidthSizingMode  = SizingMode.Fill,
                    HeightSizingMode = SizingMode.AutoSize
                };

                var itemLabel = new Label()
                {
                    Parent         = itemPanel,
                    Location       = Point.Zero,
                    Text           = string.Join("\n", lines),
                    WrapText       = true,
                    AutoSizeWidth  = false,
                    AutoSizeHeight = true,
                    TextColor      = Color.White
                };

                itemPanel.Resized += (sender, args) => itemLabel.Width = itemPanel.ContentRegion.Width;
                itemLabel.Width = itemPanel.ContentRegion.Width;
            }
        }

        protected override void Update(GameTime gameTime)
        {

        }

        /// <inheritdoc />
        protected override void Unload()
        {
            // Unload here

            // All static members must be manually unset
        }

        private sealed class RssItem
        {
            public string Title { get; set; }
            public string Link { get; set; }
            public string Published { get; set; }
            public string Author { get; set; }
            public string Description { get; set; }
        }

    }

}
